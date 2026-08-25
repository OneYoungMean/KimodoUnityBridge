# Analysis Picture Render-Pipeline Audit

Status: 2026-08-25

## Verdict

The analysis-picture renderer is **not yet equivalent** across URP, HDRP, and the Built-in/default renderer.

| Pipeline | Evidence | Status |
| --- | --- | --- |
| URP | Live Tuanjie analysis PNG generated after the depth pass change. | Supported and smoke-tested. |
| Built-in/default | The fallback subshader and `Standard`/`Sprites/Default` fallbacks exist, but no live project test was run. | Static-only; not verified. |
| HDRP | Depth shaders declare an HDRP subshader, but picture materials never select HDRP shaders and no live HDRP capture was run. | Not supported as an equivalent path. |

## Differences found

1. `MakeMaterial` and `TintPreview` prefer URP shaders, then fall back to `Standard`; they never select `HDRP/Lit` or an HDRP unlit shader.
2. `MakeUnlitMaterial` begins with `Sprites/Default`, which is not an HDRP-equivalent material path.
3. The depth encoder has URP/HDRP/Built-in subshaders, but `Camera.RenderWithShader` was only smoke-tested in URP. HDRP needs a live capture test before it can be claimed compatible.
4. The old fullscreen depth compositor is no longer used by the active renderer. The active implementation renders each pose's color/depth separately and performs nearest-depth composition on CPU. This removes cross-pose ordering errors, but has not been benchmarked in HDRP or Built-in/default.

## Required acceptance before declaring parity

For each pipeline, run the same anonymous humanoid Clip at `middle` and `high`, and confirm:

1. floor/grid, trajectory lines, and pose mesh all render without pink/error materials;
2. start/end tint and foot-transition tint match the reference rule;
3. an overlapping-pose clip is depth-occluded by camera depth rather than sample order;
4. the PNG is opened and visually reviewed.

Until these checks pass, only URP is a supported rendering target for analysis-picture evidence.
