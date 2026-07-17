from __future__ import annotations

import unittest

from kimodo.bridge import quickserver_assets as assets
from kimodo.bridge.quickserver_cli import _is_accelerator_oom, _normalize_runtime_config


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

    def test_high_precision_uses_fp16_gpu_at_18gb_and_cpu_below_it(self):
        gpu = self.resolve("high_precision", 18)
        cpu = self.resolve("high_precision", 17.9)
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

    def test_less_than_2gb_moves_kimodo_and_encoder_to_cpu(self):
        decision = self.resolve("high_performance", 1.9)
        self.assertEqual((decision.motion_device, decision.encoder_device), ("cpu", "cpu"))

    def test_mps_keeps_kimodo_accelerated_and_falls_back_per_capability(self):
        decision = self.resolve(
            "high_performance",
            16,
            device="mps",
            nf4=False,
            int8=False,
        )
        self.assertEqual((decision.motion_device, decision.encoder_route, decision.encoder_device), ("mps", "int8", "cpu"))
        precision = self.resolve(
            "high_precision",
            18,
            device="mps",
            nf4=False,
            int8=False,
            fp16=True,
        )
        self.assertEqual((precision.motion_device, precision.encoder_device), ("mps", "mps"))

    def test_cpu_only_backend_ignores_reported_accelerator_memory(self):
        decision = self.resolve("high_precision", 48, device="cpu")
        self.assertEqual((decision.motion_device, decision.encoder_device), ("cpu", "cpu"))

    def test_explicit_zero_moves_everything_to_cpu(self):
        decision = self.resolve("high_precision", 0)
        self.assertEqual((decision.motion_device, decision.encoder_device), ("cpu", "cpu"))

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

    def test_explicit_zero_simulation_is_distinct_from_automatic_detection(self):
        defaults = {
            "model": assets.DEFAULT_MODEL_NAME,
            "text_encoder_mode": "high_precision",
            "models_root": "",
            "force_hf_download": False,
            "simulate_vram_gb": None,
        }
        automatic = _normalize_runtime_config({}, defaults)
        forced_cpu = _normalize_runtime_config({"simulate_vram_gb": 0}, defaults)
        self.assertIsNone(automatic["simulate_vram_gb"])
        self.assertEqual(forced_cpu["simulate_vram_gb"], 0.0)
        for invalid in (-1, float("nan")):
            with self.assertRaises(ValueError):
                _normalize_runtime_config({"simulate_vram_gb": invalid}, defaults)
        for removed in ("highvram", "force_cpu"):
            with self.assertRaises(ValueError):
                _normalize_runtime_config({removed: False}, defaults)

    def test_only_accelerator_oom_errors_trigger_cpu_retry(self):
        self.assertTrue(_is_accelerator_oom(RuntimeError("MPS backend out of memory")))
        self.assertFalse(_is_accelerator_oom(RuntimeError("Factor is exactly singular")))


if __name__ == "__main__":
    unittest.main()
