from __future__ import annotations

import unittest
import threading
from pathlib import Path
from tempfile import TemporaryDirectory
from types import SimpleNamespace
from unittest.mock import call, patch

from core import quickserver_assets as assets
from core import kimodo_runtime
from core import quickserver_cli
from core.quickserver_cli import _is_accelerator_oom, _is_encoder_oom, _normalize_runtime_config


class TextEncoderRuntimeDecisionTests(unittest.TestCase):
    def resolve(self, mode, vram, *, device="cuda:0", nf4=True, int8=True, fp16=True):
        return assets.resolve_text_encoder_runtime(
            mode,
            device,
            vram,
            nf4_available=nf4,
            int8_accelerator_available=int8,
            fp16_accelerator_available=fp16,
        )

    def test_high_precision_uses_fp16_gpu_when_remaining_budget_fits(self):
        gpu = self.resolve("high_precision", assets.FP16_ENCODER_MIN_FREE_GB)
        cpu = self.resolve("high_precision", assets.FP16_ENCODER_MIN_FREE_GB - 0.1)
        self.assertEqual((gpu.motion_device, gpu.encoder_route, gpu.encoder_device), ("cuda:0", "fp16", "cuda:0"))
        self.assertEqual((cpu.motion_device, cpu.encoder_route, cpu.encoder_device), ("cuda:0", "fp16", "cpu"))

    def test_high_performance_prefers_nf4_at_6gb(self):
        nf4 = self.resolve("high_performance", 6)
        below = self.resolve("high_performance", 5.9)
        self.assertEqual((nf4.encoder_route, nf4.encoder_device), ("nf4", "cuda:0"))
        self.assertEqual((below.motion_device, below.encoder_route, below.encoder_device), ("cuda:0", "int8", "cpu"))

    def test_high_performance_uses_int8_gpu_without_nf4_at_8gb(self):
        gpu = self.resolve("high_performance", 8, nf4=False)
        cpu = self.resolve("high_performance", 7.9, nf4=False)
        self.assertEqual((gpu.encoder_route, gpu.encoder_device), ("int8", "cuda:0"))
        self.assertEqual((cpu.encoder_route, cpu.encoder_device), ("int8", "cpu"))

    def test_encoder_budget_does_not_move_motion_model_to_cpu(self):
        decision = self.resolve("high_performance", 1.9)
        self.assertEqual((decision.motion_device, decision.encoder_device), ("cuda:0", "cpu"))

    def test_mps_keeps_kimodo_accelerated_and_falls_back_per_capability(self):
        decision = self.resolve(
            "high_performance",
            16,
            device="mps",
            nf4=True,
            int8=True,
        )
        self.assertEqual((decision.motion_device, decision.encoder_route, decision.encoder_device), ("mps", "fp16", "mps"))
        precision = self.resolve(
            "high_precision",
            18,
            device="mps",
            nf4=False,
            int8=False,
            fp16=True,
        )
        self.assertEqual((precision.motion_device, precision.encoder_device), ("mps", "mps"))

    def test_mps_uses_fp16_even_when_fp16_acceleration_is_unavailable(self):
        decision = self.resolve(
            "high_performance",
            48,
            device="mps",
            nf4=True,
            int8=True,
            fp16=False,
        )
        self.assertEqual((decision.motion_device, decision.encoder_route, decision.encoder_device), ("mps", "fp16", "cpu"))

    def test_mps_fp16_oom_fallback_does_not_switch_to_int8(self):
        decision = self.resolve("high_performance", 48, device="mps")
        fallback = assets.force_text_encoder_cpu(decision)
        self.assertEqual((fallback.encoder_route, fallback.encoder_device), ("fp16", "cpu"))

    def test_cpu_only_backend_ignores_reported_accelerator_memory(self):
        decision = self.resolve("high_precision", 48, device="cpu")
        self.assertEqual((decision.motion_device, decision.encoder_device), ("cpu", "cpu"))

    def test_zero_encoder_budget_keeps_motion_device_and_moves_encoder_to_cpu(self):
        decision = self.resolve("high_precision", 0)
        self.assertEqual((decision.motion_device, decision.encoder_device), ("cuda:0", "cpu"))

    def test_forced_cpu_fallback_preserves_requested_precision(self):
        precision = assets.force_text_encoder_cpu(self.resolve("high_precision", 48))
        performance = assets.force_text_encoder_cpu(self.resolve("high_performance", 48))
        self.assertEqual((precision.encoder_route, precision.encoder_device), ("fp16", "cpu"))
        self.assertEqual((performance.encoder_route, performance.encoder_device), ("int8", "cpu"))

    def test_int8_layout_matches_resolved_device(self):
        self.assertEqual(
            assets.select_text_encoder_layout_for_route("int8", ".", "cpu").layout_id,
            "int8_single",
        )
        self.assertEqual(
            assets.select_text_encoder_layout_for_route("int8", ".", "cuda:0").layout_id,
            "int8_gpu_from_fp16",
        )

    def test_nf4_has_no_cpu_layout(self):
        self.assertEqual(
            assets.force_text_encoder_cpu(self.resolve("high_performance", 6)).encoder_route,
            "int8",
        )

    def test_explicit_zero_simulation_is_distinct_from_automatic_detection(self):
        defaults = {
            "model": assets.DEFAULT_MODEL_NAME,
            "text_encoder_mode": "high_precision",
            "models_root": "",
            "force_hf_download": False,
            "simulate_free_vram_gb": None,
        }
        automatic = _normalize_runtime_config({}, defaults)
        forced_cpu = _normalize_runtime_config({"simulate_free_vram_gb": 0}, defaults)
        self.assertIsNone(automatic["simulate_free_vram_gb"])
        self.assertEqual(forced_cpu["simulate_free_vram_gb"], 0.0)
        for invalid in (-1, float("nan")):
            with self.assertRaises(ValueError):
                _normalize_runtime_config({"simulate_free_vram_gb": invalid}, defaults)
        for removed in ("highvram", "force_cpu"):
            with self.assertRaises(ValueError):
                _normalize_runtime_config({removed: False}, defaults)

    def test_only_accelerator_oom_errors_are_detected(self):
        self.assertTrue(_is_accelerator_oom(RuntimeError("MPS backend out of memory")))
        self.assertFalse(_is_accelerator_oom(RuntimeError("Factor is exactly singular")))
        self.assertFalse(_is_encoder_oom(RuntimeError("CUDA out of memory")))

    def test_simulated_memory_is_treated_as_current_free_vram(self):
        with patch.dict("os.environ", {"KIMODO_SIMULATE_FREE_VRAM_GB": "6"}, clear=False), patch(
            "torch.cuda.is_available", return_value=True
        ), patch.object(kimodo_runtime, "_probe_device_kernel", return_value=True
        ), patch.object(kimodo_runtime, "_probe_bitsandbytes", return_value=(True, True, True)), patch.object(
            kimodo_runtime, "_probe_fp16_kernel", return_value=True
        ):
            profile = kimodo_runtime._runtime_self_check("cuda:0")
        self.assertEqual(profile.free_vram_gb, 6.0)

    def test_motion_model_requires_two_gb_before_encoder_routing(self):
        profile = kimodo_runtime._RuntimeSelfCheckResult(
            backend_profile="cuda",
            runtime_device="cuda:0",
            kernel_ok=True,
            bnb_present=True,
            bnb_ok=True,
            nf4_available=True,
            int8_accelerator_available=True,
            fp16_accelerator_available=True,
            free_vram_gb=1.9,
        )
        with patch("core.quickserver_assets.resolve_models_root", return_value=(Path("."), True)):
            with self.assertRaisesRegex(RuntimeError, "before model load"):
                kimodo_runtime._build_bridge_provision_plan(
                    ".",
                    assets.DEFAULT_MODEL_NAME,
                    runtime_profile=profile,
                )

    def test_quickserver_moves_motion_runtime_to_cpu_when_free_vram_is_too_low(self):
        gpu = kimodo_runtime._RuntimeSelfCheckResult(
            backend_profile="cuda",
            runtime_device="cuda:0",
            kernel_ok=True,
            bnb_present=True,
            bnb_ok=True,
            nf4_available=True,
            int8_accelerator_available=True,
            fp16_accelerator_available=True,
            free_vram_gb=1.0,
        )
        cpu = kimodo_runtime._RuntimeSelfCheckResult(
            backend_profile="cpu",
            runtime_device="cpu",
            kernel_ok=False,
            bnb_present=False,
            bnb_ok=False,
            nf4_available=False,
            int8_accelerator_available=False,
            fp16_accelerator_available=False,
            free_vram_gb=0.0,
        )
        profile = SimpleNamespace(backend="ardy", model_name="ardy-test", source_fps=20.0)
        layout = SimpleNamespace(layout_id="fp16", download_assets=())
        model = SimpleNamespace(text_encoder=object())
        config = {
            "model": "ardy-test",
            "text_encoder_mode": "high_precision",
            "models_root": "",
            "force_hf_download": False,
            "simulate_free_vram_gb": None,
        }
        logger = SimpleNamespace(log=lambda _message: None)
        with patch.dict("os.environ", {}, clear=False), patch.object(
            quickserver_cli.runtime_helpers,
            "_runtime_self_check",
            side_effect=(gpu, cpu),
        ) as self_check, patch.object(
            quickserver_cli.assets, "resolve_motion_model_profile", return_value=profile
        ), patch.object(
            quickserver_cli.assets, "resolve_models_root", return_value=(Path("."), True)
        ), patch.object(
            quickserver_cli.assets, "select_text_encoder_layout_for_route", return_value=layout
        ), patch.object(
            quickserver_cli.assets, "build_runtime_env", return_value={}
        ), patch.object(
            quickserver_cli.ardy_backend, "load_runtime", return_value=model
        ) as load_runtime, patch.object(
            quickserver_cli, "_refresh_encoder_route_after_motion_load", side_effect=lambda _m, _c, d, *_a: d
        ):
            result = quickserver_cli._ensure_runtime({}, config, ".", logger)

        self.assertEqual(self_check.call_args_list, [call(None), call("cpu")])
        self.assertEqual(result["device"], "cpu")
        self.assertEqual(load_runtime.call_args.args[3], "cpu")

    def test_mps_rebuilds_an_incompatible_shared_int8_encoder(self):
        mps = kimodo_runtime._RuntimeSelfCheckResult(
            backend_profile="mps",
            runtime_device="mps",
            kernel_ok=True,
            bnb_present=False,
            bnb_ok=False,
            nf4_available=False,
            int8_accelerator_available=False,
            fp16_accelerator_available=True,
            free_vram_gb=32.0,
        )
        decision = assets.resolve_text_encoder_runtime(
            "high_performance", "mps", 30.0,
            nf4_available=False,
            int8_accelerator_available=False,
            fp16_accelerator_available=True,
        )
        previous = assets.TextEncoderRuntimeDecision(
            mode="high_performance",
            motion_device="cpu",
            encoder_route="int8",
            encoder_device="cpu",
            reason="previous_cpu_runtime",
            effective_free_vram_gb=0.0,
        )
        old_encoder = type("LLM2VecInt8Encoder", (), {"target_device": "cpu"})()
        plan = SimpleNamespace(
            resolved_model=SimpleNamespace(local_name="Kimodo-SOMA-RP-v1"),
            models_root=Path("."),
            runtime_decision=decision,
        )
        model = SimpleNamespace(fps=30.0, text_encoder=None)
        config = {
            "model": "Kimodo-SOMA-RP-v1",
            "text_encoder_mode": "high_performance",
            "models_root": "",
            "force_hf_download": False,
            "simulate_free_vram_gb": None,
        }
        with patch.object(quickserver_cli.runtime_helpers, "_runtime_self_check", return_value=mps), patch.object(
            quickserver_cli.runtime_helpers, "_provision_bridge_assets", return_value=plan
        ), patch.object(
            quickserver_cli, "_refresh_encoder_route_after_motion_load", return_value=decision
        ), patch("core.bridge_load_model.load_bridge_model", return_value=model) as load_model:
            quickserver_cli._ensure_runtime(
                {},
                config,
                ".",
                SimpleNamespace(log=lambda _message: None),
                text_encoder=old_encoder,
                text_encoder_decision=previous,
            )

        self.assertEqual((decision.encoder_route, decision.encoder_device), ("fp16", "mps"))
        self.assertIsNone(load_model.call_args.kwargs["text_encoder"])


class DownloadCancellationTests(unittest.TestCase):
    def test_modelscope_progress_callback_observes_cancellation(self):
        cancel = threading.Event()
        callback = assets._make_logged_progress_callback(
            SimpleNamespace(log=lambda _message: None),
            "test asset",
            cancel,
        )("weights.bin", 100)

        cancel.set()
        with self.assertRaises(assets.DownloadCancelledError):
            callback.update(1)

    def test_ensure_asset_forwards_cancellation_to_modelscope(self):
        asset = assets.AssetSpec(
            label="test asset",
            local_dir_name="test-asset",
            modelscope_repo="test/modelscope",
            huggingface_repo="test/huggingface",
        )
        logger = SimpleNamespace(log=lambda _message: None)
        cancel = threading.Event()
        with TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            with patch.object(assets, "asset_is_ready", side_effect=(False, True)), patch.object(
                assets, "download_via_modelscope"
            ) as download:
                assets.ensure_asset_present(
                    asset,
                    root / "asset",
                    logger,
                    root / "flags",
                    [0],
                    force_site=assets.DownloadSite.MODELSCOPE,
                    cancel_event=cancel,
                )

        download.assert_called_once_with(asset, root / "asset", logger, cancel)


if __name__ == "__main__":
    unittest.main()
