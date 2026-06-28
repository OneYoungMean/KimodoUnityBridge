# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""LLM2Vec encoder wrapper for Kimodo text conditioning."""

import gc
import platform
import os
import numpy as np
import torch
from torch import nn
from kimodo.bridge.quickserver_assets import NF4_LOCAL_DIR
from .llm2vec import LLM2Vec

class LLM2VecEncoder(nn.Module):
    """LLM2Vec text embeddings."""

    def __init__(
        self,
        base_model_name_or_path: str,
        peft_model_name_or_path: str,
        dtype: str,
        llm_dim: int,
    ) -> None:
        super().__init__()
        self.torch_dtype = getattr(torch, dtype)
        self.llm_dim = llm_dim
        self.base_model_name_or_path = base_model_name_or_path
        self.peft_model_name_or_path = peft_model_name_or_path

        self.custom_dir = self._resolve_local_text_encoder_dir()
        self.custom_peft_dir = self._resolve_local_llm2vec_peft_dir()
        self.target_device = self._resolve_target_device()

        print(f"[LLM2VecEncoder] Initializing model from {self.custom_dir}...")
        if self.custom_peft_dir:
            print(f"[LLM2VecEncoder] Using PEFT adapter from {self.custom_peft_dir}...")
        print(f"[LLM2VecEncoder] target_device={self.target_device}")
        print(f"[LLM2VecEncoder] Initialized (Waiting for first use to load weights)...")
        self.model = None

    def _resolve_target_device(self) -> str:
        mode = os.environ.get("TEXT_ENCODER_DEVICE", "auto").strip().lower()
        if mode == "cpu":
            return "cpu"
        if mode.startswith("mps"):
            return "mps" if torch.backends.mps.is_available() else "cpu"
        if mode.startswith("cuda"):
            if torch.cuda.is_available():
                return mode
            return "cpu"
        if torch.cuda.is_available():
            return "cuda:0"
        if torch.backends.mps.is_available():
            return "mps"
        return "cpu"

    def _resolve_local_text_encoder_dir(self) -> str:
        """Resolve local LLM2Vec directory for offline runs."""
        candidates: list[str] = []

        env_override = os.environ.get("KIMODO_LLM2VEC_DIR", "").strip()
        if env_override:
            candidates.append(os.path.abspath(env_override))

        kimodo_root = os.environ.get("KIMODO_ROOT_PATH", "").strip()
        if kimodo_root:
            candidates.append(
                os.path.abspath(os.path.join(kimodo_root, "models", NF4_LOCAL_DIR))
            )

        # Keep compatibility with the original README override placeholder.
        manual_placeholder = r"path_to_your_Llama_text-encoders"
        if manual_placeholder and os.path.isdir(manual_placeholder):
            candidates.append(os.path.abspath(manual_placeholder))

        # Derive from package location.
        this_dir = os.path.dirname(os.path.abspath(__file__))
        candidates.append(
            os.path.abspath(
                os.path.join(this_dir, os.pardir, os.pardir, os.pardir, os.pardir, "models", NF4_LOCAL_DIR)
            )
        )
        candidates.append(
            os.path.abspath(
                os.path.join(this_dir, os.pardir, os.pardir, os.pardir, "models", NF4_LOCAL_DIR)
            )
        )

        seen = set()
        ordered_candidates = []
        for c in candidates:
            if c and c not in seen:
                seen.add(c)
                ordered_candidates.append(c)

        for c in ordered_candidates:
            if os.path.isdir(c):
                # Accept either complete weights or HF-style config-only dirs.
                if os.path.exists(os.path.join(c, "config.json")) or os.path.exists(os.path.join(c, "model.safetensors")):
                    return c

        raise FileNotFoundError(
            "[LLM2VecEncoder] Missing local text encoder directory. "
            f"Set KIMODO_LLM2VEC_DIR or ensure KIMODO_ROOT_PATH/models/{NF4_LOCAL_DIR} exists. "
            f"Checked: {ordered_candidates}"
        )

    def _resolve_local_llm2vec_peft_dir(self) -> str | None:
        """Resolve optional external LLM2Vec PEFT adapter directory."""
        candidates: list[str] = []

        env_peft = os.environ.get("KIMODO_LLM2VEC_PEFT_DIR", "").strip()
        if env_peft:
            candidates.append(os.path.abspath(env_peft))

        text_encoders_dir = os.environ.get("TEXT_ENCODERS_DIR", "").strip()
        if text_encoders_dir:
            peft_name = self.peft_model_name_or_path.replace("/", os.sep)
            candidates.append(os.path.abspath(os.path.join(text_encoders_dir, peft_name)))

        seen = set()
        ordered_candidates = []
        for c in candidates:
            if c and c not in seen:
                seen.add(c)
                ordered_candidates.append(c)

        for c in ordered_candidates:
            if os.path.isdir(c):
                if os.path.exists(os.path.join(c, "adapter_model.safetensors")) or os.path.exists(
                    os.path.join(c, "model.safetensors")
                ):
                    return c

        return None

    def unload(self):
        """Offload the model weights to System RAM (CPU) if currently on GPU."""
        if self.model is not None:
            if self.get_device().type == "cuda":
                print(f"[LLM2VecEncoder] Offloading 5.4GB model to System RAM...")
                self.model.model.to("cpu")
                gc.collect()
                if platform.system() == "Linux":
                    try:
                        import ctypes
                        ctypes.CDLL("libc.so.6").malloc_trim(0)
                    except Exception:
                        pass
                elif platform.system() == "Windows":
                    from kimodo.demo.memory_manager import release_system_memory
                    release_system_memory()

                if torch.cuda.is_available():
                    torch.cuda.empty_cache()
                    torch.cuda.ipc_collect()
                if torch.backends.mps.is_available():
                    torch.mps.empty_cache()

    def reload(self):
        """Move from System RAM to VRAM."""
        if self.model is None:
            print(f"[LLM2VecEncoder] Model was None. Reloading from disk (15s delay)...")
            self.model = LLM2Vec.from_pretrained(
                base_model_name_or_path=self.custom_dir,
                peft_model_name_or_path=self.custom_peft_dir,
                torch_dtype=self.torch_dtype,
                device_map="cpu"
            )
            self.model.eval()
            for param in self.model.parameters():
                param.requires_grad = False

        if self.target_device.startswith("cuda"):
            from kimodo.demo.memory_manager import manager
            manager.ensure_vram_capacity(5400 * 1024 * 1024, device=self.target_device, exclude_name="text_encoder")

        curr_device = self.get_device()
        desired_type = self.target_device.split(":")[0]
        if curr_device.type != desired_type:
            print(f"[LLM2VecEncoder] Moving weights to {self.target_device}...")
            self.model.model.to(self.target_device)
            
            gc.collect()
            
            if platform.system() == "Linux":
                try:
                    import ctypes
                    ctypes.CDLL("libc.so.6").malloc_trim(0)
                except Exception:
                    pass
            elif platform.system() == "Windows":
                from kimodo.demo.memory_manager import release_system_memory
                release_system_memory()

            if torch.cuda.is_available():
                torch.cuda.empty_cache()
                torch.cuda.ipc_collect()
            if torch.backends.mps.is_available():
                torch.mps.empty_cache()
            
            if self.target_device.startswith("cuda"):
                from kimodo.demo.memory_manager import manager
                manager.log_memory_usage("Encoder Transfer Complete (RAM Reclaimed)")
        else:
            print(f"[LLM2VecEncoder] Model already on target device ({curr_device})")

    def get_device(self):
        if self.model is None:
            return torch.device("cpu")
        for p in self.model.model.parameters():
            if p.device.type != "meta":
                return p.device
        return torch.device("cpu")

    def delete(self):
        """Reclaim RAM without deleting from disk unless absolutely necessary."""
        self.unload()

    def __call__(self, text: list[str] | str):
        self.reload() # Auto-reload if called
        is_string = isinstance(text, str)
        texts = [text] if is_string else list(text)

        if len(texts) == 0:
            empty = torch.empty((0, 1, self.llm_dim), dtype=torch.float32, device=self.get_device())
            return empty, []

        with torch.no_grad():
            encoded_text = self.model.encode(
                texts,
                batch_size=min(32, len(texts)),
                show_progress_bar=False,
                convert_to_tensor=True,
                device=self.target_device,
            )

        if len(encoded_text.shape) == 1:
            encoded_text = encoded_text.unsqueeze(0)

        assert len(encoded_text.shape)
        assert self.llm_dim == encoded_text.shape[-1]

        encoded_text = encoded_text[:, None]
        lengths = np.ones(len(encoded_text), dtype=int).tolist()

        if is_string:
            encoded_text = encoded_text[0]
            lengths = lengths[0]

        return encoded_text, lengths
