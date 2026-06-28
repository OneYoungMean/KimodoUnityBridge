# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""llama-server embeddings API client for motion generation."""

import json
import urllib.request

import numpy as np
import torch


class LlamaEmbeddingsAPI:
    """OpenAI-compatible embeddings client for llama-server."""

    def __init__(self, url: str, timeout_seconds: int = 120):
        if not url:
            raise ValueError("LlamaEmbeddingsAPI requires non-empty url.")
        self.base_url = str(url).rstrip("/")
        self.timeout_seconds = int(timeout_seconds)
        self.model_name = "default"
        self.device = "cpu"
        self.dtype = torch.float

    def to(self, device=None, dtype=None):
        if device is not None:
            self.device = device
        if dtype is not None:
            self.dtype = dtype
        return self

    def _embed_one(self, text: str) -> np.ndarray:
        def extract_embedding(payload_obj):
            if isinstance(payload_obj, dict):
                data = payload_obj.get("data")
                if isinstance(data, list) and len(data) > 0:
                    first = data[0]
                    if isinstance(first, dict):
                        emb = first.get("embedding")
                        if isinstance(emb, list) and len(emb) > 0:
                            return np.asarray(emb, dtype=np.float32)
                    if isinstance(first, list) and len(first) > 0:
                        return np.asarray(first, dtype=np.float32)
                emb = payload_obj.get("embedding")
                if isinstance(emb, list) and len(emb) > 0:
                    return np.asarray(emb, dtype=np.float32)
                return None
            if isinstance(payload_obj, list) and len(payload_obj) > 0:
                first = payload_obj[0]
                if isinstance(first, (int, float)):
                    return np.asarray(payload_obj, dtype=np.float32)
                if isinstance(first, list) and len(first) > 0:
                    return np.asarray(first, dtype=np.float32)
                if isinstance(first, dict):
                    emb = first.get("embedding")
                    if isinstance(emb, list) and len(emb) > 0:
                        return np.asarray(emb, dtype=np.float32)
            return None

        def normalize_vec(arr: np.ndarray) -> np.ndarray:
            if arr.ndim == 1:
                return arr.astype(np.float32, copy=False)
            if arr.ndim == 2:
                # Some llama-server builds may return per-token embeddings.
                # Collapse to a single sentence vector.
                return arr.mean(axis=0).astype(np.float32, copy=False)
            if arr.ndim > 2:
                flat = arr.reshape(-1, arr.shape[-1])
                return flat.mean(axis=0).astype(np.float32, copy=False)
            return arr.astype(np.float32, copy=False)

        endpoints = ["/v1/embeddings", "/embeddings"]
        payloads = [
            {"model": self.model_name, "input": text},
            {"model": self.model_name, "input": [text]},
            {"input": text},
            {"input": [text]},
        ]
        last_error = "unknown"
        for ep in endpoints:
            for body in payloads:
                payload = json.dumps(body).encode("utf-8")
                req = urllib.request.Request(
                    f"{self.base_url}{ep}",
                    data=payload,
                    headers={"Content-Type": "application/json"},
                    method="POST",
                )
                try:
                    with urllib.request.urlopen(req, timeout=self.timeout_seconds) as resp:
                        raw = resp.read().decode("utf-8", errors="replace")
                except Exception as exc:
                    last_error = f"{ep} {type(exc).__name__}: {exc}"
                    continue
                try:
                    obj = json.loads(raw)
                except Exception as exc:
                    last_error = f"{ep} invalid-json: {exc}"
                    continue
                emb = extract_embedding(obj)
                if emb is None:
                    last_error = f"{ep} invalid-embedding: {obj}"
                    continue
                return normalize_vec(emb)
        raise RuntimeError(f"Llama embeddings request failed: {last_error}")

    def __call__(self, texts):
        if isinstance(texts, str):
            texts = [texts]
        if not isinstance(texts, list) or len(texts) == 0:
            raise ValueError("texts must be non-empty string or list[str].")

        vecs = [self._embed_one(str(t)) for t in texts]
        arr = np.stack(vecs, axis=0)[:, None, :]
        lengths = np.ones(arr.shape[0], dtype=int).tolist()
        tensor = torch.from_numpy(arr).to(device=self.device, dtype=self.dtype)
        return tensor, lengths
