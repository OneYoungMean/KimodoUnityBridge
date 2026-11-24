import unittest
from flowhmr_memory import (
    CONSERVATIVE_FLOWHMR_OPTIONS,
    device_for_free_vram_gb,
    is_cuda_oom,
    options_for_free_vram_gb,
)


class FlowHmrMemoryTests(unittest.TestCase):
    def test_memory_bands_reduce_work_for_smaller_gpus(self):
        self.assertEqual(options_for_free_vram_gb(32).sam_chunk_size, 16)
        self.assertEqual(options_for_free_vram_gb(24).sam_chunk_size, 8)
        self.assertEqual(options_for_free_vram_gb(16).sam_chunk_size, 4)
        self.assertEqual(options_for_free_vram_gb(10).sam_chunk_size, 2)
        self.assertEqual(options_for_free_vram_gb(4), CONSERVATIVE_FLOWHMR_OPTIONS)

    def test_oom_detection_is_narrow(self):
        self.assertTrue(is_cuda_oom("CUDA out of memory while allocating tensor"))
        self.assertTrue(is_cuda_oom("CUDA error: memory allocation failed"))
        self.assertFalse(is_cuda_oom("checkpoint file is missing"))

    def test_four_gb_uses_conservative_cuda_and_lower_memory_uses_cpu(self):
        self.assertEqual(device_for_free_vram_gb(4), "cuda")
        self.assertEqual(device_for_free_vram_gb(4.0), "cuda")
        self.assertEqual(device_for_free_vram_gb(3.9), "cpu")
        self.assertEqual(device_for_free_vram_gb(7.9), "cuda")
        self.assertEqual(device_for_free_vram_gb(8), "cuda")



if __name__ == "__main__":
    unittest.main()
