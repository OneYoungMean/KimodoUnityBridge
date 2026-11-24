# QuickServer runtime notes

If you explicitly plan to use Kimodo in Unity, we strongly recommend using Unity CLI.

This page records the project-local QuickServer details that are easy to confuse with the public Unity command surface. The command schema remains the source of truth for request and response fields.

## Launcher download timeouts

The launchers pass `KIMODO_UV_HTTP_TIMEOUT_SEC` through to uv as `UV_HTTP_TIMEOUT` for package downloads during setup and runtime operations. It defaults to 300 seconds. Set it before starting QuickServer when a package source is slow; for example, use `$env:KIMODO_UV_HTTP_TIMEOUT_SEC = '900'` in PowerShell or `export KIMODO_UV_HTTP_TIMEOUT_SEC=900` in a Unix shell. This is separate from `KIMODO_UV_INSTALL_TIMEOUT_SEC`, which limits downloading the uv executable itself, and `KIMODO_UV_PROBE_TIMEOUT_SEC`, which limits source probes.

## Optional MP4 output

The TCP `generate` request can set `output_mp4: true`. QuickServer keeps the selected motion `output_format` as the binary response and adds an absolute `mp4_path` to the JSON result. MP4 is an additional render; it does not replace the KMB, BVH, or compact JSON motion payload.

The renderer uses the UniMate plotting module. Set `KIMODO_UNIMATE_ROOT` when UniMate is outside the bundled QuickServer checkout. This variable affects MP4 rendering only; it does not select the mocap or GVHMR source. Discovery checks the configured path, the bundled `vendor/UniMate` directory, and supported adjacent checkouts; no fixed developer-machine path is required. The rendering environment also needs the Python packages reported by the runtime (`matplotlib`, `imageio`, and `imageio-ffmpeg`).

## Media pose analysis

Unity commands `pose.fromvideo` and `pose.frompicture` are asynchronous. They return a `request_id`, which is polled with `generation.get`, and leave Unity scene assets unchanged. The Unity command fields are absolute `video_path` and `image_path`; the lower-level QuickServer TCP adapter calls the same operations with `media_path`.

`pose.fromvideo` accepts MP4, MOV, M4V, AVI, and MKV files up to 2 GiB. `pose.frompicture` accepts PNG, JPG, and JPEG files up to 2 GiB and returns one pose sample. For image inference, QuickServer repeats the still image for one second at 30 FPS, then keeps the middle estimate.

The response contains FlowHMR's 52-joint SMPL-H motion in a KMB1 attachment, with source frame rate and frame count. Camera, per-frame global-time, and human-detection tracks are included when available; the output can be retargeted or used to build constraints in Unity.

## Runtime sources and assets

`pose.fromvideo` and `pose.frompicture` use the bundled FlowHMR source. Setup creates a separate Python 3.10 environment for FlowHMR; `KIMODO_SETUP_FLOWHMR=0` skips it, and `KIMODO_FLOWHMR_PYTHON` selects an externally managed interpreter. FlowHMR weights and gated body-model assets are provisioned under `models/FlowHMR` according to their upstream access and license terms.
