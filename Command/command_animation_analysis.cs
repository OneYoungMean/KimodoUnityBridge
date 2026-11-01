using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KimodoBridge;
using KimodoBridge.Editor;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        public static string AnimationAnalysis(string argumentsJson) => Execute(argumentsJson, AnimationAnalysis);

        private static string AnimationAnalysis(JObject arguments) =>
            Ok(AnimationAnalysisCore(arguments, allowActiveGenerationRange: false));

        private static JObject AnimationAnalysisCore(
            JObject arguments,
            bool allowActiveGenerationRange)
        {
            try
            {
                return AnimationAnalysisCoreInternal(arguments, allowActiveGenerationRange);
            }
            finally
            {
                // Samples are request-local evaluation data. Persisting them in
                // a static dictionary made every analysis consume more memory,
                // while a later request can deterministically recapture them.
                AnalysisPoseSamples.Clear();
            }
        }

        private static JObject AnimationAnalysisCoreInternal(
            JObject arguments,
            bool allowActiveGenerationRange)
        {
            TimelineSessionRecord session = RequireTimelineSession(arguments);
            JArray clips = arguments["clips"] as JArray;
            if (clips == null || clips.Count != 1 || clips[0] is not JObject clipReference)
            {
                throw new InvalidOperationException("clips must contain exactly one character and clip reference.");
            }
            string characterName = RequiredStringValue(clipReference, "character");
            if (IsLegacyPreviewCharacterReference(characterName))
            {
                throw new InvalidOperationException($"Character reference '{characterName}' identifies a session preview object. Use the source scene character name instead.");
            }
            TimelineCharacterRecord character = session.Characters.FirstOrDefault(item =>
                string.Equals(item.Name, characterName, StringComparison.OrdinalIgnoreCase));
            if (character == null)
            {
                throw new InvalidOperationException($"Character '{characterName}' is not in the current scene context.");
            }

            string clipName = RequiredStringValue(clipReference, "clip");
            TimelineAnimationRecord animation = character.Animations.FirstOrDefault(item =>
                string.Equals(item.Name, clipName, StringComparison.OrdinalIgnoreCase));
            if (animation == null)
            {
                if (allowActiveGenerationRange)
                {
                    throw new InvalidOperationException(
                        $"Generated Clip '{clipName}' is not registered in the current scene context; post-analysis will not append a duplicate Clip.");
                }
                AnimationClip externalClip = ResolveAnimationClip(clipName);
                animation = AppendAnimationClip(session, character, externalClip, "analysis_source", null);
            }
            int startFrame = Mathf.RoundToInt((float)(animation.TimelineStartSeconds * SessionFrameRate));
            int endFrame = startFrame + Math.Max(1, Mathf.RoundToInt((float)(animation.TimelineDurationSeconds * SessionFrameRate)));
            if (!allowActiveGenerationRange)
            {
                ThrowIfGenerationRangeLocked(session, character, startFrame, endFrame, AnimationAnalysisCommand);
            }

            JObject cameraOptions = arguments["camera"] as JObject ?? new JObject();
            int pictureResolution = ResolveAnalysisPictureResolution(arguments["resolution"]);
            // Keep command-level analysis settings explicit and stable.
            JObject analysisOptions = new JObject();
            string inputSignature = BuildAnimationAnalysisSignature(character, animation, analysisOptions);
            AnalysisCacheRecord record;
            if (!TryFindCachedAnimationAnalysis(session, character, animation, inputSignature, out record))
            {
                if (!IsHumanoidCharacter(character) && !HasRenderableMesh(character.Root))
                {
                    throw new InvalidOperationException(
                        $"Character '{character.Name}' is neither a valid humanoid nor a renderable Mesh object.");
                }

                JObject analysis;
                byte[] analysisMotionBytes;
                if (IsHumanoidCharacter(character))
                {
                    analysis = AnalyzeAnimation(session, animation, analysisOptions, out analysisMotionBytes);
                }
                else
                {
                    analysis = BuildMeshAnalysis(animation);
                    analysisMotionBytes = null;
                }
                if (!IsHumanoidCharacter(character)) analysis["source"] = "mesh_only_pose_sampling";
                string id = CacheAnalysisResult(
                    session, character, startFrame / SessionFrameRate, endFrame / SessionFrameRate,
                    analysis, analysisMotionBytes, animation, inputSignature);
                record = GetCachedAnalysis(session, id);
            }

            // Normalize on cache hits as well so persisted results never
            // reintroduce deprecated relative-time keyframe fields.
            NormalizeAnalysisContract(record.Analysis, startFrame, endFrame);
            if (IsHumanoidCharacter(character))
            {
                EnsureAnalysisRootTrajectory(session, character, record, startFrame, endFrame);
                EnsureEndpointPoseComparison(session, character, animation, record, startFrame, endFrame);
            }
            var subject = new AnalysisSubject(character, animation, record, startFrame, endFrame);
            JObject pictures = RenderAnalysisPictures(session, new[] { subject }, pictureResolution, cameraOptions);
            record.Pictures = pictures != null ? (JObject)pictures.DeepClone() : new JObject();
            AnalysisCache[record.Id] = record;
            WriteJsonAtomically(AnalysisCachePath(session, record.Id), record.ToJson());
            SaveTimelineSession(session);
            string analysisImagePath = pictures?.Value<string>("image_path") ?? string.Empty;
            var analysisLog = new JObject
            {
                ["camera"] = cameraOptions.DeepClone(),
                ["clip"] = new JObject
                {
                    ["character"] = subject.Character?.Name ?? string.Empty,
                    ["clip"] = subject.Animation?.Name ?? string.Empty,
                    ["analysis"] = subject.Record.Analysis?.DeepClone() ?? new JObject()
                },
                ["image_path"] = analysisImagePath,
                ["image_path_absolute"] = string.IsNullOrWhiteSpace(analysisImagePath)
                    ? string.Empty
                    : ProjectRelativePathToAbsolute(analysisImagePath)
            };
            Debug.Log("[Kimodo][Analysis] " + analysisLog.ToString(Formatting.None));
            return KimodoAnimationAnalysisResponseBuilder.BuildPublicResponse(
                CreateAnalysisResponseData(subject),
                IsHumanoidCharacter(character),
                pictures,
                AnalysisContractVersion);
        }

        private static KimodoAnimationAnalysisResponseData CreateAnalysisResponseData(AnalysisSubject subject)
        {
            AnalysisCacheRecord record = subject?.Record;
            return new KimodoAnimationAnalysisResponseData(
                record?.Id,
                record?.SessionId,
                subject?.Character?.Name,
                subject?.Animation?.Name,
                subject?.StartFrame ?? 0,
                subject?.EndFrameExclusive ?? 0,
                record?.Analysis,
                record?.RootTrajectory);
        }

        private static void EnsureAnalysisRootTrajectory(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            AnalysisCacheRecord record,
            int startFrame,
            int endFrameExclusive)
        {
            if (record?.RootTrajectory?.Value<int?>("motion_semantics_version") >= 2 &&
                record.RootTrajectory["path"] is JObject pathReference)
            {
                int cachedIndex = pathReference.Value<int?>("index") ?? -1;
                string cachedTrack = pathReference.Value<string>("track");
                KimodoConstraintMarker cachedMarker = cachedIndex >= 0 &&
                    string.Equals(cachedTrack, character.PoseCacheTrack?.name, StringComparison.OrdinalIgnoreCase)
                        ? FindPoseMarker(character.PoseCacheTrack, cachedIndex)
                        : null;
                if (cachedMarker?.IsExternalPath == true && cachedMarker.PathData != null)
                {
                    return;
                }
            }

            int frameCount = Math.Max(1, endFrameExclusive - startFrame);
            KimodoMarkerSampleResult[] samples = CaptureCachedSampleResults(session, record, character, startFrame, frameCount);
            if (samples.Length == 0 || !TryGetRoot2DWorld(samples[0], out Vector3 startPosition, out Quaternion startRotation))
            {
                throw new InvalidOperationException(
                    $"Character '{character.Name}' root trajectory could not sample the first complete root pose.");
            }

            Quaternion startHeading = KimodoMotionMath.ResolvePlanarHeading(startRotation);
            var knots = new List<KimodoRootPathKnot>(samples.Length);
            var jsonSamples = new JArray();
            float pathLength = 0f;
            float minPitchDelta = 0f;
            float maxPitchDelta = 0f;
            float minRollDelta = 0f;
            float maxRollDelta = 0f;
            Vector2 previousPosition = Vector2.zero;
            Vector2 finalPosition = Vector2.zero;
            Vector2 firstHeading = Vector2.up;
            Vector2 finalHeading = Vector2.up;
            for (int frame = 0; frame < samples.Length; frame++)
            {
                if (!TryGetRoot2DWorld(samples[frame], out Vector3 worldPosition, out Quaternion worldRotation))
                {
                    throw new InvalidOperationException(
                        $"Character '{character.Name}' root trajectory could not sample complete root motion at frame {frame}.");
                }

                Vector3 normalizedPosition = KimodoMotionMath.NormalizePlanarPosition(
                    worldPosition,
                    startPosition,
                    startHeading);
                Quaternion normalizedHeading = KimodoMotionMath.NormalizePlanarHeading(
                    worldRotation,
                    startHeading);
                var position = KimodoMotionMath.ToPlanar(normalizedPosition);
                var heading = KimodoMotionMath.ToPlanarHeadingVector(normalizedHeading);
                heading = heading.sqrMagnitude > 1e-8f ? heading.normalized : finalHeading;
                Vector3 rootRotationDelta = KimodoMotionMath.RelativeEulerDegrees(startRotation, worldRotation);
                if (frame > 0) pathLength += Vector2.Distance(previousPosition, position);
                if (frame == 0) firstHeading = heading;
                minPitchDelta = Mathf.Min(minPitchDelta, rootRotationDelta.x);
                maxPitchDelta = Mathf.Max(maxPitchDelta, rootRotationDelta.x);
                minRollDelta = Mathf.Min(minRollDelta, rootRotationDelta.z);
                maxRollDelta = Mathf.Max(maxRollDelta, rootRotationDelta.z);
                previousPosition = position;
                finalPosition = position;
                finalHeading = heading;
                knots.Add(new KimodoRootPathKnot
                {
                    frame = frame,
                    position = position,
                    hasHeading = true,
                    heading = heading
                });
                jsonSamples.Add(new JObject
                {
                    ["frame"] = frame,
                    ["position_xz"] = new JArray(position.x, position.y),
                    ["heading_xz"] = new JArray(heading.x, heading.y),
                    ["root_rotation_delta_euler_degrees"] = new JArray(
                        rootRotationDelta.x,
                        rootRotationDelta.y,
                        rootRotationDelta.z)
                });
            }

            int index = AllocatePoseIndex(character.PoseCacheTrack);
            float sourceHumanScale = KimodoConstraintNormalizationUtility.ResolveHumanScale(character.Avatar);
            StoreExternalPath(character, index, new KimodoRootPathData
            {
                type = "analyzed",
                length = pathLength,
                sourceHumanScale = sourceHumanScale,
                inverse = false,
                knots = knots
            });

            float sampleSpanSeconds = samples.Length > 1
                ? (samples.Length - 1) / (float)SessionFrameRate
                : 0f;
            float firstYaw = KimodoMotionMath.ResolvePlanarYawDegrees(firstHeading);
            float finalYaw = KimodoMotionMath.ResolvePlanarYawDegrees(finalHeading);
            float signedHeadingChange = Mathf.DeltaAngle(firstYaw, finalYaw);
            record.RootTrajectory = new JObject
            {
                ["motion_semantics_version"] = 2,
                ["path"] = PoseReferenceJson(character.PoseCacheTrack.name, index),
                ["coordinate_space"] = "world",
                ["frame_rate"] = SessionFrameRate,
                ["frame_count"] = samples.Length,
                ["duration_seconds"] = samples.Length / SessionFrameRate,
                ["sample_span_seconds"] = sampleSpanSeconds,
                ["path_length_xz"] = pathLength,
                ["net_displacement_xz"] = new JArray(finalPosition.x, finalPosition.y),
                ["net_distance_xz"] = finalPosition.magnitude,
                ["average_speed_xz"] = sampleSpanSeconds > 1e-6f ? pathLength / sampleSpanSeconds : 0f,
                ["heading_change_degrees"] = signedHeadingChange,
                ["root_pitch_delta_range_degrees"] = new JArray(minPitchDelta, maxPitchDelta),
                ["root_roll_delta_range_degrees"] = new JArray(minRollDelta, maxRollDelta),
                ["source_human_scale"] = sourceHumanScale,
                ["samples"] = jsonSamples
            };
            AnalysisCache[record.Id] = record;
            WriteJsonAtomically(AnalysisCachePath(session, record.Id), record.ToJson());
        }

        private static bool IsSessionObject(GameObject gameObject)
        {
            Transform current = gameObject != null ? gameObject.transform : null;
            while (current != null)
            {
                if (KimodoRuntimeUtility.IsContextObject(current.gameObject)) return true;
                current = current.parent;
            }
            return false;
        }

        private static void EnsureEndpointPoseComparison(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            TimelineAnimationRecord animation,
            AnalysisCacheRecord record,
            int startFrame,
            int endFrameExclusive)
        {
            if (record?.Analysis == null || endFrameExclusive <= startFrame) return;
            if (record.Analysis["endpoint_pose_comparison"] is JObject existing &&
                existing["root_transform_included"]?.Value<bool>() == true)
            {
                return;
            }

            KimodoMarkerSampleResult[] endpointSamples = CaptureCachedSampleResults(session, record, character, startFrame, Math.Max(1, endFrameExclusive - startFrame));
            KimodoMarkerSampleResult first = endpointSamples[0];
            KimodoMarkerSampleResult last = endpointSamples[endpointSamples.Length - 1];
            bool valid = first?.sampleData?.IsValid == true && last?.sampleData?.IsValid == true;
            var comparison = new JObject
            {
                ["method"] = "humanoid_pose_and_root_motion_endpoint_compare",
                ["root_transform_included"] = true,
                ["root2d_constraint_included"] = false,
                ["start_frame"] = 0,
                ["end_frame"] = Math.Max(0, endFrameExclusive - startFrame - 1)
            };

            if (valid)
            {
                KimodoMotionMath.PoseDelta delta = ComputePoseMotionDelta(first, last);
                comparison["status"] = "ok";
                comparison["mean_muscle_delta"] = delta.MeanBodyMuscleDelta;
                comparison["body_pose_matches"] = delta.MeanBodyMuscleDelta <= 0.08f;
                comparison["root_position_delta_xyz"] = new JArray(
                    delta.RootPositionDelta.x,
                    delta.RootPositionDelta.y,
                    delta.RootPositionDelta.z);
                comparison["root_height_delta"] = delta.RootHeightDelta;
                comparison["root_rotation_delta_degrees"] = delta.RootRotationDeltaDegrees;
                comparison["root_rotation_delta_euler_degrees"] = new JArray(
                    delta.RootPitchDeltaDegrees,
                    delta.RootYawDeltaDegrees,
                    delta.RootRollDeltaDegrees);
            }
            else
            {
                comparison["status"] = "insufficient_evidence";
            }

            record.Analysis["endpoint_pose_comparison"] = comparison;
            JObject trajectory = record.RootTrajectory ?? new JObject();
            float pathLength = trajectory.Value<float?>("path_length_xz") ?? 0f;
            float netDistance = trajectory.Value<float?>("net_distance_xz") ?? 0f;
            float headingChange = trajectory.Value<float?>("heading_change_degrees") ?? 0f;
            bool bodyPoseMatches = valid && comparison.Value<bool?>("body_pose_matches") == true;
            float rootHeightDelta = comparison.Value<float?>("root_height_delta") ?? 0f;
            JArray rootEulerDelta = comparison["root_rotation_delta_euler_degrees"] as JArray;
            float rootPitchDelta = rootEulerDelta?[0]?.Value<float>() ?? 0f;
            float rootRollDelta = rootEulerDelta?[2]?.Value<float>() ?? 0f;
            bool rootVerticalAndTiltMatch = valid &&
                Mathf.Abs(rootHeightDelta) <= 0.03f &&
                Mathf.Abs(rootPitchDelta) <= 5f &&
                Mathf.Abs(rootRollDelta) <= 5f;
            record.Analysis["motion_profile"] = new JObject
            {
                ["clip_loop_time"] = animation?.Clip != null && animation.Clip.isLooping,
                ["is_loop_candidate"] = animation?.Clip != null && animation.Clip.isLooping &&
                    bodyPoseMatches && rootVerticalAndTiltMatch,
                ["root_vertical_and_tilt_matches"] = rootVerticalAndTiltMatch,
                ["has_clear_path"] = netDistance >= 0.05f || pathLength >= 0.08f,
                ["path_length_xz"] = pathLength,
                ["net_distance_xz"] = netDistance,
                ["heading_change_degrees"] = headingChange,
                ["heading_consistent"] = Mathf.Abs(headingChange) <= 8f,
                ["keyframe_heading_consistent"] = Mathf.Abs(headingChange) <= 8f,
                ["root_pitch_delta_range_degrees"] = trajectory["root_pitch_delta_range_degrees"]?.DeepClone() ?? new JArray(),
                ["root_roll_delta_range_degrees"] = trajectory["root_roll_delta_range_degrees"]?.DeepClone() ?? new JArray(),
                ["should_override_path"] = "defer_to_task_semantics",
                ["should_override_heading"] = "defer_to_task_semantics"
            };
            WriteJsonAtomically(AnalysisCachePath(session, record.Id), record.ToJson());
        }

        // Shared by automatic analysis endpoint checks.
        // The common math keeps body, root height and root rotation observable.
        private static KimodoMotionMath.PoseDelta ComputePoseMotionDelta(
            KimodoMarkerSampleResult origin,
            KimodoMarkerSampleResult target)
        {
            if (origin?.sampleData == null || target?.sampleData == null ||
                !origin.sampleData.IsValid || !target.sampleData.IsValid)
            {
                throw new InvalidOperationException("Pose comparison requires two valid Humanoid samples.");
            }
            GetRootTransform(origin, out Vector3 originPosition, out Quaternion originRotation);
            GetRootTransform(target, out Vector3 targetPosition, out Quaternion targetRotation);
            return KimodoMotionMath.Compare(
                origin.sampleData,
                target.sampleData,
                originPosition,
                originRotation,
                targetPosition,
                targetRotation);
        }

        private static int ResolveAnalysisPictureResolution(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return 1920;
            if (value.Type != JTokenType.Integer)
            {
                throw new InvalidOperationException("resolution must be a positive integer pixel size.");
            }
            int resolution = value.Value<int>();
            if (resolution < 64 || resolution > 4096)
            {
                throw new InvalidOperationException("resolution must be between 64 and 4096 pixels.");
            }
            return resolution;
        }

        private static void NormalizeAnalysisContract(
            JObject analysis,
            int startFrame,
            int endFrame)
        {
            analysis ??= new JObject();
            var keyframes = new JArray();
            foreach (JObject keyframe in (analysis?["keyframes"] as JArray ?? new JArray()).OfType<JObject>())
            {
                // QuickServer analysis is always relative to the requested
                // segment.  Convert that local frame to the Session frame once;
                // do not infer absolute-vs-local from its numeric value.
                int frame;
                if (keyframe.Value<int?>("timeline_frame_60") is int timelineFrame)
                {
                    frame = Mathf.Clamp(timelineFrame, startFrame, endFrame - 1);
                }
                else
                {
                    int reportedFrame = keyframe.Value<int?>("frame")
                        ?? Mathf.RoundToInt((float)((keyframe.Value<double?>("time") ?? 0.0) * SessionFrameRate));
                    frame = Mathf.Clamp(startFrame + reportedFrame, startFrame, endFrame - 1);
                }
                JObject annotation = (JObject)keyframe.DeepClone();
                annotation.Remove("time");
                annotation.Remove("session_time");
                annotation.Remove("time_seconds");
                annotation.Remove("frame");
                annotation.Remove("local_frame_60");
                annotation.Remove("local_time_seconds");
                annotation["timeline_frame_60"] = frame;
                keyframes.Add(annotation);
            }
            JArray contacts = analysis["foot_contacts"] as JArray
                ?? analysis["foot_contact_changes"] as JArray
                ?? new JArray();
            var normalizedContacts = new JArray();
            foreach (JObject contact in contacts.OfType<JObject>())
            {
                normalizedContacts.Add(new JObject
                {
                    ["clip_index"] = contact.Value<int?>("clip_index") ?? 0,
                    ["foot"] = contact.Value<string>("foot") ?? string.Empty,
                    ["frame"] = contact.Value<int?>("frame") ?? 0,
                    ["contact"] = contact.Value<bool?>("contact") ?? false,
                    ["transition"] = contact.Value<string>("transition") ?? string.Empty,
                    ["duration_frames"] = contact.Value<int?>("duration_frames") ?? 0
                });
            }
            // Destructive v2 contract: the analyzer owns temporal phase
            // intervals. Do not re-sample or uniformly select keyframes here.
            // The renderer later replaces Humanoid anchors with phase anchors.
            analysis["keyframes"] = keyframes;
            analysis["foot_contacts"] = normalizedContacts;
            analysis["source"] = "quickserver_analysis_only";
            analysis["command_fps"] = SessionFrameRate;
        }

        private static JObject BuildMeshAnalysis(TimelineAnimationRecord animation)
        {
            int startFrame = Math.Max(0, animation?.StartFrame ?? 0);
            int frameCount = Math.Max(1, animation?.EndFrameExclusive > animation?.StartFrame
                ? animation.EndFrameExclusive - animation.StartFrame
                : Mathf.Max(1, Mathf.RoundToInt((float)((animation?.TimelineDurationSeconds ?? 0.0) * SessionFrameRate))));
            int count = Math.Min(2, frameCount);
            var keyframes = new JArray();
            for (int index = 0; index < count; index++)
            {
                keyframes.Add(new JObject
                {
                    ["timeline_frame_60"] = startFrame + (count <= 1
                        ? 0
                        : Mathf.RoundToInt(Mathf.Lerp(0f, frameCount - 1, index / (float)(count - 1)))),
                    ["kind"] = "mesh_pose"
                });
            }
            return new JObject
            {
                ["keyframes"] = keyframes,
                ["foot_contacts"] = new JArray(),
                ["phase_track_version"] = "NOT_APPLICABLE",
                ["phase_track"] = new JArray(),
                ["source"] = "mesh_only_pose_sampling"
            };
        }

        private static bool IsHumanoidCharacter(TimelineCharacterRecord character)
        {
            return character != null && KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar);
        }

        private static JObject AnalyzeAnimation(
            TimelineSessionRecord session,
            TimelineAnimationRecord animation,
            JObject analysisOptions,
            out byte[] analysisMotionBytes)
        {
            analysisMotionBytes = null;
            // Analysis is a command-level contract. Timeline editor FPS and
            // native KMB FPS are sampling details, never protocol frame rates.
            float frameRate = (float)KimodoFrameTimeUtility.CommandFrameRate;
            byte[] motionBytes = animation.KmbBytes;
            int startFrame = Math.Max(0, animation.StartFrame);
            int frameCount = animation.EndFrameExclusive > animation.StartFrame
                ? animation.EndFrameExclusive - animation.StartFrame
                : Math.Max(1, Mathf.CeilToInt((float)(animation.TimelineDurationSeconds * frameRate)));
            // A generated Clip may have an authored four-channel contact track
            // that was edited after its KMB cache was written. Treat that track
            // as the source of truth for full analysis instead of allowing the
            // stale KMB contact payload to mask it.
            bool hasAuthoredFootContactTrack = KimodoTimelineFootContactSampler.HasAuthoredTrack(animation?.Clip);
            bool requiresFootContactEncoding = hasAuthoredFootContactTrack || motionBytes == null || motionBytes.Length == 0;
            if (!requiresFootContactEncoding &&
                KimodoRawMotionUtility.TryParseFlatBuffer(motionBytes, out KimodoRawMotionData existingMotion, out _) &&
                !existingMotion.HasFootContacts)
            {
                requiresFootContactEncoding = true;
            }
            if (requiresFootContactEncoding)
            {
                motionBytes = KimodoClipConstraintEncoder.EncodeTimeline(animation.TimelineClip, ResolveModelName(null), frameCount,
                    frameRate, 0, KimodoInOutConstraintMode.None, false, false, includeFootContacts: true);
                startFrame = 0;
            }
            if (KimodoRawMotionUtility.TryParseFlatBuffer(motionBytes, out KimodoRawMotionData motion, out _) && motion.FrameCount > 0)
            {
                // KMB uses the model's native time base (Kimodo is normally
                // 30 FPS), while a Timeline Session is fixed at 60 FPS. The
                // analysis contract is consumed by Session-frame renderers,
                // so analyze a time-base-aligned copy instead of treating
                // native model frame numbers as Session frame numbers.
                int sessionFrameCount = Math.Max(1, Mathf.RoundToInt(
                    (float)(animation.TimelineDurationSeconds * frameRate)));
                if (sessionFrameCount != motion.FrameCount ||
                    !Mathf.Approximately(motion.FrameRate, frameRate))
                {
                    if (!KimodoRawMotionUtility.TryResample(
                            motion,
                            frameRate,
                            sessionFrameCount,
                            out KimodoRawMotionData aligned,
                            out string resampleError))
                    {
                        throw new InvalidOperationException(
                            $"Analysis motion time-base alignment failed: {resampleError}");
                    }
                    motion = aligned;
                    motionBytes = KimodoRawMotionUtility.ToFlatBuffer(motion, ResolveModelName(null));
                    startFrame = 0;
                    frameCount = sessionFrameCount;
                }
                else
                {
                    startFrame = Mathf.Clamp(startFrame, 0, motion.FrameCount - 1);
                    frameCount = Mathf.Clamp(frameCount, 1, motion.FrameCount - startFrame);
                }
            }
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            var input = new KimodoEditorAnalysisInput
            {
                MotionBytes = motionBytes,
                StartFrame = startFrame,
                EndFrameExclusive = startFrame + frameCount,
                ModelName = ResolveModelName(null),
                TextEncoderMode = settings.DefaultTextEncoderMode,
                ModelsRoot = settings.LocalModelsPath?.Trim() ?? string.Empty,
                AnalysisOptionsJson = (analysisOptions ?? new JObject()).ToString(Formatting.None)
            };
            if (!KimodoPlayableClipGenerationExecutionService.Analysis(
                    input,
                    out string analysisJson,
                    out analysisMotionBytes,
                    out string error))
            {
                throw new InvalidOperationException(error);
            }
            JObject analysis = ParseAnalysisObject(analysisJson);
            analysis["source"] = "quickserver_analysis_only";
            analysis["command_fps"] = KimodoFrameTimeUtility.CommandFrameRate;
            return analysis;
        }
    }
}
