import os
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import mocap_analysis


class MocapAnalysisMemoryTests(unittest.TestCase):
    def test_explicit_simulated_vram_controls_flowhmr_budget(self):
        self.assertEqual(mocap_analysis._resolve_free_vram_gb(6), 6.0)

    def test_quickserver_simulated_vram_environment_is_used(self):
        with patch.dict(os.environ, {"KIMODO_SIMULATE_FREE_VRAM_GB": "4"}, clear=False):
            self.assertEqual(mocap_analysis._resolve_free_vram_gb(), 4.0)

    def test_invalid_explicit_simulated_vram_is_rejected(self):
        with self.assertRaises(ValueError):
            mocap_analysis._resolve_free_vram_gb(float("nan"))

    def test_simulated_vram_does_not_fake_cuda_availability(self):
        with patch("torch.cuda.is_available", return_value=False):
            self.assertEqual(mocap_analysis._resolve_flowhmr_device(32), "cpu")
        self.assertEqual(mocap_analysis._resolve_flowhmr_device(1, "cuda"), "cuda")


if __name__ == "__main__":
    unittest.main()
