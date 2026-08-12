using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoClipConstraintMergeUtility
    {
        internal static bool TryMergeHumanoidFootIkMotion(
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
                        sample.leftFootPosition = constrainedSample.leftFootPosition;
                        sample.leftFootRotation = constrainedSample.leftFootRotation;
                    }
                    if (useRightFoot)
                    {
                        sample.rightFootPosition = constrainedSample.rightFootPosition;
                        sample.rightFootRotation = constrainedSample.rightFootRotation;
                    }
                    mergedSamples[frame] = sample;
                }

                if (!KimodoRetargetSamplingUtility.TryCreateTransientMuscleClip(
                        mergedSamples,
                        baseline.FrameRate,
                        out mergedClip,
                        out error) ||
                    !KimodoRetargetClipSamplingUtility.TryBuildClipSamplingContext(
                        mergedClip,
                        cache,
                        "KimodoClipConstraintFootTQOutput",
                        KimodoRetargetClipSamplingUtility.ClipSamplingMode.Humanoid,
                        out samplingContext,
                        out error,
                        applyMotionXToDelta: true))
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
                throw new InvalidOperationException("ClipConstraint merge requires two motion results.");
            }
            if (baseline.FrameCount != constrained.FrameCount ||
                baseline.JointCount != constrained.JointCount ||
                !Mathf.Approximately(baseline.FrameRate, constrained.FrameRate))
            {
                throw new InvalidOperationException(
                    $"ClipConstraint merge motion results do not have matching layouts. " +
                    $"baseline=[frames:{baseline.FrameCount}, joints:{baseline.JointCount}, fps:{baseline.FrameRate}], " +
                    $"constraint=[frames:{constrained.FrameCount}, joints:{constrained.JointCount}, fps:{constrained.FrameRate}].");
            }
            if (mask == null)
            {
                throw new InvalidOperationException("ClipConstraint merge requires a mask.");
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
                    throw new InvalidOperationException($"ClipConstraint merge cannot read root frame {frame}.");
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
                            $"ClipConstraint merge cannot read local rotation for joint '{jointNames[joint]}' at frame {frame}.");
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
            if (!KimodoRetargetToolsEditor.RecordIntoClip(
                    clip,
                    KimodoRawMotionUtility.ToCompactJson(motion),
                    KimodoPlayableClip.ResolveRecordSkeletonTypeFromModelName(modelName),
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
                throw new InvalidOperationException("ClipConstraint merge requires two motion results.");
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
                        $"ClipConstraint merge could not remove the runtime guard frame: {sliceError}");
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
                        $"ClipConstraint merge could not align motion timebases: {resampleError}");
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
                throw new InvalidOperationException("FullBody constraint recording requires motion keyframes.");
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
                    throw new InvalidOperationException($"FullBody constraint recording failed at frame {frame}: {error}");
                }
                samples.Add(sample);
            }

            return KimodoConstraintJsonExporter.ToConstraintsJson(
                samples,
                0.0,
                clipDurationSeconds ?? motion.DurationSeconds,
                motion.FrameRate);
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
    }
}
