# Third-Party Licenses and Model-Asset Terms

This document summarizes the principal third-party source projects, upstream acknowledgements, and model-asset terms currently included or used by KimodoUnityBridge and QuickServer. It is an index and practical summary; it is not a new combined license and does not replace any component license, model card, access agreement, or separately signed authorization. Each component remains subject to its own terms. The Apache-2.0 license at the repository root must not be interpreted as permission to commercially use or redistribute every model, weight, or dependency.

## Bundled source code

| Component | Location | License / terms | Notes |
| --- | --- | --- | --- |
| KimodoUnityBridge | [`LICENSE`](LICENSE) | Apache-2.0 | License for the Unity package itself. |
| QuickServer | [`NvlabKimodoQuickServer~/LICENSE`](NvlabKimodoQuickServer~/LICENSE) | Apache-2.0 | QuickServer has its own copy of the license. |
| ARDY | [`NvlabKimodoQuickServer~/ardy/LICENSE`](NvlabKimodoQuickServer~/ardy/LICENSE) | Apache-2.0 | Copyright notice names NVIDIA Corporation & Affiliates. |
| Kimodo | [`NvlabKimodoQuickServer~/kimodo/LICENSE`](NvlabKimodoQuickServer~/kimodo/LICENSE) | Apache-2.0 | Kimodo source used by QuickServer. |
| FlowHMR | [`NvlabKimodoQuickServer~/vendor/FlowHMR/LICENSE`](NvlabKimodoQuickServer~/vendor/FlowHMR/LICENSE), [upstream README](NvlabKimodoQuickServer~/vendor/FlowHMR/README.md) | Apache-2.0 (source) | The upstream README assigns model weights to their model-card terms; the source-code license does not automatically cover the weights. |
| YOLOX | [`NvlabKimodoQuickServer~/vendor/FlowHMR/third_party/YOLOX/LICENSE`](NvlabKimodoQuickServer~/vendor/FlowHMR/third_party/YOLOX/LICENSE) | Apache-2.0 (source) | Check the actual model repository for the terms covering YOLOX weights. |
| UniMate | [`NvlabKimodoQuickServer~/vendor/UniMate/LICENSE`](NvlabKimodoQuickServer~/vendor/UniMate/LICENSE) | MIT | Retain the copyright and permission notices when redistributing. |
| DINOv3 source | [`NvlabKimodoQuickServer~/vendor/FlowHMR/third_party/dinov3/`](NvlabKimodoQuickServer~/vendor/FlowHMR/third_party/dinov3/), [upstream license](NvlabKimodoQuickServer~/vendor/FlowHMR/third_party/dinov3/LICENSE.md) | Meta DINOv3 License Agreement (custom terms) | Bundled for the SAM 3D Body backbone; retain the upstream license and commit notice. |
| DINOv3-derived layers in VGGT-Omega | [`NvlabKimodoQuickServer~/vendor/FlowHMR/third_party/vggt-omega/vggt_omega/models/layers/`](NvlabKimodoQuickServer~/vendor/FlowHMR/third_party/vggt-omega/vggt_omega/models/layers/), [DINOv3 license](NvlabKimodoQuickServer~/vendor/FlowHMR/third_party/vggt-omega/DINOv3_LICENSE.md) | Meta DINOv3 License Agreement (custom terms) | This license remains relevant to the DINOv3-derived code retained in the VGGT-Omega vendor tree. |
| SAM 3D Body source | [`NvlabKimodoQuickServer~/vendor/FlowHMR/third_party/sam-3d-body/LICENSE`](NvlabKimodoQuickServer~/vendor/FlowHMR/third_party/sam-3d-body/LICENSE) | Meta SAM License (custom terms) | The vendored library source is covered by this agreement. The DINOv3 checkpoint and MHR assets remain gated model files and are kept under `models/FlowHMR` when migrated. |
| VGGT-Omega | [`NvlabKimodoQuickServer~/vendor/FlowHMR/third_party/vggt-omega/LICENSE`](NvlabKimodoQuickServer~/vendor/FlowHMR/third_party/vggt-omega/LICENSE) | FAIR Noncommercial Research License | Limited to noncommercial research uses. The license also restricts commercial use of related materials and outputs. Obtain separate authorization before using it in a commercial product or service. |

The FlowHMR upstream README also acknowledges HY-Motion 1.0, MixGRPO, PHC, GVHMR, VGGT-Omega, SAM 3D Body, YOLOX, Kimodo, and toyxyz_RGBMocap. An acknowledgement does not mean that all of those projects' code or assets are bundled here and does not change their licenses. GVHMR is only an upstream acknowledgement in this worktree; it is not a runtime dependency.

## Models, weights, and human-body assets

QuickServer obtains model files through local setup or external model repositories. The repository's source declaration is [`MODEL_DOWNLOAD_SOURCES.md`](NvlabKimodoQuickServer~/models/MODEL_DOWNLOAD_SOURCES.md). Code licenses and weight/data licenses may differ, so the actual model page and access agreement control before downloading or redistributing a model.

QuickServer restores and uses the SAM 3D Body DINOv3 checkpoint set for the default FlowHMR inference path. Keep these gated model files under their upstream access and license terms.

| Resource | Source / location | Distribution and use requirements |
| --- | --- | --- |
| FlowHMR checkpoint | Hugging Face [`fafsaf1/flowhmr-0.46B`](https://huggingface.co/fafsaf1/flowhmr-0.46B), with ModelScope mirror `oneyoungmean/flowhmr-0.46B` | The FlowHMR README refers users to the model card. The source license does not establish the checkpoint's rights; keep it in the external model directory and verify the repository's license and access conditions before copying or distributing it. |
| YOLOX-L checkpoint | ModelScope `oneyoungmean/yolox_l-v0.1.1` | YOLOX source is Apache-2.0, but the weight file must not automatically be treated as Apache-2.0. Verify the model card or the provider's terms. |
| VGGT-Omega checkpoint | ModelScope [`facebook/VGGT-Omega`](https://huggingface.co/facebook/VGGT-Omega) | Subject to the FAIR Noncommercial Research License and upstream access approval. The source license must not be treated as commercial authorization for the weights. |
| SMPL-H neutral model and derived skeleton file | `assets/body_models/smplh/neutral/model.npz` and `model_skeleton.npz`; terms in [`SMPL-H LICENSE.txt`](NvlabKimodoQuickServer~/vendor/FlowHMR/assets/body_models/smplh/LICENSE.txt) | The Max Planck MANO/SMPL+H terms allow only noncommercial scientific research, education, or artistic use and prohibit making the model available to third parties without prior written permission. The derived `model_skeleton.npz` must therefore also remain local and must be generated by an authorized user. |

## External runtime dependencies and release checks

The Unity Package Manager dependencies declared in `package.json` (Newtonsoft JSON and Unity Timeline), together with the Python packages installed from QuickServer and FlowHMR `requirements*.txt` files, are not relicensed by this document. They and their transitive dependencies retain their own upstream licenses. For an offline runtime bundle or binary distribution, generate a dependency inventory and include the license copies for the exact versions installed.

Before distribution, verify that third-party source licenses and copyright notices remain with the package; retain the DINOv3 license for the bundled source and DINOv3-derived VGGT-Omega layers and the SAM 3D Body agreement for its source; ensure model-card and gated-access terms permit the intended use; respect VGGT-Omega and SMPL-H noncommercial restrictions; and keep SMPL-H source or derived files out of the distributable package.
