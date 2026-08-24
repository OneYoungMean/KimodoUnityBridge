# Optimization

Use this when an existing Session Clip needs diagnosis and a corrected appended Clip.

## Loop

1. Analyze the source with `animation_analyze` (`high` when foot transitions matter) and open `pictures.image_path`.
2. Use `pose_get` on local frames `0`, `1`, `N-2`, and `N-1` when the Clip length is `N`; compare analysis tiles and root/foot evidence.
3. Decide in-place versus locomotion. Repair the smallest supported cause with materialized Pose edits, `pose_contract`, point constraints, a `pose_create_path` trajectory, or replacement generation.
4. Append a new Clip, analyze it again, open its PNG, and report temporal qualities as `not_verified` without playback/dense samples.

## General correction

Analyze before editing. Keep the source Clip and unrequested body regions unchanged. Use `pose_get` to materialize a `{track,index}` Pose, edit only required root/muscle channels, or generate a constrained replacement. A public-command limitation is a valid result; do not claim an existing Clip was edited in place.

## Transition diagnosis

Use `animation_compare` and endpoint `pose_get` to compare root, heading, pose, support foot, and phase. A direct imported transition may be a logical `transition_clip`, not a baked asset. Generate a separate bridge only when evidence shows a direct transition is unsafe and the public workflow can express the bridge.

## 中文

用于诊断并修正已有 Session Clip。先用 `animation_analyze` 分析并打开 `pictures.image_path`；需要脚切换时使用 `high`。长度为 `N` 时，用 `pose_get` 检查局部帧 `0`、`1`、`N-2`、`N-1`。区分原地/位移循环；通过实体化 Pose、`pose_contract`、点约束、`pose_create_path` 轨迹或替代生成修正，追加新 Clip，再次分析和打开 PNG。未提供播放/密集采样时，时间质量报告 `not_verified`。不得覆盖源 Clip，也不得声称不存在的原地编辑。
