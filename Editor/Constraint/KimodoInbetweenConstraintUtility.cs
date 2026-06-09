using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoInbetweenConstraintUtility
    {
        private const string LogPrefix = "[Kimodo][InbetweenConstraint]";
        private const double NeighborSampleDeltaSeconds = 1.0 / 60.0;
        private const float PelvisCompensationThreshold = 0.1f;
        private const int FootSwitchFrameThreshold = 5;
        private const float InClipClusterThreshold = 0.1f;

        private enum SupportFootSide
        {
            Unknown = 0,
            Left = 1,
            Right = 2
        }

        public static bool TryBuildConstraintsJson(
            TimelineClip sourceClip,
            bool enableInbetweenInterpolation,
            bool normalizeConstraintOrigin,
            bool enableInClipRootMotionCompensation,
            int generationFrames,
            out string constraintsJson,
            out string error)
        {
            constraintsJson = string.Empty;
            error = string.Empty;

            if (!TryBuildMarkerSamplesForExport(sourceClip, out List<KimodoMarkerSampleResult> samples, out error))
            {
                return false;
            }

            if (enableInClipRootMotionCompensation)
            {
                if (!TryApplyInClipRootMotionCompensation(samples, sourceClip, out string compensationWarning, out error))
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(compensationWarning))
                {
                    Debug.LogWarning($"{LogPrefix} {compensationWarning}");
                }
            }

            if (enableInbetweenInterpolation)
            {
                if (!TryAddAutoInbetweenSamples(sourceClip, Mathf.Max(1, generationFrames), samples, out string warning))
                {
                    if (!string.IsNullOrWhiteSpace(warning))
                    {
                        Debug.LogWarning($"{LogPrefix} {warning}");
                    }
                }
            }

            if (normalizeConstraintOrigin)
            {
                NormalizeConstraintOrigin(samples);
            }

            constraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                samples,
                clipStartSeconds: sourceClip.start,
                clipDurationSeconds: sourceClip.duration);
            return true;
        }

        internal static bool TryBuildMarkerSamplesForExport(
            TimelineClip sourceClip,
            out List<KimodoMarkerSampleResult> samples,
            out string error)
        {
            samples = new List<KimodoMarkerSampleResult>();
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "No selected timeline clip for constraint export.";
                return false;
            }

            TrackAsset track = sourceClip.GetParentTrack();
            if (track == null)
            {
                error = "Cannot resolve parent animation track.";
                return false;
            }

            List<KimodoConstraintMarkerBase> markers = GatherKimodoMarkers(track, sourceClip);
            if (markers.Count == 0)
            {
                return true;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                error = "Timeline inspected director is null.";
                return false;
            }

            Animator animator = director.GetGenericBinding(track) as Animator;
            if (animator == null)
            {
                error = "Animation track has no Animator binding.";
                return false;
            }

            double originalTime = director.time;
            DirectorWrapMode originalWrap = director.extrapolationMode;

            try
            {
                director.extrapolationMode = DirectorWrapMode.Hold;
                for (int i = 0; i < markers.Count; i++)
                {
                    if (!TryBuildMarkerSample(markers[i], sourceClip, animator, director, out KimodoMarkerSampleResult sample, out error))
                    {
                        return false;
                    }

                    samples.Add(sample);
                }
            }
            finally
            {
                director.time = originalTime;
                director.Evaluate();
                director.extrapolationMode = originalWrap;
            }

            return true;
        }

        internal static bool TrySamplePoseFromClipAsset(
            TimelineClip sourceClip,
            Animator animator,
            double timelineTime,
            string markerType,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "Source clip is null.";
                return false;
            }

            TrackAsset track = sourceClip.GetParentTrack();
            if (track == null)
            {
                error = "Parent track not found.";
                return false;
            }

            if (animator == null)
            {
                error = "Animation track has no Animator binding.";
                return false;
            }

            if (!KimodoMarkerSamplingUtility.TryResolveAnimationClipFromTimelineClip(sourceClip, out AnimationClip resolvedClip, out error))
            {
                return false;
            }

            KimodoLocalAvatarUtility.AvatarResolveResult avatarResult = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(animator.gameObject);
            Avatar sourceAvatar = avatarResult.Avatar;
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(sourceAvatar))
            {
                error = $"Resolve source avatar failed: {avatarResult.Error}";
                return false;
            }

            if (!KimodoRetargetToolsEditor.TrySampleMarkerForClip(
                    resolvedClip,
                    markerType,
                    timelineTime,
                    sourceAvatar,
                    null,
                    animator,
                    string.IsNullOrWhiteSpace(((KimodoPlayableClip)sourceClip.asset)?.bridgeModelName) ? "Kimodo-SOMA-RP-v1" : ((KimodoPlayableClip)sourceClip.asset).bridgeModelName.Trim(),
                    forceRefresh: false,
                    out KimodoMarkerSampleResult sampledPose,
                    out error))
            {
                return false;
            }

            sample = sampledPose;
            sample.constraintType = markerType ?? string.Empty;
            sample.sampleTime = timelineTime;
            return true;
        }

        private static bool TryApplyInClipRootMotionCompensation(
            List<KimodoMarkerSampleResult> samples,
            TimelineClip sourceClip,
            out string warning,
            out string error)
        {
            warning = string.Empty;
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "source clip is null.";
                return false;
            }

            if (samples == null || samples.Count == 0)
            {
                return true;
            }

            string modelName = string.IsNullOrWhiteSpace(((KimodoPlayableClip)sourceClip.asset)?.bridgeModelName)
                ? "Kimodo-SOMA-RP-v1"
                : ((KimodoPlayableClip)sourceClip.asset).bridgeModelName.Trim();

            if (!TryBuildPoseRigRootMotionCompensation(
                    samples,
                    modelName,
                    out Vector3[] compensatedRootPositions,
                    out warning,
                    out error))
            {
                return false;
            }

            if (compensatedRootPositions == null || compensatedRootPositions.Length == 0)
            {
                return true;
            }

            for (int i = 0; i < samples.Count && i < compensatedRootPositions.Length; i++)
            {
                KimodoMarkerSampleResult sample = samples[i];
                if (sample == null)
                {
                    continue;
                }

                sample.rootPosition = compensatedRootPositions[i];
            }

            return true;
        }

        private static bool TryBuildPoseRigRootMotionCompensation(
            List<KimodoMarkerSampleResult> samples,
            string modelName,
            out Vector3[] compensatedRootPositions,
            out string warning,
            out string error)
        {
            compensatedRootPositions = null;
            warning = string.Empty;
            error = string.Empty;

            if (samples == null || samples.Count == 0)
            {
                return true;
            }

            int anchorIndex = ResolveConstraintOriginAnchorIndex(samples);
            if (anchorIndex < 0 || anchorIndex >= samples.Count || samples[anchorIndex] == null)
            {
                return true;
            }

            Vector3 anchorRoot = samples[anchorIndex].rootPosition;
            float maxDistanceSq = 0f;
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult sample = samples[i];
                if (sample == null)
                {
                    continue;
                }

                Vector3 delta = sample.rootPosition - anchorRoot;
                delta.y = 0f;
                maxDistanceSq = Mathf.Max(maxDistanceSq, delta.sqrMagnitude);
            }

            if (maxDistanceSq > InClipClusterThreshold * InClipClusterThreshold)
            {
                return true;
            }

            KimodoConstraintPoseRigFactory.PoseRigInstance rigInstance = null;
            try
            {
                if (!KimodoConstraintPoseRigFactory.TryCreatePoseRig(modelName, 0, 0, out rigInstance, out error))
                {
                    return false;
                }

                compensatedRootPositions = new Vector3[samples.Count];
                Vector3 cumulativeDelta = Vector3.zero;
                SupportFootSide activeFoot = SupportFootSide.Unknown;
                SupportFootSide pendingFoot = SupportFootSide.Unknown;
                int pendingFootFrames = 0;
                Vector3 previousFootWorldPosition = Vector3.zero;
                bool hasPreviousFootWorldPosition = false;

                for (int i = 0; i < samples.Count; i++)
                {
                    KimodoMarkerSampleResult sample = samples[i];
                    if (sample == null)
                    {
                        compensatedRootPositions[i] = Vector3.zero;
                        continue;
                    }

                    if (!KimodoConstraintPoseRigFactory.TryApplySampleToPoseRig(sample, modelName, rigInstance, out error))
                    {
                        return false;
                    }

                    if (!KimodoConstraintPoseRigFactory.TryResolveFootWorldPositions(
                            rigInstance,
                            modelName,
                            out Vector3 leftFootPosition,
                            out Vector3 rightFootPosition,
                            out error))
                    {
                        return false;
                    }

                    float leftHeight = leftFootPosition.y;
                    float rightHeight = rightFootPosition.y;
                    SupportFootSide candidateFoot = ResolveHigherFootSide(leftHeight, rightHeight, activeFoot);

                    if (activeFoot == SupportFootSide.Unknown)
                    {
                        activeFoot = candidateFoot;
                        pendingFoot = SupportFootSide.Unknown;
                        pendingFootFrames = 0;
                        hasPreviousFootWorldPosition = false;
                    }
                    else if (candidateFoot != activeFoot)
                    {
                        if (pendingFoot == candidateFoot)
                        {
                            pendingFootFrames++;
                        }
                        else
                        {
                            pendingFoot = candidateFoot;
                            pendingFootFrames = 1;
                        }

                        if (pendingFootFrames >= FootSwitchFrameThreshold)
                        {
                            activeFoot = candidateFoot;
                            pendingFoot = SupportFootSide.Unknown;
                            pendingFootFrames = 0;
                            hasPreviousFootWorldPosition = false;
                            previousFootWorldPosition = Vector3.zero;
                            compensatedRootPositions[i] = sample.rootPosition + cumulativeDelta;
                            continue;
                        }
                    }
                    else
                    {
                        pendingFoot = SupportFootSide.Unknown;
                        pendingFootFrames = 0;
                    }

                    Vector3 currentFootWorldPosition = activeFoot == SupportFootSide.Left
                        ? leftFootPosition
                        : rightFootPosition;
                    if (currentFootWorldPosition == Vector3.zero)
                    {
                        compensatedRootPositions[i] = sample.rootPosition + cumulativeDelta;
                        continue;
                    }

                    if (hasPreviousFootWorldPosition)
                    {
                        Vector3 delta = currentFootWorldPosition - previousFootWorldPosition;
                        delta.y = 0f;
                        cumulativeDelta += delta;
                    }

                    previousFootWorldPosition = currentFootWorldPosition;
                    hasPreviousFootWorldPosition = true;
                    compensatedRootPositions[i] = sample.rootPosition + cumulativeDelta;
                }

                warning = string.Empty;
                return true;
            }
            finally
            {
                KimodoConstraintPoseRigFactory.DestroyPoseRig(rigInstance);
            }
        }

        private static bool TryBuildMarkerSample(
            KimodoConstraintMarkerBase marker,
            TimelineClip sourceClip,
            Animator animator,
            PlayableDirector director,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (marker == null)
            {
                error = "Marker is null.";
                return false;
            }

            bool isCustomEndEffector = marker is KimodoEndEffectorConstraintMarker ee &&
                                       string.Equals(ee.ConstraintType, "end-effector", StringComparison.OrdinalIgnoreCase);
            if (marker.useOverride && !isCustomEndEffector)
            {
                sample = KimodoMarkerSamplingUtility.NormalizeConstraintMarkerSample(marker, marker.SampleData);
                if (sample == null)
                {
                    error = "failed to read override marker data";
                    return false;
                }

                return true;
            }

            double sampleTime = marker.time;
            director.time = sampleTime;
            director.Evaluate();

            if (!TrySamplePoseFromClipAsset(
                    sourceClip,
                    animator,
                    sampleTime,
                    marker.ConstraintType,
                    out KimodoMarkerSampleResult captured,
                    out error))
            {
                return false;
            }

            captured.sampleTime = sampleTime;
            sample = KimodoMarkerSamplingUtility.NormalizeConstraintMarkerSample(marker, captured);
            if (sample == null)
            {
                error = "failed to map sampled pose to marker sample data";
                return false;
            }

            return true;
        }

        private static bool TryAddAutoInbetweenSamples(
            TimelineClip sourceClip,
            int generationFrames,
            List<KimodoMarkerSampleResult> samples,
            out string warning)
        {
            warning = string.Empty;
            if (sourceClip == null)
            {
                warning = "source clip is null, skip inbetween interpolation.";
                return false;
            }

            TrackAsset track = sourceClip.GetParentTrack();
            if (track == null)
            {
                warning = "cannot resolve parent track, skip inbetween interpolation.";
                return false;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                warning = "Timeline inspected director is null, skip inbetween interpolation.";
                return false;
            }

            Animator animator = director.GetGenericBinding(track) as Animator;
            if (animator == null)
            {
                warning = "track has no Animator binding, skip inbetween interpolation.";
                return false;
            }

            FindNeighborClips(sourceClip, out TimelineClip leftNeighbor, out TimelineClip rightNeighbor);
            if (leftNeighbor == null && rightNeighbor == null)
            {
                warning = "no neighboring clips found, skip inbetween interpolation.";
                return true;
            }

            var occupiedManualTimes = new HashSet<long>();
            CollectManualTimes(samples, occupiedManualTimes);

            double originalTime = director.time;
            DirectorWrapMode originalWrapMode = director.extrapolationMode;

            try
            {
                director.extrapolationMode = DirectorWrapMode.Hold;

                if (leftNeighbor != null && !occupiedManualTimes.Contains(ToTimeKey(0.0)))
                {
                    double evalTime = Math.Max(leftNeighbor.start, leftNeighbor.end - NeighborSampleDeltaSeconds);
                    if (TryCapturePoseAtTime(leftNeighbor, director, animator, evalTime, "fullbody", out KimodoMarkerSampleResult pose, out string captureError))
                    {
                        samples.Add(pose);
                    }
                    else
                    {
                        Debug.LogWarning($"{LogPrefix} Failed to sample left neighbor end pose: {captureError}");
                    }
                }

                int endFrame = Math.Max(0, generationFrames - 1);
                if (rightNeighbor != null && !occupiedManualTimes.Contains(ToTimeKey(endFrame / (double)generationFrames)))
                {
                    double evalTime = rightNeighbor.start;
                    if (TryCapturePoseAtTime(rightNeighbor, director, animator, evalTime, "fullbody", out KimodoMarkerSampleResult pose, out string captureError))
                    {
                        samples.Add(pose);
                    }
                    else
                    {
                        Debug.LogWarning($"{LogPrefix} Failed to sample right neighbor start pose: {captureError}");
                    }
                }
            }
            finally
            {
                director.time = originalTime;
                director.Evaluate();
                director.extrapolationMode = originalWrapMode;
            }

            return true;
        }

        private static void CollectManualTimes(List<KimodoMarkerSampleResult> samples, HashSet<long> output)
        {
            if (samples == null || output == null)
            {
                return;
            }

            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult s = samples[i];
                if (s == null)
                {
                    continue;
                }

                output.Add(ToTimeKey(s.sampleTime));
            }
        }

        private static SupportFootSide ResolveHigherFootSide(float leftHeight, float rightHeight, SupportFootSide activeFoot)
        {
            float epsilon = 1e-4f;
            if (leftHeight > rightHeight + epsilon)
            {
                return SupportFootSide.Left;
            }

            if (rightHeight > leftHeight + epsilon)
            {
                return SupportFootSide.Right;
            }

            return activeFoot != SupportFootSide.Unknown
                ? activeFoot
                : SupportFootSide.Left;
        }

        private static List<KimodoConstraintMarkerBase> GatherKimodoMarkers(TrackAsset track, TimelineClip clipRange)
        {
            var markers = new List<KimodoConstraintMarkerBase>();
            double minTime = clipRange != null ? clipRange.start : double.MinValue;
            double maxTime = clipRange != null ? clipRange.end : double.MaxValue;
            foreach (IMarker marker in track.GetMarkers())
            {
                if (marker is KimodoConstraintMarkerBase kimodoMarker)
                {
                    if (kimodoMarker.time < minTime || kimodoMarker.time > maxTime)
                    {
                        continue;
                    }

                    markers.Add(kimodoMarker);
                }
            }

            markers.Sort((a, b) => a.time.CompareTo(b.time));
            return markers;
        }

        private static void FindNeighborClips(TimelineClip sourceClip, out TimelineClip leftNeighbor, out TimelineClip rightNeighbor)
        {
            leftNeighbor = null;
            rightNeighbor = null;

            TrackAsset track = sourceClip.GetParentTrack();
            if (track == null)
            {
                return;
            }

            foreach (TimelineClip clip in track.GetClips())
            {
                if (clip == null || clip == sourceClip)
                {
                    continue;
                }

                if (clip.end <= sourceClip.start)
                {
                    if (leftNeighbor == null || clip.end > leftNeighbor.end)
                    {
                        leftNeighbor = clip;
                    }
                }

                if (clip.start >= sourceClip.end)
                {
                    if (rightNeighbor == null || clip.start < rightNeighbor.start)
                    {
                        rightNeighbor = clip;
                    }
                }
            }
        }

        private static void NormalizeConstraintOrigin(List<KimodoMarkerSampleResult> samples)
        {
            if (samples == null || samples.Count == 0)
            {
                return;
            }

            int anchorIndex = ResolveConstraintOriginAnchorIndex(samples);
            if (anchorIndex < 0 || anchorIndex >= samples.Count || samples[anchorIndex] == null)
            {
                return;
            }

            KimodoMarkerSampleResult anchor = samples[anchorIndex];
            Vector3 anchorRootPosition = anchor.rootPosition;
            Quaternion anchorYaw = GetRootYaw(anchor);

            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult sample = samples[i];
                if (sample == null)
                {
                    continue;
                }

                Vector3 localPosition = sample.rootPosition - anchorRootPosition;
                sample.rootPosition = new Vector3(localPosition.x, sample.rootPosition.y, localPosition.z);

                if (sample.localAxisAngles != null && sample.localAxisAngles.Count > 0)
                {
                    Quaternion rootRot = AxisAngleToQuaternion(sample.localAxisAngles[0]);
                    Quaternion normalizedRootRot = Quaternion.Inverse(anchorYaw) * rootRot;
                    sample.localAxisAngles[0] = KimodoRuntimeUtility.QuaternionToAxisAngleVector(normalizedRootRot);
                }
            }
        }

        private static Quaternion GetRootYaw(KimodoMarkerSampleResult sample)
        {
            if (sample == null || sample.localAxisAngles == null || sample.localAxisAngles.Count == 0)
            {
                return Quaternion.identity;
            }

            Quaternion rootRot = AxisAngleToQuaternion(sample.localAxisAngles[0]);
            Vector3 flatForward = Vector3.ProjectOnPlane(rootRot * Vector3.forward, Vector3.up);
            if (flatForward.sqrMagnitude <= 1e-8f)
            {
                return Quaternion.identity;
            }

            return Quaternion.LookRotation(flatForward.normalized, Vector3.up);
        }

        private static Quaternion AxisAngleToQuaternion(Vector3 axisAngle)
        {
            float angleRad = axisAngle.magnitude;
            if (angleRad <= 1e-8f)
            {
                return Quaternion.identity;
            }

            Vector3 axis = axisAngle / angleRad;
            return Quaternion.AngleAxis(angleRad * Mathf.Rad2Deg, axis);
        }

        private static int ResolveConstraintOriginAnchorIndex(List<KimodoMarkerSampleResult> samples)
        {
            if (samples == null || samples.Count == 0)
            {
                return -1;
            }

            int earliest = -1;
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i] != null)
                {
                    earliest = i;
                    break;
                }
            }

            return earliest;
        }

        internal static bool TryBuildNeighborBoundarySamplesForPreview(
            TimelineClip sourceClip,
            int generationFrames,
            out KimodoMarkerSampleResult leftNeighborEndPose,
            out KimodoMarkerSampleResult rightNeighborStartPose,
            out string warning)
        {
            leftNeighborEndPose = null;
            rightNeighborStartPose = null;
            warning = string.Empty;

            if (sourceClip == null)
            {
                warning = "source clip is null, skip inbetween preview boundaries.";
                return false;
            }

            TrackAsset track = sourceClip.GetParentTrack();
            if (track == null)
            {
                warning = "cannot resolve parent track, skip inbetween preview boundaries.";
                return false;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                warning = "Timeline inspected director is null, skip inbetween preview boundaries.";
                return false;
            }

            Animator animator = director.GetGenericBinding(track) as Animator;
            if (animator == null)
            {
                warning = "track has no Animator binding, skip inbetween preview boundaries.";
                return false;
            }

            FindNeighborClips(sourceClip, out TimelineClip leftNeighbor, out TimelineClip rightNeighbor);
            if (leftNeighbor == null && rightNeighbor == null)
            {
                warning = "no neighboring clips found, skip inbetween preview boundaries.";
                return true;
            }

            double originalTime = director.time;
            DirectorWrapMode originalWrapMode = director.extrapolationMode;
            try
            {
                director.extrapolationMode = DirectorWrapMode.Hold;

                if (leftNeighbor != null)
                {
                    double evalTime = Math.Max(leftNeighbor.start, leftNeighbor.end - NeighborSampleDeltaSeconds);
                    if (!TryCapturePoseAtTime(leftNeighbor, director, animator, evalTime, "fullbody", out leftNeighborEndPose, out string leftError))
                    {
                        Debug.LogWarning($"{LogPrefix} Failed to sample left neighbor end pose for preview: {leftError}");
                    }
                }

                if (rightNeighbor != null)
                {
                    _ = Math.Max(0, generationFrames - 1);
                    double evalTime = rightNeighbor.start;
                    if (!TryCapturePoseAtTime(rightNeighbor, director, animator, evalTime, "fullbody", out rightNeighborStartPose, out string rightError))
                    {
                        Debug.LogWarning($"{LogPrefix} Failed to sample right neighbor start pose for preview: {rightError}");
                    }
                    else if (leftNeighborEndPose != null &&
                             TryBuildPoseRigRootMotionCompensation(
                                 new List<KimodoMarkerSampleResult> { leftNeighborEndPose, rightNeighborStartPose },
                                 string.IsNullOrWhiteSpace(((KimodoPlayableClip)sourceClip.asset)?.bridgeModelName)
                                     ? "Kimodo-SOMA-RP-v1"
                                     : ((KimodoPlayableClip)sourceClip.asset).bridgeModelName.Trim(),
                                 out Vector3[] previewCompensated,
                                 out _,
                                 out _)
                             && previewCompensated != null &&
                             previewCompensated.Length > 1)
                    {
                        rightNeighborStartPose = rightNeighborStartPose.Clone();
                        rightNeighborStartPose.rootPosition = previewCompensated[1];
                    }
                }
            }
            finally
            {
                director.time = originalTime;
                director.Evaluate();
                director.extrapolationMode = originalWrapMode;
            }

            return true;
        }

        private static bool TryCapturePoseAtTime(
            TimelineClip sourceClip,
            PlayableDirector director,
            Animator animator,
            double evalTime,
            string markerType,
            out KimodoMarkerSampleResult pose,
            out string error)
        {
            pose = null;
            error = string.Empty;

            try
            {
                director.time = evalTime;
                director.Evaluate();
                if (!TrySamplePoseFromClipAsset(
                        sourceClip,
                        animator,
                        evalTime,
                        markerType,
                        out pose,
                        out error))
                {
                    return false;
                }

                return pose != null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static long ToTimeKey(double time)
        {
            return (long)Math.Round(time * 1000000.0);
        }
    }
}
