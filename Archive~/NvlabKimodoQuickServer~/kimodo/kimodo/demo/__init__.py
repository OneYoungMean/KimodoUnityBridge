# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0

"""Demo entrypoint helpers.

Keep this module lightweight so non-UI runtime imports such as
`kimodo.demo.memory_manager` do not require optional UI dependencies
(`viser`) at import time.
"""

import argparse


def main() -> None:
    # Import UI app lazily to avoid requiring viser for headless/bridge usage.
    from .app import Demo
    from kimodo.model import DEFAULT_MODEL
    from kimodo.model.registry import resolve_model_name

    parser = argparse.ArgumentParser(description="Run the kimodo demo UI.")
    parser.add_argument(
        "--model",
        type=str,
        default=DEFAULT_MODEL,
        help="Default model to load (e.g. Kimodo-SOMA-RP-v1, kimodo-soma-rp, or SOMA).",
    )
    parser.add_argument(
        "--offload",
        action="store_true",
        help="Enable multi-tier memory offloading (Disk-RAM-VRAM) for low-memory GPUs.",
    )
    args = parser.parse_args()

    resolved = resolve_model_name(args.model, "Kimodo")
    demo = Demo(default_model_name=resolved, offload=args.offload)
    demo.run()


if __name__ == "__main__":
    main()
