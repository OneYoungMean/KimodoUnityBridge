# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0

import gc
import logging
import platform
import time
from typing import Dict, List, Optional
import psutil

import torch

log = logging.getLogger(__name__)

def release_system_memory():
    """Force garbage collection and instruct glibc (Linux) or Windows to return freed heap to the OS."""
    gc.collect()
    if platform.system() == "Linux":
        try:
            import ctypes
            ctypes.CDLL("libc.so.6").malloc_trim(0)
        except Exception as e:
            log.debug(f"malloc_trim failed: {e}")
    elif platform.system() == "Windows":
        try:
            import ctypes
            kernel32 = ctypes.windll.kernel32
            # Explicitly define types to avoid handle truncation on 64-bit systems
            kernel32.GetCurrentProcess.restype = ctypes.c_void_p
            kernel32.SetProcessWorkingSetSize.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_size_t]
            kernel32.SetProcessWorkingSetSize.restype = ctypes.c_int
            
            handle = kernel32.GetCurrentProcess()
            # -1, -1 triggers the OS to empty the working set of the process.
            kernel32.SetProcessWorkingSetSize(handle, -1, -1)
        except Exception as e:
            log.debug(f"Windows memory reclamation failed: {e}")

# Constants
RESERVED_VRAM_WINDOWS = 600 * 1024 * 1024
RESERVED_VRAM_LINUX = 400 * 1024 * 1024  # Increased for 6GB card safety
SYSTEM_RAM_THRESHOLD = 90.0

import os
os.environ["PYTORCH_ALLOC_CONF"] = "expandable_segments:True"

