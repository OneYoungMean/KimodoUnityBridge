# QuickServer runtime notes

If you explicitly plan to use Kimodo in Unity, we strongly recommend using Unity CLI.

This page records the project-local QuickServer details that are easy to confuse with the public Unity command surface. The command schema remains the source of truth for request and response fields.

## Optional MP4 output

The TCP `generate` request can set `output_mp4: true`. QuickServer keeps the selected motion `output_format` as the binary response and adds an absolute `mp4_path` to the JSON result. MP4 is an additional render; it does not replace the KMB, BVH, or compact JSON motion payload.

The renderer uses the UniMate plotting module. Set `KIMODO_UNIMATE_ROOT` when UniMate is outside the bundled QuickServer checkout. This variable affects MP4 rendering only; it does not select the mocap or GVHMR source. Discovery checks the configured path, the bundled `vendor/UniMate` directory, and supported adjacent checkouts; no fixed developer-machine path is required. The rendering environment also needs the Python packages reported by the runtime (`matplotlib`, `imageio`, and `imageio-ffmpeg`).

## Media pose analysis

Unity commands `pose.fromvideo` and `pose.frompicture` are asynchronous. They return a `request_id`, which is polled with `generation.get`, and leave Unity scene assets unchanged. The Unity command fields are absolute `video_path` and `image_path`; the lower-level QuickServer TCP adapter calls the same operations with `media_path`.

`pose.fromvideo` accepts MP4, MOV, M4V, AVI, and MKV files up to 2 GiB. Its optional `person` value (`left` or `right`) selects a performer by screen side. `pose.frompicture` accepts PNG, JPG, and JPEG files up to 2 GiB and returns one pose sample. Internally the still image is repeated for one second at 24 FPS so the video estimator can run, then the middle estimate is returned.

The result is an SMPL-X pose array with axis-angle rotations in radians and translations in meters. It includes the source frame rate, frame counts, joint names, body shape (`betas`), rest joints, gravity-aligned Y-up coordinates, and optional OpenCV-camera fields. The data is intended for downstream retargeting or constraint construction; it is not a Unity AnimationClip by itself.

## Runtime sources and assets

The bundled Mixamo LLM Mocap pipeline is used unless `KIMODO_MOCAP_ROOT` points to another checkout. `KIMODO_MOCAP_PYTHON` selects the Python interpreter used for GVHMR; otherwise QuickServer uses its current interpreter. `GVHMR_ROOT` selects the GVHMR runtime/checkpoint directory and defaults to `vendor/GVHMR`.

QuickServer provisions missing GVHMR source and checkpoints during analysis when it can, then requires the lightweight bundled `SMPLX_NEUTRAL_LITE_V437.npz` asset (3,539,332 bytes, about 3.54 MB) beside the checkpoints. Pose-only analysis does not require the full SMPL-X model. Missing source, interpreter, media, or checkpoints are reported as analysis errors and should be fixed before retrying the request.
