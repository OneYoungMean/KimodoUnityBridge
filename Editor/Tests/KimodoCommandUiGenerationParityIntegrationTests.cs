using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using KimodoBridge;
using KimodoBridge.Editor;
using KimodoUnityBridge.Command;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;
using UnityEngine.TestTools;

namespace KimodoUnityBridge.Command.Tests
{
    /// <summary>
    /// Requires a running QuickServer.  The command creates the Timeline
    /// KimodoPlayableClip, then the Inspector generation entry point regenerates
    /// that same asset with the same seed.  The two resulting Clips are sampled
    /// on one Humanoid and compared frame by frame across every available
    /// HumanBodyBones entry plus the scene root.
    ///
    /// This baseline intentionally has no In/Out constraints. It isolates the
    /// command and Inspector entry points while keeping the backend request and
    /// output pose directly comparable.
    /// </summary>
    public sealed class KimodoCommandUiGenerationParityIntegrationTests
    {
        private const string ModelPath = "Packages/com.unity.kimodo_unity_motion_tools/Editor/Model/Armature.fbx";
        private const int DurationFrames = 120;
        private const int Seed = 481927;

        [UnityTest, Timeout(45 * 60 * 1000), Category("RequiresQuickServer")]
        public IEnumerator CommandThenInspectorGeneration_SameSeedProducesSameMotion()
        {
            SceneSetup[] previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            Scene fixtureScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject character = null;
            object testSession = null;
            string requestId = null;
            UnityEngine.Object previousSelection = Selection.activeObject;
            TimelineClip[] previousTimelineSelection = TimelineEditor.selectedClips;
            Action<string> previousRequestCapture = KimodoBridgeService.GenerationRequestPayloadContext.Value;
            string inspectorBackendRequestJson = null;
            AnimationClip commandClip = null;
            Stopwatch commandTimer = null;
            Stopwatch inspectorTimer = null;
            try
            {
                GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
                Assert.That(source, Is.Not.Null, "Missing Humanoid Armature fixture.");
                character = UnityEngine.Object.Instantiate(source);
                character.name = "KimodoCommandUiParity_" + Guid.NewGuid().ToString("N");
                SceneManager.MoveGameObjectToScene(character, fixtureScene);

                Animator animator = character.GetComponentInChildren<Animator>(true);
                Assert.That(animator, Is.Not.Null, "Armature fixture has no Animator.");
                Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath)
                    .OfType<Avatar>()
                    .FirstOrDefault(item => item != null && item.isValid && item.isHuman);
                Assert.That(avatar, Is.Not.Null, "Armature fixture has no Humanoid Avatar.");
                animator.avatar = avatar;
                animator.runtimeAnimatorController = null;
                animator.applyRootMotion = false;
                animator.Rebind();

                string fixtureFolder = "Assets/Archive/KimodoCommandUiParity/Fixtures";
                Directory.CreateDirectory(Path.Combine(Application.dataPath, "Archive", "KimodoCommandUiParity", "Fixtures"));
                Directory.CreateDirectory(Path.Combine(Application.dataPath, "Archive", "KimodoCommandUiParity", "Generated"));
                Directory.CreateDirectory(Path.Combine(Application.dataPath, "Archive", "KimodoCommandUiParity", "Timelines"));
                string fixtureScenePath = fixtureFolder + "/Parity_" + Guid.NewGuid().ToString("N") + ".unity";
                Assert.That(EditorSceneManager.SaveScene(fixtureScene, fixtureScenePath), Is.True,
                    "Could not save the scene needed by command character resolution.");

                Selection.activeGameObject = character;
                Invoke("session.context", new JObject());
                testSession = CurrentSessionProjection();
                Assert.That(testSession, Is.Not.Null, "session.context did not create an active Timeline session.");
                string outputName = "CommandUiParity_" + Guid.NewGuid().ToString("N");
                commandTimer = Stopwatch.StartNew();
                JObject accepted = Invoke("animation.generate", new JObject
                {
                    ["character"] = character.name,
                    ["prompt"] = "stand still and breathe naturally",
                    ["duration_frames"] = DurationFrames,
                    ["loop"] = false,
                    ["output"] = new JObject
                    {
                        ["folder"] = "Assets/Archive/KimodoCommandUiParity/Generated",
                        ["name"] = outputName
                    },
                    ["generation"] = new JObject
                    {
                        ["seed"] = Seed,
                        ["diffusion_steps"] = 20
                    }
                });
                requestId = accepted.Value<string>("request_id");
                Assert.That(requestId, Is.Not.Null.And.Not.Empty, accepted.ToString());

                JObject commandStatus = null;
                DateTime deadline = DateTime.UtcNow.AddMinutes(15);
                while (DateTime.UtcNow < deadline)
                {
                    commandStatus = Invoke("generation.get", new JObject { ["request_id"] = requestId });
                    string state = commandStatus.Value<string>("status")?.ToLowerInvariant();
                    if (state == "completed" || state == "failed" || state == "canceled")
                    {
                        commandTimer.Stop();
                        break;
                    }
                    yield return new WaitForSeconds(1f);
                }

                Assert.That(commandStatus?.Value<string>("status"), Is.EqualTo("completed"),
                    commandStatus?.ToString() ?? "Command generation returned no status.");
                string commandPath = commandStatus["clip"]?.Value<string>("asset_path");
                Assert.That(commandPath, Is.Not.Null.And.Not.Empty, commandStatus.ToString());
                AssetDatabase.Refresh();
                commandClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(commandPath);
                Assert.That(commandClip, Is.Not.Null, $"Command Clip was not found at '{commandPath}'.");

                KimodoPlayableClip playableClip = FindCommandPlayableClip(testSession);
                Assert.That(playableClip, Is.Not.Null, "Command did not leave a KimodoPlayableClip in the Timeline.");
                Assert.That(playableClip.seed, Is.EqualTo(Seed), "Command seed was not written to the UI asset.");
                Assert.That(playableClip.randomSeed, Is.False);
                Assert.That(playableClip.clip, Is.SameAs(commandClip),
                    "The command Timeline asset must reference its generated AnimationClip before the UI pass.");

                // Calling TryStartGenerate is the same path used by the Inspector
                // button.  It discovers the Timeline clip through the active
                // PlayableDirector, so no command-side payload is supplied here.
                TimelineEditor.selectedClips = Array.Empty<TimelineClip>();
                KimodoBridgeService.GenerationRequestPayloadContext.Value = payload =>
                    inspectorBackendRequestJson = payload;
                inspectorTimer = Stopwatch.StartNew();
                Assert.That(KimodoPlayableClipGenerationExecutionService.TryStartGenerate(
                    playableClip,
                    out KimodoEditorGenerationJobSession uiSession,
                    out string uiError), Is.True, uiError);
                Assert.That(uiSession, Is.Not.Null);

                while (uiSession.IsRunning)
                {
                    yield return null;
                }
                inspectorTimer.Stop();
                KimodoBridgeService.GenerationRequestPayloadContext.Value = previousRequestCapture;
                Assert.That(uiSession.Status, Is.EqualTo(KimodoEditorGenerationJobStatus.Completed), uiSession.Error);
                AnimationClip inspectorClip = uiSession.Payload?.GeneratedClip ?? playableClip.clip;
                Assert.That(inspectorClip, Is.Not.Null, "Inspector generation returned no AnimationClip.");
                Assert.That(inspectorClip, Is.Not.SameAs(commandClip),
                    "The parity test must compare the two independently written output assets.");

                commandStatus = Invoke("generation.get", new JObject
                {
                    ["request_id"] = requestId,
                    ["include_request_json"] = true
                });
                JObject commandBackendRequest = commandStatus["backend_request_json"] as JObject;
                Assert.That(commandBackendRequest, Is.Not.Null,
                    "Command generation status did not include its backend request payload.");
                Assert.That(inspectorBackendRequestJson, Is.Not.Null.And.Not.Empty,
                    "Inspector generation did not expose its backend request payload.");
                JObject inspectorBackendRequest = JObject.Parse(inspectorBackendRequestJson);
                NormalizeTaskId(commandBackendRequest);
                NormalizeTaskId(inspectorBackendRequest);
                bool commandRequestWasGeneration = commandBackendRequest.Value<string>("prompt") != null &&
                    commandBackendRequest["analysis_option"] == null;
                bool requestsEqual = commandRequestWasGeneration &&
                    JToken.DeepEquals(commandBackendRequest, inspectorBackendRequest);

                PoseDifference difference = CompareClips(commandClip, inspectorClip, character, animator);
                Assert.That(inspectorClip.frameRate, Is.EqualTo(commandClip.frameRate).Within(0.0001f));
                Assert.That(inspectorClip.length, Is.EqualTo(commandClip.length).Within(1.0 / 60.0));
                Assert.That(difference.MaxPositionError, Is.LessThanOrEqualTo(0.0025f), difference.ToString());
                Assert.That(difference.MaxRotationErrorDegrees, Is.LessThanOrEqualTo(0.25f), difference.ToString());
                string benchmark =
                    "Kimodo parity baseline: " +
                    $"command_elapsed_seconds={commandTimer.Elapsed.TotalSeconds:F3}, " +
                    $"inspector_elapsed_seconds={inspectorTimer.Elapsed.TotalSeconds:F3}, " +
                    $"requests_equal={requestsEqual}, frames={difference.FrameCount}, " +
                    $"max_bone_position_error_m={difference.MaxPositionError:F6}, " +
                    $"max_bone_rotation_error_degrees={difference.MaxRotationErrorDegrees:F4}";
                TestContext.Progress.WriteLine(benchmark);
                UnityEngine.Debug.Log(benchmark);
                if (!commandRequestWasGeneration)
                {
                    const string requestWarning =
                        "Command backend request was overwritten by post-generation analysis; " +
                        "request parity is unknown. The motion parity benchmark remains valid.";
                    TestContext.Progress.WriteLine(requestWarning);
                    UnityEngine.Debug.LogWarning(requestWarning);
                }
                else
                {
                    Assert.That(requestsEqual, Is.True,
                        "Command and Inspector sent different backend requests.\nCommand: " +
                        commandBackendRequest.ToString() + "\nInspector: " + inspectorBackendRequest.ToString());
                }
            }
            finally
            {
                KimodoBridgeService.GenerationRequestPayloadContext.Value = previousRequestCapture;
                if (!string.IsNullOrWhiteSpace(requestId))
                {
                    try
                    {
                        JObject status = Invoke("generation.get", new JObject { ["request_id"] = requestId });
                        string state = status.Value<string>("status")?.ToLowerInvariant();
                        if (state != "completed" && state != "failed" && state != "canceled")
                        {
                            Invoke("generation.cancel", new JObject
                            {
                                ["request_id"] = requestId,
                                ["reason"] = "parity test cleanup"
                            });
                        }
                    }
                    catch (Exception cleanupError)
                    {
                        TestContext.Progress.WriteLine("Best-effort generation cleanup failed: " + cleanupError.Message);
                    }
                }

                ResetCurrentTestContext(testSession);
                try
                {
                    ArchiveSessionTimeline(testSession);
                }
                catch (Exception cleanupError)
                {
                    TestContext.Progress.WriteLine("Best-effort Timeline archive failed: " + cleanupError.Message);
                }
                Selection.activeObject = previousSelection;
                TimelineEditor.selectedClips = previousTimelineSelection ?? Array.Empty<TimelineClip>();
                if (character != null) UnityEngine.Object.DestroyImmediate(character);
                if (previousSceneSetup.Any(setup => setup.isLoaded))
                    EditorSceneManager.RestoreSceneManagerSetup(previousSceneSetup);
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        private static PoseDifference CompareClips(
            AnimationClip expected,
            AnimationClip actual,
            GameObject character,
            Animator animator)
        {
            int frameCount = Mathf.Max(1, Mathf.RoundToInt(Mathf.Min(expected.length, actual.length) * expected.frameRate));
            float frameRate = Mathf.Max(1f, expected.frameRate);
            var result = new PoseDifference();
            result.FrameCount = frameCount;
            HumanBodyBones[] bones = Enum.GetValues(typeof(HumanBodyBones))
                .Cast<HumanBodyBones>()
                .Where(bone => bone != HumanBodyBones.LastBone)
                .ToArray();
            TransformState[] baseline = character.GetComponentsInChildren<Transform>(true)
                .Select(transform => new TransformState(transform, transform.localPosition, transform.localRotation, transform.localScale))
                .ToArray();
            for (int frame = 0; frame < frameCount; frame++)
            {
                float time = frame / frameRate;
                RestoreTransforms(baseline);
                expected.SampleAnimation(character, time);
                PoseSnapshot expectedPose = CapturePose(character, animator, bones);

                RestoreTransforms(baseline);
                actual.SampleAnimation(character, time);
                result.MaxPositionError = Mathf.Max(result.MaxPositionError,
                    Vector3.Distance(expectedPose.RootPosition, character.transform.position));
                result.MaxRotationErrorDegrees = Mathf.Max(result.MaxRotationErrorDegrees,
                    Quaternion.Angle(expectedPose.RootRotation, character.transform.rotation));
                Dictionary<HumanBodyBones, BoneSnapshot> actualBones = CaptureBones(animator, bones);
                foreach (HumanBodyBones bone in bones)
                {
                    if (!expectedPose.Bones.TryGetValue(bone, out BoneSnapshot before) ||
                        !actualBones.TryGetValue(bone, out BoneSnapshot after)) continue;
                    result.MaxPositionError = Mathf.Max(result.MaxPositionError,
                        Vector3.Distance(before.Position, after.Position));
                    result.MaxRotationErrorDegrees = Mathf.Max(result.MaxRotationErrorDegrees,
                        Quaternion.Angle(before.Rotation, after.Rotation));
                }
            }
            return result;
        }

        private static PoseSnapshot CapturePose(GameObject character, Animator animator, IEnumerable<HumanBodyBones> bones) =>
            new PoseSnapshot(character.transform.position, character.transform.rotation, CaptureBones(animator, bones));

        private static void RestoreTransforms(IEnumerable<TransformState> transforms)
        {
            foreach (TransformState state in transforms)
            {
                if (state.Transform == null) continue;
                state.Transform.localPosition = state.Position;
                state.Transform.localRotation = state.Rotation;
                state.Transform.localScale = state.Scale;
            }
        }

        private static void NormalizeTaskId(JObject request)
        {
            if (request != null) request["task_id"] = "<task_id>";
        }

        private static Dictionary<HumanBodyBones, BoneSnapshot> CaptureBones(
            Animator animator,
            IEnumerable<HumanBodyBones> bones)
        {
            var snapshots = new Dictionary<HumanBodyBones, BoneSnapshot>();
            foreach (HumanBodyBones bone in bones)
            {
                Transform transform = animator.GetBoneTransform(bone);
                if (transform != null)
                    snapshots[bone] = new BoneSnapshot(transform.position, transform.rotation);
            }
            return snapshots;
        }

        private static KimodoPlayableClip FindCommandPlayableClip(object session)
        {
            Assert.That(session, Is.Not.Null);
            object character = ((IEnumerable)session.GetType().GetProperty("Characters").GetValue(session))
                .Cast<object>().Single();
            AnimationTrack track = character.GetType().GetProperty("Track").GetValue(character) as AnimationTrack;
            Assert.That(track, Is.Not.Null);
            KimodoPlayableClip clip = track.GetClips()
                .Select(item => item?.asset as KimodoPlayableClip)
                .FirstOrDefault(item => item != null);
            return clip;
        }

        private static JObject Invoke(string command, JObject arguments)
        {
            JObject response = JObject.Parse(command_dispatcher.Invoke(command, arguments.ToString()));
            Assert.That(response.Value<bool?>("ok"), Is.True, $"{command} failed: {response["error"]}");
            return response;
        }

        private static object CurrentSessionProjection()
        {
            Type contextType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context");
            MethodInfo buildView = contextType.GetMethod("BuildTimelineSessionView", BindingFlags.Static | BindingFlags.NonPublic);
            return buildView.Invoke(null, new object[] { SceneManager.GetActiveScene() });
        }

        private static void ResetCurrentTestContext(object session)
        {
            Type contextType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context");
            if (session == null) return;
            contextType.GetMethod("ClearRuntimeSession", BindingFlags.Static | BindingFlags.NonPublic)
                ?.Invoke(null, new[] { session });
        }

        private static void ArchiveSessionTimeline(object session)
        {
            if (session == null) return;
            string sourcePath = session.GetType().GetProperty("TimelineAssetPath")?.GetValue(session) as string;
            if (string.IsNullOrWhiteSpace(sourcePath) || !sourcePath.StartsWith("Assets/", StringComparison.Ordinal)) return;
            string archiveFolder = "Assets/Archive/KimodoCommandUiParity/Timelines";
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Archive", "KimodoCommandUiParity", "Timelines"));
            string destinationPath = archiveFolder + "/" + Path.GetFileName(sourcePath);
            if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal)) return;
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(sourcePath) == null) return;
            string moveError = AssetDatabase.MoveAsset(sourcePath, AssetDatabase.GenerateUniqueAssetPath(destinationPath));
            Assert.That(moveError, Is.Empty, $"Could not archive parity test Timeline asset: {moveError}");
        }

        private readonly struct BoneSnapshot
        {
            public BoneSnapshot(Vector3 position, Quaternion rotation)
            {
                Position = position;
                Rotation = rotation;
            }

            public Vector3 Position { get; }
            public Quaternion Rotation { get; }
        }

        private readonly struct TransformState
        {
            public TransformState(Transform transform, Vector3 position, Quaternion rotation, Vector3 scale)
            {
                Transform = transform;
                Position = position;
                Rotation = rotation;
                Scale = scale;
            }

            public Transform Transform { get; }
            public Vector3 Position { get; }
            public Quaternion Rotation { get; }
            public Vector3 Scale { get; }
        }

        private readonly struct PoseSnapshot
        {
            public PoseSnapshot(Vector3 rootPosition, Quaternion rootRotation, Dictionary<HumanBodyBones, BoneSnapshot> bones)
            {
                RootPosition = rootPosition;
                RootRotation = rootRotation;
                Bones = bones;
            }

            public Vector3 RootPosition { get; }
            public Quaternion RootRotation { get; }
            public Dictionary<HumanBodyBones, BoneSnapshot> Bones { get; }
        }

        private sealed class PoseDifference
        {
            public int FrameCount;
            public float MaxPositionError;
            public float MaxRotationErrorDegrees;

            public override string ToString() =>
                $"Command/UI parity exceeded tolerance: max position error {MaxPositionError:F6} m, " +
                $"max rotation error {MaxRotationErrorDegrees:F4} degrees.";
        }
    }
}