class MemoryManager:
    """Singleton to manage model residency across Disk, System RAM, and VRAM."""

    _instance = None

    def __new__(cls):
        if cls._instance is None:
            cls._instance = super(MemoryManager, cls).__new__(cls)
            cls._instance._initialized = False
        return cls._instance

    def __init__(self):
        if self._initialized:
            return
        self.models: Dict[str, object] = {}  # model_name -> model_object
        self.last_used: Dict[str, float] = {}
        self.encoder: Optional[object] = None  # Global text encoder
        self.reserved_vram = RESERVED_VRAM_WINDOWS if platform.system() == "Windows" else RESERVED_VRAM_LINUX
        self.offload_enabled = False # Toggle via CLI
        self._initialized = True
        self.log_memory_usage("Initialized")

    def log_memory_usage(self, message: str):
        """Log current RAM and VRAM usage."""
        mem = psutil.virtual_memory()
        ram_used = mem.used / (1024**3)
        ram_total = mem.total / (1024**3)
        ram_percent = mem.percent

        log_msg = f"[MemoryManager] {message} | RAM: {ram_used:.2f}/{ram_total:.2f}GB ({ram_percent}%)"

        if torch.cuda.is_available():
            free, total = torch.cuda.mem_get_info()
            vram_used = (total - free) / (1024**3)
            vram_total = total / (1024**3)
            vram_free = free / (1024**3)
            log_msg += f" | VRAM: {vram_used:.2f}/{vram_total:.2f}GB (Free: {vram_free:.2f}GB)"
        else:
            log_msg += " | VRAM: N/A"

        print(log_msg)
        log.info(log_msg)

    def register_model(self, name: str, model: object):
        self.models[name] = model
        self.last_used[name] = time.time()

    def register_encoder(self, encoder: object):
        self.encoder = encoder
        self.models["text_encoder"] = encoder
        self.last_used["text_encoder"] = time.time()

    def purge_encoder(self):
        """Standard encoder offload, respecting offload_enabled."""
        if not self.offload_enabled:
            return
        if self.encoder is not None:
            if hasattr(self.encoder, "unload"):
                self.encoder.unload()
            release_system_memory()

    def purge_encoder_completely(self):
        """Force-offload the encoder and trigger deep GC (Always works)."""
        if self.encoder is not None:
            print("[MemoryManager] Reclaiming RAM from encoder via residency transition...")
            if hasattr(self.encoder, "unload"):
                self.encoder.unload()
            
            release_system_memory()
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
                torch.cuda.ipc_collect()
            if torch.backends.mps.is_available():
                torch.mps.empty_cache()
            self.log_memory_usage("RAM Reclamation Complete")

    def get_free_vram(self) -> int:
        if not torch.cuda.is_available():
            return 0
        free, total = torch.cuda.mem_get_info()
        return free - self.reserved_vram

    def ensure_system_ram_capacity(self):
        """Trigger GC if System RAM is high."""
        if not self.offload_enabled:
            return
        mem = psutil.virtual_memory()
        if mem.percent > SYSTEM_RAM_THRESHOLD:
            self.log_memory_usage(f"System RAM threshold reached ({mem.percent}%)")
            release_system_memory()

    def ensure_vram_capacity(self, required_bytes: int, device: str = "cuda:0", exclude_name: Optional[str] = None):
        if not self.offload_enabled or "cpu" in device:
            return

        if torch.cuda.is_available():
            torch.cuda.empty_cache()
            torch.cuda.ipc_collect()
        if torch.backends.mps.is_available():
            torch.mps.empty_cache()
        release_system_memory()

        current_free = self.get_free_vram()
        
        if exclude_name and exclude_name in self.models:
             m = self.models[exclude_name]
             curr_dev = str(getattr(m, "device", "cpu"))
             if "cuda" in curr_dev:
                 pass

        if current_free >= required_bytes:
            return

        print(f"[MemoryManager] VRAM tight. Need {required_bytes/(1024**2):.1f}MB, only {current_free/(1024**2):.1f}MB free. Offloading others...")

        sorted_names = [k for k, v in sorted(self.last_used.items(), key=lambda item: item[1])]
        for name in sorted_names:
            if name == exclude_name:
                continue
            self.offload_model(name)
            if self.get_free_vram() >= required_bytes:
                return

    def touch_and_move(self, name: str, device: str):
        """Move a model to GPU, minimizing peak memory usage."""
        self.last_used[name] = time.time()
        model = self.models.get(name)
        if model is None:
            return

        if "cuda" in device:
            if not self.offload_enabled:
                if hasattr(model, "to"):
                    model.to(device)
                return

            if hasattr(model, "device") and str(model.device) == device:
                return

            self.ensure_system_ram_capacity()
            self.ensure_vram_capacity(800 * 1024 * 1024, device)
            
            encoder = getattr(model, "text_encoder", None)
            motion_rep = getattr(model, "motion_rep", None)
            if encoder is not None:
                setattr(model, "text_encoder", None)
            if motion_rep is not None:
                setattr(model, "motion_rep", None)

            print(f"[MemoryManager] Moving model '{name}' to {device}...")
            try:
                if hasattr(model, "to"):
                    model.to(device)
            finally:
                if encoder is not None:
                    setattr(model, "text_encoder", encoder)
                if motion_rep is not None:
                    setattr(model, "motion_rep", motion_rep)
            
            if hasattr(model, "device"):
                model.device = device

            if torch.cuda.is_available():
                torch.cuda.empty_cache()
                torch.cuda.ipc_collect()
            if torch.backends.mps.is_available():
                torch.mps.empty_cache()
            release_system_memory()

    def offload_model(self, name: str):
        """Move model weights to System RAM with deep GC."""
        if not self.offload_enabled:
            return
            
        model = self.models.get(name)
        if model is not None:
            # Only offload if currently on GPU
            curr_dev = str(getattr(model, "device", "cpu"))
            if "cuda" in curr_dev:
                print(f"[MemoryManager] Offloading model '{name}' to System RAM...")
                if hasattr(model, "to"):
                    model.to("cpu")
                if hasattr(model, "device"):
                    model.device = "cpu"
                
                release_system_memory()
                if torch.cuda.is_available():
                    torch.cuda.empty_cache()
                    torch.cuda.ipc_collect()
                if torch.backends.mps.is_available():
                    torch.mps.empty_cache()
                self.log_memory_usage(f"Offloaded '{name}'")

    def report_residency(self):
        """Print a summary of where all models are currently residing."""
        print("-" * 50)
        print("[MemoryManager] Residency Report:")
        for name, model in self.models.items():
            device = "Unknown"
            if hasattr(model, "get_device"):
                device = str(model.get_device())
            elif hasattr(model, "device"):
                device = str(model.device)
            elif hasattr(model, "parameters"):
                try:
                    p = next(model.parameters())
                    device = str(p.device)
                except StopIteration:
                    pass
            print(f"  - {name:20} : {device}")
        
        mem = psutil.virtual_memory()
        print(f"  - System RAM           : {mem.percent}% ({mem.used/(1024**3):.1f}GB used)")
        if torch.cuda.is_available():
            free, total = torch.cuda.mem_get_info()
            print(f"  - GPU VRAM             : {(total-free)/(1024**3):.1f}/{(total)/(1024**3):.1f}GB used")
        print("-" * 50)

# Global instance
manager = MemoryManager()
