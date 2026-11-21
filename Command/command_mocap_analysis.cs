using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using KimodoBridge;
using KimodoBridge.Editor;
using UnityEngine;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        public static string AnalyzeMocapVideo(string argumentsJson) => Execute(argumentsJson, arguments => StartMocapAnalysis(arguments, "video"));

        public static string AnalyzeMocapPicture(string argumentsJson) => Execute(argumentsJson, arguments => StartMocapAnalysis(arguments, "picture"));

        private static string StartMocapAnalysis(JObject arguments, string kind)
        {
                string mediaPath = RequiredStringValue(arguments, kind == "video" ? "video_path" : "image_path");
                string character = RequiredStringValue(arguments, "character");
                TimelineSessionRecord session = RequireCurrentTimelineSession();
                TimelineCharacterRecord target = ResolveSessionCharacterByReference(session, character, addIfMissing: true);
                if (target?.Animator == null || target.Track == null)
                    throw new InvalidOperationException($"Timeline character '{character}' has no bound Humanoid Animator track.");
                if (!KimodoRetargetCoreUtility.IsValidHumanoid(target.Animator.avatar))
                    throw new InvalidOperationException("The Timeline character binding Animator does not have a valid Humanoid Avatar.");
                string person = arguments.Value<string>("person")?.Trim();
                if (!string.IsNullOrWhiteSpace(person))
                {
                    throw new InvalidOperationException("FlowHMR selects the tracked performer automatically; the person argument is not supported.");
                }
                bool enableCamera = arguments.Value<bool?>("enable_camera") ?? false;
                Camera mainCamera = null;
                if (enableCamera)
                {
                    mainCamera = ResolveSingleMainCamera();
                }

                string requestId = "mocap:" + Guid.NewGuid().ToString("N");
                lock (JobsLock)
                {
                    MocapAnalysisTasks[requestId] = new JObject
                    {
                        ["request_id"] = requestId,
                        ["status"] = "accepted",
                        ["stage"] = "queued",
                        ["message"] = "Mocap analysis accepted.",
                        ["source_type"] = kind,
                        ["media_path"] = mediaPath,
                        ["character"] = target.CharacterRef,
                        ["enable_camera"] = enableCamera,
                        ["started_at_utc"] = DateTime.UtcNow.ToString("O")
                    };
                    TrimTerminalTasksLocked(MocapAnalysisTasks, MaxRememberedJobs, "completed", "failed");
                }

                EditorApplication.delayCall += () =>
                {
                    _ = RunMocapAnalysisTaskAsync(requestId, kind, mediaPath, person, target.CharacterRef, session.Id, enableCamera, mainCamera);
                };
                return Ok(new JObject
                {
                    ["request_id"] = requestId,
                    ["status"] = "accepted"
                });
        }

        private static async Task RunMocapAnalysisTaskAsync(
            string requestId,
            string kind,
            string mediaPath,
            string person,
            string characterReference,
            Guid sessionId,
            bool enableCamera,
            Camera mainCamera)
        {
            UpdateMocapAnalysisTask(requestId, "running", "estimating", "Running FlowHMR pose estimation...");
            try
            {
                JObject analysis = await KimodoBridgeService.Shared.AnalyzeMocapAsync(
                    kind, mediaPath, person, enableCamera, CancellationToken.None);
                await RunOnEditorThreadAsync(() =>
                    CompleteFlowHmrTimelineImport(requestId, analysis, characterReference, sessionId, enableCamera, mainCamera));
            }
            catch (Exception exception)
            {
                UpdateMocapAnalysisTask(requestId, "failed", "failed", exception.Message);
            }
        }

        private static Task RunOnEditorThreadAsync(Action action)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EditorApplication.delayCall += () =>
            {
                try
                {
                    action();
                    completion.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            };
            return completion.Task;
        }

        private static void CompleteFlowHmrTimelineImport(
            string requestId,
            JObject analysis,
            string characterReference,
            Guid expectedSessionId,
            bool enableCamera,
            Camera mainCamera)
        {
            try
            {
                TimelineSessionRecord session = RequireCurrentTimelineSession();
                if (session.Id != expectedSessionId)
                    throw new InvalidOperationException("The active Timeline changed while FlowHMR was running. KMB is available in the task result; re-run the import in the intended Timeline.");
                TimelineCharacterRecord character = ResolveSessionCharacterByReference(session, characterReference, addIfMissing: true);
                if (character == null || character.Animator == null)
                    throw new InvalidOperationException($"Timeline character '{characterReference}' was not found.");
                // The track's bound character Animator owns the target Avatar. Do not
                // infer it from any source AnimationClip or clip-level retarget setting.
                Avatar avatar = character.Animator.avatar;
                if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatar))
                    throw new InvalidOperationException("The Timeline character binding Animator does not have a valid Humanoid Avatar.");
                if (!KimodoFlowHmrMuscleClipImporter.TryCreateMuscleClip(
                        analysis, avatar, $"FlowHMR_{Guid.NewGuid():N}", out AnimationClip clip, out string assetPath, out string error))
                    throw new InvalidOperationException(error);
                RefreshCharacterTimelineAppendPosition(character);
                TimelineAnimationRecord animation = AppendAnimationClip(session, character, clip, "flowhmr", analysis, clip.name);
                animation.TimelineClip.duration = Math.Max(
                    1.0 / Math.Max(1f, clip.frameRate),
                    (analysis.Value<int?>("kmb_frame_count") ?? 1) / (double)Math.Max(1f, clip.frameRate));
                character.NextStartSeconds = animation.TimelineClip.end + ClipSafeZoneSeconds;
                if (enableCamera)
                {
                    if (!KimodoFlowHmrCameraTrackImporter.TryCreateCameraTrack(
                        session.Director, session.TimelineAsset, mainCamera, analysis, animation.TimelineStartSeconds, out TimelineClip cameraClip, out string cameraError))
                        throw new InvalidOperationException(cameraError);
                    analysis["camera_track"] = new JObject
                    {
                        ["enabled"] = true,
                        ["main_camera"] = mainCamera.name,
                        ["timeline_start_seconds"] = cameraClip.start,
                        ["timeline_duration_seconds"] = cameraClip.duration
                    };
                }
                SaveTimelineSession(session);
                analysis["clip"] = new JObject
                {
                    ["name"] = animation.Name,
                    ["asset_path"] = assetPath,
                    ["character"] = character.Name,
                    ["avatar"] = avatar.name,
                    ["timeline_start_seconds"] = animation.TimelineStartSeconds,
                    ["timeline_duration_seconds"] = animation.TimelineDurationSeconds,
                    ["frame_count"] = analysis.Value<int?>("kmb_frame_count") ?? 0,
                    ["avatar_source"] = "timeline_binding_animator"
                };
                UpdateMocapAnalysisTask(requestId, "completed", "completed", "FlowHMR motion was retargeted to the Timeline character Avatar and appended as a muscle clip.", analysis);
            }
            catch (Exception exception)
            {
                UpdateMocapAnalysisTask(requestId, "failed", "failed", exception.Message, analysis);
            }
        }

        private static Camera ResolveSingleMainCamera()
        {
            UnityEngine.SceneManagement.Scene activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            Camera[] candidates = UnityEngine.Object.FindObjectsOfType<Camera>(true)
                .Where(item => item != null && item.enabled && item.gameObject.scene == activeScene && item.CompareTag("MainCamera"))
                .ToArray();
            if (candidates.Length == 0)
                throw new InvalidOperationException("enable_camera requires one enabled scene Camera tagged MainCamera.");
            if (candidates.Length > 1)
                throw new InvalidOperationException($"enable_camera requires exactly one enabled MainCamera; found {candidates.Length}.");
            return candidates[0];
        }

        private static void UpdateMocapAnalysisTask(
            string requestId,
            string status,
            string stage,
            string message,
            JObject analysis = null)
        {
            lock (JobsLock)
            {
                if (!MocapAnalysisTasks.TryGetValue(requestId, out JObject task)) return;
                task["status"] = status;
                task["stage"] = stage;
                task["message"] = message ?? string.Empty;
                if (analysis != null)
                {
                    foreach (JProperty property in analysis.Properties())
                    {
                        if (!string.Equals(property.Name, "status", StringComparison.Ordinal))
                        {
                            task[property.Name] = property.Value.DeepClone();
                        }
                    }
                }
                if (status == "failed")
                {
                    task["error"] = new JObject
                    {
                        ["code"] = "mocap_analysis_failed",
                        ["message"] = message ?? "Mocap analysis failed."
                    };
                }
            }
        }

        private static JObject GetMocapAnalysisTask(string requestId)
        {
            lock (JobsLock)
            {
                if (!MocapAnalysisTasks.TryGetValue(requestId, out JObject task))
                {
                    throw new InvalidOperationException($"Unknown or expired Mocap request_id '{requestId}'.");
                }
                return (JObject)task.DeepClone();
            }
        }
    }
}
