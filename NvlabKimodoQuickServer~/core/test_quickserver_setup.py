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


if __name__ == "__main__":
    unittest.main()
