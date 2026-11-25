from pathlib import Path
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

from core import quickserver_setup


class QuickServerSetupTests(unittest.TestCase):
    def test_default_venv_is_at_runtime_root(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "kimodo"
            source.mkdir()
            (source / "pyproject.toml").write_text("[project]\nname='test'\nversion='0'\n", encoding="utf-8")

            paths = quickserver_setup.discover_project_paths(root)

            self.assertEqual(paths.venv_dir, root / ".venv")
            self.assertEqual(paths.flowhmr_venv_dir, paths.venv_dir)
            self.assertEqual(paths.flowhmr_python, paths.venv_python)

    def test_setup_flowhmr_uses_shared_python_without_installing_upstream_requirements(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            (root / "kimodo").mkdir()
            (root / "kimodo" / "pyproject.toml").write_text(
                "[project]\nname='test'\nversion='0'\n", encoding="utf-8"
            )
            source = root / "vendor" / "FlowHMR"
            yolox = source / "third_party" / "YOLOX"
            yolox.mkdir(parents=True)
            (source / "run_v2m_demo_generation.py").touch()
            (yolox / "setup.py").touch()
            paths = quickserver_setup.discover_project_paths(root)
            paths.venv_python.parent.mkdir(parents=True)
            paths.venv_python.touch()
            logger = SimpleNamespace(log=lambda _message: None)
            with patch.object(quickserver_setup, "_find_uv_bin", return_value="uv"), patch.object(
                quickserver_setup, "_select_uv_default_index", return_value="https://pypi.org/simple"
            ), patch.object(quickserver_setup, "_flowhmr_protected_versions", return_value={
                name: "unchanged" for name in quickserver_setup.FLOWHMR_PROTECTED_PACKAGES
            }), patch.object(quickserver_setup, "_validate_flowhmr_protected_versions"), patch.object(
                quickserver_setup, "_flowhmr_runtime_ready", return_value=True
            ), patch.object(quickserver_setup, "_run_logged") as run_logged:
                quickserver_setup._setup_flowhmr_env(paths, logger)

            self.assertEqual(run_logged.call_count, 2)
            install_args = run_logged.call_args_list[0].args[0]
            self.assertEqual(install_args[install_args.index("--python") + 1], str(paths.venv_python))
            self.assertIn("--no-deps", install_args)
            self.assertNotIn("-r", install_args)
            self.assertEqual(run_logged.call_args_list[1].args[0][-2], "--editable")

    def test_macos_arm64_uses_checked_in_motion_correction_wheel(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            wheel = root / "wheels" / "motion_correction-1.0.0-cp312-cp312-macosx_11_0_arm64.whl"
            wheel.parent.mkdir()
            wheel.write_bytes(b"test wheel")
            paths = SimpleNamespace(
                wheels_dir=wheel.parent,
                venv_python=root / "venv" / "bin" / "python",
                source_root=root / "kimodo",
            )
            logger = SimpleNamespace(log=lambda _message: None)
            with patch.object(quickserver_setup.sys, "platform", "darwin"), patch.object(
                quickserver_setup.platform, "machine", return_value="arm64"
            ), patch.object(
                quickserver_setup, "_run_capture", side_effect=[(1, ""), (0, "/venv/site-packages/motion_correction/__init__.py")]
            ), patch.object(quickserver_setup, "_run_logged") as run_logged:
                self.assertTrue(quickserver_setup._ensure_motion_correction(paths, "uv", logger, "https://pypi.org/simple"))

            self.assertEqual(run_logged.call_count, 1)
            self.assertEqual(run_logged.call_args.args[0][-1], str(wheel))

    def test_macos_arm64_missing_wheel_fails_without_source_build(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            paths = SimpleNamespace(
                wheels_dir=root / "wheels",
                venv_python=root / "venv" / "bin" / "python",
                source_root=root / "missing-source",
            )
            paths.wheels_dir.mkdir()
            logger = SimpleNamespace(log=lambda _message: None)
            with patch.object(quickserver_setup.sys, "platform", "darwin"), patch.object(
                quickserver_setup.platform, "machine", return_value="arm64"
            ), patch.object(quickserver_setup, "_run_capture", return_value=(1, "")), patch.object(
                quickserver_setup, "_run_logged"
            ) as run_logged:
                with self.assertRaisesRegex(quickserver_setup.SetupError, "Missing macOS ARM64 MotionCorrection wheel"):
                    quickserver_setup._ensure_motion_correction(paths, "uv", logger, "https://pypi.org/simple")

            run_logged.assert_not_called()

    def test_macos_x64_fails_with_architecture_error(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            paths = SimpleNamespace(
                wheels_dir=root / "wheels",
                venv_python=root / "venv" / "bin" / "python",
                source_root=root / "missing-source",
            )
            paths.wheels_dir.mkdir()
            logger = SimpleNamespace(log=lambda _message: None)
            with patch.object(quickserver_setup.sys, "platform", "darwin"), patch.object(
                quickserver_setup.platform, "machine", return_value="x86_64"
            ), patch.object(quickserver_setup, "_run_capture", return_value=(1, "")):
                with self.assertRaisesRegex(quickserver_setup.SetupError, "Unsupported macOS architecture"):
                    quickserver_setup._ensure_motion_correction(paths, "uv", logger, "https://pypi.org/simple")

    def test_setup_failure_keeps_command_output_in_log(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "kimodo"
            source.mkdir()
            (source / "pyproject.toml").write_text("[project]\nname='test'\nversion='0'\n", encoding="utf-8")

            def fail_setup(_paths, _mode, logger):
                logger.log("uv detail: file is locked")
                raise quickserver_setup.SetupError("Command failed")

            options = quickserver_setup.SetupCliOptions("file", None, False, "auto", None)
            with patch.object(quickserver_setup, "_setup_buildenv", fail_setup):
                result = quickserver_setup.run_setup_cli(root, options)

            log = (root / "log" / "setup.log").read_text(encoding="utf-8")
            self.assertFalse(result.ok)
            self.assertIn("uv detail: file is locked", log)
            self.assertIn("[ERROR] Command failed", log)

    def test_missing_uv_is_reported_during_setup_bootstrap(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "kimodo"
            source.mkdir()
            (source / "pyproject.toml").write_text("[project]\nname='test'\nversion='0'\n", encoding="utf-8")
            paths = quickserver_setup.discover_project_paths(root)
            messages = []
            logger = SimpleNamespace(log=messages.append)
            with patch.object(quickserver_setup.shutil, "which", return_value=None), patch.object(
                quickserver_setup.subprocess, "run", side_effect=OSError("missing")
            ):
                with self.assertRaisesRegex(quickserver_setup.SetupError, "uv not found"):
                    quickserver_setup._find_uv_bin(paths, logger)

            self.assertIn("[ERROR] No working uv executable found during setup bootstrap.", messages)

    def test_flowhmr_setup_is_required_after_base_sentinel_when_runtime_imports_are_missing(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "kimodo"
            source.mkdir()
            (source / "pyproject.toml").write_text("[project]\nname='test'\nversion='0'\n", encoding="utf-8")
            paths = quickserver_setup.discover_project_paths(root)
            paths.venv_python.parent.mkdir(parents=True)
            paths.venv_python.touch()
            paths.setup_sentinel.write_text("setup_mode=auto\n", encoding="utf-8")
            options = quickserver_setup.SetupCliOptions("file", None, False, "auto", None)
            with patch.object(quickserver_setup, "_flowhmr_runtime_ready", return_value=False), patch.object(
                quickserver_setup, "_setup_buildenv"
            ) as setup_buildenv, patch.object(quickserver_setup, "_setup_flowhmr_env") as setup_flowhmr:
                result = quickserver_setup.run_setup_cli(root, options)

            self.assertTrue(result.ok)
            setup_buildenv.assert_not_called()
            setup_flowhmr.assert_called_once()


if __name__ == "__main__":
    unittest.main()
