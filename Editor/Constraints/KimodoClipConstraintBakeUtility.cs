using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using KimodoBridge;
using CharacterAnimationCli.Unity;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoClipConstraintBakeUtility
    {
        internal static bool TryMergeHumanoidFootEffectorMotion(
            KimodoRawMotionData baseline,
            KimodoRawMotionData constrained,
            KimodoClipConstraintMask mask,
            string modelName,
            out KimodoRawMotionData merged,
            out string error)
        {
            merged = null;
            error = string.Empty;
            if (!TryResolveFootMask(mask, out bool useLeftFoot, out bool useRightFoot))
            {
                return false;
            }

            if (baseline == null || constrained == null ||
                baseline.FrameCount != constrained.FrameCount ||
                !Mathf.Approximately(baseline.FrameRate, constrained.FrameRate))
            {
                error = "Humanoid FootT/Q merge requires matching frame counts and frame rates.";
                return false;
            }

            Avatar avatar = null;
            SkeletonCache cache = null;
            AnimationClip baselineClip = null;
            AnimationClip constrainedClip = null;
            AnimationClip mergedClip = null;
            KimodoRetargetClipSamplingUtility.ClipSamplingContext samplingContext = null;
            try
            {
                if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                        modelName,
                        out avatar,
                        out error) ||
                    !KimodoRetargetCoreUtility.IsValidHumanoid(avatar))
                {
                    if (string.IsNullOrWhiteSpace(error))
                    {
                        error = $"Model '{modelName}' has no valid Humanoid avatar.";
                    }
                    return false;
                }
                if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                        avatar,
                        "KimodoClipConstraintFootTQ",
                        out cache,
                        out error))
                {
                    return false;
                }
                if (!TryCreateRawMotionClip(baseline, modelName, out baselineClip, out error) ||
                    !TryCreateRawMotionClip(constrained, modelName, out constrainedClip, out error))
                {
                    return false;
                }

                if (!KimodoRetargetSamplingUtility.TryCollectMuscleSamplesFromClip(
                        baselineClip,
                        cache,
                        baseline.FrameCount,
                        KimodoRetargetClipSamplingUtility.ClipSamplingMode.RawTransform,
                        out MuscleSample[] baselineSamples,
                        out error) ||
                    !KimodoRetargetSamplingUtility.TryCollectMuscleSamplesFromClip(
                        constrainedClip,
                        cache,
                        constrained.FrameCount,
                        KimodoRetargetClipSamplingUtility.ClipSamplingMode.RawTransform,
                        out MuscleSample[] constrainedSamples,
                        out error))
                {
                    return false;
                }

                var mergedSamples = new MuscleSample[baselineSamples.Length];
                for (int frame = 0; frame < mergedSamples.Length; frame++)
                {
                    MuscleSample sample = KimodoRetargetSamplingUtility.CloneMuscleSample(baselineSamples[frame]);
                    MuscleSample constrainedSample = constrainedSamples[frame];
                    if (useLeftFoot)
                    {
                        constrainedSample.GetLeftFoot(out Vector3 position, out Quaternion rotation);
                        sample.SetLeftFoot(position, rotation);
                    }
                    if (useRightFoot)
                    {
                        constrainedSample.GetRightFoot(out Vector3 position, out Quaternion rotation);
                        sample.SetRightFoot(position, rotation);
                    }
                    mergedSamples[frame] = sample;
                }

                if (!KimodoRetargetSamplingUtility.TryCreateTransientMuscleClip(
                        mergedSamples,
                        baseline.FrameRate,
                        out mergedClip,
                        out error) ||
                    !KimodoRetargetClipSamplingUtility.TryBuildHumanoidClipSamplingContext(
                        mergedClip,
                        cache,
                        "KimodoClipConstraintFootTQOutput",
                        KimodoRetargetClipSamplingUtility.ClipSamplingMode.Humanoid,
                        out samplingContext,
                        out error,
                        applyMotionXToDelta: true,
                        includeLeftFootEffector: useLeftFoot,
                        includeRightFootEffector: useRightFoot))
                {
                    return false;
                }

                if (!KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                        modelName,
                        cache.skeletonRoot,
                        out string[] jointNames,
                        out int[] jointParents,
                        out Transform[] joints,
                        out error))
                {
                    return false;
                }

                var roots = new Vector3[baseline.FrameCount];
                var rotations = new List<float>(baseline.FrameCount * jointNames.Length * 4);
                float fps = Mathf.Max(1f, baseline.FrameRate);
                for (int frame = 0; frame < baseline.FrameCount; frame++)
                {
                    if (!KimodoRetargetClipSamplingUtility.TryEvaluateClipSamplingContext(
                            samplingContext,
                            frame / fps,
                            out error))
                    {
                        return false;
                    }

                    Transform root = joints[0];
                    if (root == null)
                    {
                        error = "Humanoid FootT/Q merge profile root is missing.";
                        return false;
                    }
                    roots[frame] = root.position;
                    for (int joint = 0; joint < joints.Length; joint++)
                    {
                        Quaternion rotation = joint == 0
                            ? joints[joint].rotation
                            : joints[joint] != null
                                ? joints[joint].localRotation
                                : Quaternion.identity;
                        rotation = rotation.normalized;
                        rotations.Add(rotation.w);
                        rotations.Add(rotation.x);
                        rotations.Add(-rotation.y);
                        rotations.Add(-rotation.z);
                    }
                }

                merged = new KimodoRawMotionData(
                    baseline.FrameCount,
                    jointNames.Length,
                    baseline.FrameRate,
                    jointNames,
                    jointParents,
                    roots,
                    rotations,
                    rootJointIndex: 0,
                    baseline.HasFootContacts ? (byte[])baseline.footContacts.Clone() : null);
                return true;
            }
            finally
            {
                samplingContext?.Dispose();
                cache?.Dispose();
                DestroyTransientClip(mergedClip);
                DestroyTransientClip(constrainedClip);
                DestroyTransientClip(baselineClip);
            }
        }

        internal static KimodoRawMotionData MergeMaskedMotion(
            KimodoRawMotionData baseline,
            KimodoRawMotionData constrained,
            KimodoClipConstraintMask mask)
        {
            if (baseline == null || constrained == null)
            {
                throw new InvalidOperationException("ClipConstraint bake requires two motion results.");
            }
            if (baseline.FrameCount != constrained.FrameCount ||
                baseline.JointCount != constrained.JointCount ||
                !Mathf.Approximately(baseline.FrameRate, constrained.FrameRate))
            {
                throw new InvalidOperationException(
                    $"ClipConstraint bake motion results do not have matching layouts. " +
                    $"baseline=[frames:{baseline.FrameCount}, joints:{baseline.JointCount}, fps:{baseline.FrameRate}], " +
                    $"constraint=[frames:{constrained.FrameCount}, joints:{constrained.JointCount}, fps:{constrained.FrameRate}].");
            }
            if (mask == null)
            {
                throw new InvalidOperationException("ClipConstraint bake requires a mask.");
            }

            var joints = new Dictionary<string, KimodoClipConstraintJointMask>(StringComparer.OrdinalIgnoreCase);
            foreach (KimodoClipConstraintJointMask joint in mask.joints ?? new List<KimodoClipConstraintJointMask>())
            {
                if (joint != null && !string.IsNullOrWhiteSpace(joint.jointName))
                {
                    joints[joint.jointName] = joint;
                }
            }

            int jointCount = baseline.JointCount;
            var jointNames = new string[jointCount];
            for (int joint = 0; joint < jointCount; joint++)
            {
                jointNames[joint] = baseline.JointNames[joint];
            }

            var roots = new Vector3[baseline.FrameCount];
            var rotations = new List<float>(baseline.FrameCount * jointCount * 4);
            bool useConstrainedRootRotation = mask.rootRotation || mask.rootHeading;
            for (int frame = 0; frame < baseline.FrameCount; frame++)
            {
                if (!baseline.TryReadUnityRootPosition(frame, out Vector3 baselineRoot) ||
                    !constrained.TryReadUnityRootPosition(frame, out Vector3 constrainedRoot))
                {
                    throw new InvalidOperationException($"ClipConstraint bake cannot read root frame {frame}.");
                }
                roots[frame] = new Vector3(
                    mask.rootPosition?.x == true ? constrainedRoot.x : baselineRoot.x,
                    mask.rootPosition?.y == true ? constrainedRoot.y : baselineRoot.y,
                    mask.rootPosition?.z == true ? constrainedRoot.z : baselineRoot.z);

                for (int joint = 0; joint < jointCount; joint++)
                {
                    bool useConstrained = joint == 0
                        ? useConstrainedRootRotation
                        : joints.TryGetValue(jointNames[joint], out KimodoClipConstraintJointMask item) &&
                          (item.rotation || HasPositionAxis(item.position));
                    KimodoRawMotionData source = useConstrained ? constrained : baseline;
                    if (!source.TryReadUnityLocalRotation(frame, joint, jointCount, out Quaternion rotation))
                    {
                        throw new InvalidOperationException(
                            $"ClipConstraint bake cannot read local rotation for joint '{jointNames[joint]}' at frame {frame}.");
                    }
                    rotations.Add(rotation.w);
                    rotations.Add(rotation.x);
                    rotations.Add(-rotation.y);
                    rotations.Add(-rotation.z);
                }
            }

            byte[] footContacts = null;
            if (baseline.HasFootContacts)
            {
                footContacts = new byte[baseline.FrameCount * KimodoFootContactTrackUtility.ChannelCount];
                for (int frame = 0; frame < baseline.FrameCount; frame++)
                {
                    for (int channel = 0; channel < KimodoFootContactTrackUtility.ChannelCount; channel++)
                    {
                        baseline.TryReadFootContact(frame, channel, out float value);
                        footContacts[frame * KimodoFootContactTrackUtility.ChannelCount + channel] =
                            value >= 0.5f ? (byte)1 : (byte)0;
                    }
                }
            }

            return new KimodoRawMotionData(
                baseline.FrameCount,
                jointCount,
                baseline.FrameRate,
                jointNames,
                CopyParents(baseline, jointCount),
                roots,
                rotations,
                baseline.RootJointIndex,
                footContacts);
        }

        private static bool TryResolveFootMask(
            KimodoClipConstraintMask mask,
            out bool useLeftFoot,
            out bool useRightFoot)
        {
            useLeftFoot = false;
            useRightFoot = false;
            if (mask == null || mask.rootPosition != null &&
                (mask.rootPosition.x || mask.rootPosition.y || mask.rootPosition.z) ||
                mask.rootHeading || mask.rootRotation)
            {
                return false;
            }

            bool hasNonFootJoint = false;
            foreach (KimodoClipConstraintJointMask joint in mask.joints ?? new List<KimodoClipConstraintJointMask>())
            {
                if (joint == null || !HasPositionAxis(joint.position) && !joint.rotation)
                {
                    continue;
                }

                string name = joint.jointName ?? string.Empty;
                bool isLeftFoot = name.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (name.IndexOf("foot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     name.IndexOf("toe", StringComparison.OrdinalIgnoreCase) >= 0);
                bool isRightFoot = name.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (name.IndexOf("foot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     name.IndexOf("toe", StringComparison.OrdinalIgnoreCase) >= 0);
                if (isLeftFoot)
                {
                    useLeftFoot = true;
                }
                else if (isRightFoot)
                {
                    useRightFoot = true;
                }
                else
                {
                    hasNonFootJoint = true;
                }
            }

            return !hasNonFootJoint && (useLeftFoot || useRightFoot);
        }

        private static bool TryCreateRawMotionClip(
            KimodoRawMotionData motion,
            string modelName,
            out AnimationClip clip,
            out string error)
        {
            clip = new AnimationClip
            {
                name = "KimodoClipConstraintRawMotion",
                legacy = false,
                frameRate = motion.FrameRate
            };
            if (!KimodoRetargetToolsEditor.BakeIntoClip(
                    clip,
                    KimodoRawMotionUtility.ToCompactJson(motion),
                    KimodoMotionModelProfiles.ResolveBakeSkeletonType(modelName),
                    modelName,
                    null,
                    out error))
            {
                DestroyTransientClip(clip);
                clip = null;
                return false;
            }
            return true;
        }

        private static void DestroyTransientClip(AnimationClip clip)
        {
            if (clip != null)
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        internal static KimodoRawMotionData AlignConstraintMotion(
            KimodoRawMotionData baseline,
            KimodoRawMotionData constraint,
            int trimStartFrame)
        {
            if (baseline == null || constraint == null)
            {
                throw new InvalidOperationException("ClipConstraint bake requires two motion results.");
            }

            KimodoRawMotionData aligned = constraint;
            if (trimStartFrame > 0 &&
                constraint.FrameCount >= trimStartFrame + baseline.FrameCount)
            {
                if (!KimodoRawMotionUtility.TrySlice(
                        constraint,
                        trimStartFrame,
                        baseline.FrameCount,
                        out aligned,
                        out string sliceError))
                {
                    throw new InvalidOperationException(
                        $"ClipConstraint bake could not remove the runtime guard frame: {sliceError}");
                }
            }

            if (aligned.FrameCount != baseline.FrameCount ||
                !Mathf.Approximately(aligned.FrameRate, baseline.FrameRate))
            {
                if (!KimodoRawMotionUtility.TryResample(
                        aligned,
                        baseline.FrameRate,
                        baseline.FrameCount,
                        out KimodoRawMotionData resampled,
                        out string resampleError))
                {
                    throw new InvalidOperationException(
                        $"ClipConstraint bake could not align motion timebases: {resampleError}");
                }
                aligned = resampled;
            }

            return aligned;
        }

        internal static string BuildFullBodyConstraintsJson(
            KimodoRawMotionData motion,
            string modelName,
            IReadOnlyList<int> frames,
            double sampleTimeOffsetSeconds = 0.0,
            double? clipDurationSeconds = null)
        {
            if (motion == null || frames == null || frames.Count == 0)
            {
                throw new InvalidOperationException("FullBody bake requires motion keyframes.");
            }

            var samples = new List<KimodoMarkerSampleResult>(frames.Count);
            for (int index = 0; index < frames.Count; index++)
            {
                int frame = Mathf.Clamp(frames[index], 0, Mathf.Max(0, motion.FrameCount - 1));
                if (!KimodoRawMotionUtility.TryExtractMarkerSample(
                        motion,
                        modelName,
                        frame,
                        out KimodoMarkerSampleResult sample,
                        out string error,
                        "fullbody",
                        sampleTimeOffsetSeconds + frame / motion.FrameRate))
                {
                    throw new InvalidOperationException($"FullBody bake sample failed at frame {frame}: {error}");
                }
                samples.Add(sample);
            }

            return KimodoConstraintJsonExporter.ToConstraintsJson(
                samples,
                ResolveExportContext(modelName),
                0.0,
                clipDurationSeconds ?? motion.DurationSeconds,
                motion.FrameRate);
        }

        internal static string BuildLoopConstraintJson(
            KimodoMarkerSampleResult firstFrame,
            KimodoMarkerSampleResult tailFrame,
            string modelName,
            int runtimeTrimStartFrame,
            int targetFrameCount,
            int runtimeFrameCount,
            float frameRate)
        {
            List<KimodoMarkerSampleResult> samples = BuildLoopConstraintSamples(
                firstFrame,
                tailFrame,
                runtimeTrimStartFrame,
                targetFrameCount,
                runtimeFrameCount,
                frameRate);
            KimodoMarkerSampleResult head = samples[0];
            KimodoMarkerSampleResult visibleStart = samples[1];
            KimodoMarkerSampleResult terminal = samples[2];
            KimodoMarkerSampleResult tail = samples[3];
            int terminalFrame = runtimeTrimStartFrame + targetFrameCount - 1;
            KimodoPlayableClipGenerationSettings.DebugLog(
                $"[Kimodo][GenerateLoop] loop anchors and visible FullBody boundaries: " +
                $"head frame=0 posXZ=({head.root2DOverride.t.x:F4}, {head.root2DOverride.t.z:F4}) " +
                $"rotationY={head.root2DOverride.q.eulerAngles.y:F3}°, " +
                $"visible start FullBody frame={runtimeTrimStartFrame}, " +
                $"terminal FullBody frame={terminalFrame}, " +
                $"tail frame={runtimeFrameCount - 1} posXZ=({tail.root2DOverride.t.x:F4}, {tail.root2DOverride.t.z:F4}) " +
                $"rotationY={tail.root2DOverride.q.eulerAngles.y:F3}°.");
            return KimodoConstraintJsonExporter.ToConstraintsJson(
                samples,
                ResolveExportContext(modelName),
                0.0,
                runtimeFrameCount / (double)frameRate,
                frameRate);
        }

        internal static List<KimodoMarkerSampleResult> BuildLoopConstraintSamples(
            KimodoMarkerSampleResult firstFrame,
            KimodoMarkerSampleResult tailFrame,
            int runtimeTrimStartFrame,
            int targetFrameCount,
            int runtimeFrameCount,
            float frameRate)
        {
            if (!TryGetRoot2D(firstFrame, out CharacterPoseTransform firstRoot) ||
                !TryGetRoot2D(tailFrame, out CharacterPoseTransform tailRoot))
            {
                throw new InvalidOperationException("Loop constraint requires explicit world root2D overrides.");
            }
            if (runtimeTrimStartFrame < 0 || targetFrameCount <= 1 || runtimeFrameCount <= 0 || frameRate <= 0f)
            {
                throw new InvalidOperationException("Loop constraint frame range is invalid.");
            }

            int terminalFrame = runtimeTrimStartFrame + targetFrameCount - 1;
            int virtualTailFrame = runtimeFrameCount - 1;
            if (terminalFrame >= runtimeFrameCount)
            {
                throw new InvalidOperationException("Loop terminal frame is outside the runtime range.");
            }

            Vector3 planarDelta = tailRoot.t - firstRoot.t;
            planarDelta.y = 0f;
            float sourceSpanFrames = targetFrameCount - 1f;
            float headRatio = runtimeTrimStartFrame / sourceSpanFrames;
            float tailRatio = (virtualTailFrame - terminalFrame) / sourceSpanFrames;
            Vector3 virtualHeadPosition = firstRoot.t - planarDelta * headRatio;
            Vector3 virtualTailPosition = tailRoot.t + planarDelta * tailRatio;
            virtualHeadPosition.y = firstRoot.t.y;
            virtualTailPosition.y = tailRoot.t.y;

            float firstYaw = KimodoConstraintNormalizationUtility.ResolvePlanarRotation(firstRoot.q).eulerAngles.y;
            float tailYaw = KimodoConstraintNormalizationUtility.ResolvePlanarRotation(tailRoot.q).eulerAngles.y;
            float yawDelta = Mathf.DeltaAngle(firstYaw, tailYaw);
            Quaternion virtualHeadHeading = Quaternion.Euler(0f, firstYaw - yawDelta * headRatio, 0f);
            Quaternion virtualTailHeading = Quaternion.Euler(0f, tailYaw + yawDelta * tailRatio, 0f);

            return new List<KimodoMarkerSampleResult>
            {
                BuildLoopRoot2DConstraintSample(firstFrame, virtualHeadPosition, virtualHeadHeading, 0.0),
                BuildLoopFullBodyConstraintSample(
                    firstFrame,
                    runtimeTrimStartFrame / (double)frameRate),
                BuildLoopTerminalConstraintSample(firstFrame, tailFrame, terminalFrame / (double)frameRate),
                BuildLoopRoot2DConstraintSample(
                    tailFrame,
                    virtualTailPosition,
                    virtualTailHeading,
                    virtualTailFrame / (double)frameRate)
            };
        }

        internal static KimodoMarkerSampleResult BuildLoopTerminalConstraintSample(
            KimodoMarkerSampleResult firstFrame,
            KimodoMarkerSampleResult tailFrame,
            double sampleTimeSeconds)
        {
            if (!TryGetRoot2D(firstFrame, out _) ||
                !TryGetRoot2D(tailFrame, out CharacterPoseTransform tailRoot))
            {
                throw new InvalidOperationException("Loop terminal constraint requires explicit world root2D overrides.");
            }

            KimodoMarkerSampleResult sample = BuildLoopFullBodyConstraintSample(firstFrame, sampleTimeSeconds);
            MergeRoot2DIntoFullBody(sample, tailRoot);
            return sample;
        }

        private static KimodoMarkerSampleResult BuildLoopFullBodyConstraintSample(
            KimodoMarkerSampleResult source,
            double sampleTimeSeconds)
        {
            KimodoMarkerSampleResult sample = source.Clone();
            sample.constraintType = "fullbody";
            sample.mask = KimodoConstraintMask.ForType("fullbody");
            sample.sampleTime = sampleTimeSeconds;
            sample.root2DOverride = null;
            sample.enableMask.root2DPosition = false;
            sample.enableMask.root2DHeading = false;
            return sample;
        }

        private static void MergeRoot2DIntoFullBody(
            KimodoMarkerSampleResult fullBody,
            CharacterAnimationCli.Unity.CharacterPoseTransform root2D)
        {
            if (fullBody == null || root2D == null)
            {
                throw new InvalidOperationException("Loop FullBody/Root2D merge requires a world root2D override.");
            }

            fullBody.root2DOverride = new CharacterAnimationCli.Unity.CharacterPoseTransform
            {
                t = root2D.t,
                q = root2D.q
            };
            fullBody.enableMask ??= new KimodoSampleChannelMask();
            fullBody.enableMask.muscle49 = true;
            fullBody.enableMask.rootTQ = true;
            fullBody.enableMask.leftFootTQ = true;
            fullBody.enableMask.rightFootTQ = true;
            fullBody.enableMask.root2DPosition = true;
            fullBody.enableMask.root2DHeading = true;
        }

        private static KimodoMarkerSampleResult BuildLoopRoot2DConstraintSample(
            KimodoMarkerSampleResult source,
            Vector3 position,
            Quaternion heading,
            double sampleTimeSeconds)
        {
            KimodoMarkerSampleResult sample = source.Clone();
            sample.constraintType = "root2d";
            sample.mask = KimodoConstraintMask.ForType("root2d");
            sample.sampleTime = sampleTimeSeconds;
            sample.root2DOverride = new CharacterAnimationCli.Unity.CharacterPoseTransform
            {
                t = position,
                q = KimodoConstraintNormalizationUtility.ResolvePlanarRotation(heading)
            };
            sample.enableMask.root2DPosition = true;
            sample.enableMask.root2DHeading = true;
            sample.enableMask.muscle49 = false;
            sample.enableMask.rootTQ = false;
            sample.enableMask.leftFootTQ = false;
            sample.enableMask.rightFootTQ = false;
            return sample;
        }

        private static bool TryGetRoot2D(
            KimodoMarkerSampleResult sample,
            out CharacterAnimationCli.Unity.CharacterPoseTransform root)
        {
            root = sample?.root2DOverride;
            return sample != null && sample.enableMask?.root2DPosition == true && root != null;
        }

        internal static string AppendConstraintsJson(string baseJson, string additionalJson)
        {
            var output = new JArray();
            AppendJson(output, baseJson);
            AppendJson(output, additionalJson);
            return output.Count == 0 ? string.Empty : output.ToString(Formatting.None);
        }

        private static bool HasPositionAxis(KimodoClipConstraintPositionMask position)
        {
            return position != null && (position.x || position.y || position.z);
        }

        private static int[] CopyParents(KimodoRawMotionData motion, int jointCount)
        {
            var parents = new int[jointCount];
            for (int joint = 0; joint < jointCount; joint++)
            {
                parents[joint] = joint < motion.jointParents.Length
                    ? motion.jointParents[joint]
                    : joint == 0 ? -1 : joint - 1;
            }
            return parents;
        }

        private static void AppendJson(JArray output, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }
            JToken token = JToken.Parse(json);
            if (token is JArray array)
            {
                foreach (JToken item in array)
                {
                    output.Add(item.DeepClone());
                }
                return;
            }
            if (token is JObject obj)
            {
                output.Add(obj.DeepClone());
                return;
            }
            throw new InvalidOperationException("Constraint JSON must be an array or object.");
        }
            private static KimodoConstraintExportContext ResolveExportContext(string modelName)
        {
            return KimodoRetargetMarkerSamplingUtility.TryResolveTargetAvatar(null, modelName, out Avatar avatar, out _)
                ? new KimodoConstraintExportContext(
                    KimodoConstraintNormalizationUtility.ResolveHumanScale(avatar),
                    KimodoConstraintExportProjector.Create(modelName))
                : new KimodoConstraintExportContext();
        }
}

}
