from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from kimodo.bridge import quickserver_setup


class QuickServerSetupTests(unittest.TestCase):
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
