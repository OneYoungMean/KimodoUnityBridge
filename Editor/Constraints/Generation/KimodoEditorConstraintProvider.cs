using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoEditorConstraintProvider
    {
        public KimodoInOutConstraintResult BuildGenerationConstraintsOrThrow(
            KimodoPlayableClip clip,
            KimodoExternalConstraintRequest externalConstraint,
            int runtimeFrameCount,
            float runtimeLengthSeconds,
            float frameRate,
            bool disableTimelineInOut,
            bool deferNormalization,
            bool enableAutoBeginAnchor,
            double sampleTimeOffsetSeconds,
            TimelineClip timelineClip)
        {
            bool includeTimeline = externalConstraint?.Enabled != true ||
                externalConstraint.IncludeTimelineConstraints;
            KimodoInOutConstraintResult result;
            if (includeTimeline)
            {
                result = BuildConstraintDataOrThrow(
                    clip,
                    runtimeFrameCount,
                    disableTimelineInOut,
                    deferNormalization,
                    enableAutoBeginAnchor,
                    sampleTimeOffsetSeconds,
                    timelineClip);
                if (result.BeginBoundarySample != null)
                {
                    result.CombinedSamples.Remove(result.BeginBoundarySample);
                }
            }
            else
            {
                result = new KimodoInOutConstraintResult
                {
                    ConstraintsJson = externalConstraint.ConstraintsJson ?? string.Empty
                };
            }

            if (externalConstraint?.Enabled == true)
            {
                int externalSampleStart = result.CombinedSamples.Count;
                KimodoInOutConstraintComposer.AppendSamples(
                    externalConstraint.ConstraintSamples,
                    result.CombinedSamples);
                for (int i = externalSampleStart; i < result.CombinedSamples.Count; i++)
                {
                    result.CombinedSamples[i].sampleTime += sampleTimeOffsetSeconds;
                }

                if (result.HasSyntheticAutoBeginConstraint &&
                    result.CombinedSamples.Count > 0 &&
                    KimodoConstraintNormalizationUtility.HasNormalizationAnchor(
                        result.CombinedSamples,
                        1.0,
                        result.CombinedSamples[0]))
                {
                    result.CombinedSamples.RemoveAt(0);
                    result.HasSyntheticAutoBeginConstraint = false;
                }
            }

            if (includeTimeline || result.CombinedSamples.Count > 0)
            {
                result.ConstraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                    result.CombinedSamples,
                    ResolveExportContext(timelineClip),
                    0.0,
                    runtimeLengthSeconds,
                    frameRate,
                    result.DenseRootPath);
            }
            return result;
        }

        internal void ApplyLoopGeneration(
            KimodoInOutConstraintResult result,
            int sourceFrameCount,
            float frameRate,
            double runtimeLengthSeconds,
            TimelineClip timelineClip)
        {
            if (result == null || result.CombinedSamples == null || result.CombinedSamples.Count == 0 ||
                sourceFrameCount <= 1 || frameRate <= 0f)
            {
                return;
            }

            int paddingFrames = sourceFrameCount / 2;
            int trailingPaddingFrames = sourceFrameCount - paddingFrames;
            double paddingSeconds = paddingFrames / (double)frameRate;
            double sourceDurationSeconds = sourceFrameCount / (double)frameRate;
            KimodoMarkerSampleResult first = null;
            KimodoMarkerSampleResult last = null;
            for (int i = 0; i < result.CombinedSamples.Count; i++)
            {
                KimodoMarkerSampleResult sample = result.CombinedSamples[i];
                KimodoConstraintMask mask = KimodoConstraintMask.Resolve(sample?.mask, sample?.constraintType);
                if (sample?.characterPose?.root == null || (!mask.rootPosition && !mask.rootHeading))
                {
                    continue;
                }

                if (first == null || sample.sampleTime < first.sampleTime)
                {
                    first = sample;
                }
                if (last == null || sample.sampleTime > last.sampleTime)
                {
                    last = sample;
                }
            }

            if (first != null && last != null)
            {
                Vector3 start = ResolveRootPosition(first);
                Vector3 end = ResolveRootPosition(last);
                Vector3 delta = (end - start) / 3f;
                Vector3 heading = new Vector3(start.x - end.x, 0f, start.z - end.z);
                if (heading.sqrMagnitude <= 1e-8f)
                {
                    heading = Vector3.forward;
                }

                for (int i = 0; i < paddingFrames; i++)
                {
                    float t = (i + 1f) / (paddingFrames + 1f);
                    Vector3 position = LoopBezier(end, end - delta, start + delta, start, t);
                    KimodoMarkerSampleResult bridge = CreateLoopRootSample(last, position, heading);
                    bridge.sampleTime = i / (double)frameRate;
                    result.CombinedSamples.Add(bridge);
                }
                for (int i = 0; i < trailingPaddingFrames; i++)
                {
                    float t = (i + 1f) / (trailingPaddingFrames + 1f);
                    Vector3 position = LoopBezier(end, end - delta, start + delta, start, t);
                    KimodoMarkerSampleResult bridge = CreateLoopRootSample(last, position, heading);
                    bridge.sampleTime = paddingSeconds + sourceDurationSeconds + i / (double)frameRate;
                    result.CombinedSamples.Add(bridge);
                }
            }

            result.ConstraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                result.CombinedSamples,
                ResolveExportContext(timelineClip),
                0.0,
                runtimeLengthSeconds,
                frameRate,
                result.DenseRootPath);
        }

        private static Vector3 ResolveRootPosition(KimodoMarkerSampleResult sample)
        {
            if (sample.hasRoot2DOverride && sample.root2DOverride != null)
            {
                return sample.root2DOverride.t;
            }

            return sample.characterPose.root.t;
        }

        private static KimodoMarkerSampleResult CreateLoopRootSample(
            KimodoMarkerSampleResult source,
            Vector3 position,
            Vector3 heading)
        {
            KimodoMarkerSampleResult result = source.Clone();
            result.constraintType = "root2d";
            result.mask = KimodoConstraintMask.ForType("root2d");
            result.characterPose.root.t = position;
            result.characterPose.root.q = Quaternion.LookRotation(heading, Vector3.up);
            result.root2DOverride = new CharacterAnimationCli.Unity.CharacterPoseTransform
            {
                t = position,
                q = result.characterPose.root.q
            };
            result.hasRoot2DOverride = true;
            result.hasRootHeading = true;
            return result;
        }

        private static Vector3 LoopBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            t = Mathf.Clamp01(t);
            float u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
        }

        public KimodoInOutConstraintResult BuildConstraintDataOrThrow(
            KimodoPlayableClip clip,
            int? generationFramesOverride = null,
            bool disableTimelineInOut = false,
            bool deferNormalization = false,
            bool enableAutoBeginAnchor = true,
            double sampleTimeOffsetSeconds = 0.0,
            TimelineClip timelineClipOverride = null)
        {
            TimelineClip sourceClip = timelineClipOverride ?? KimodoTimelineClipResolver.FindTimelineClipForAsset(clip);
            if (sourceClip == null)
            {
                return new KimodoInOutConstraintResult();
            }

            int generationFrames = generationFramesOverride ?? clip.generationFrames;
            var splinePathSamples = new List<KimodoMarkerSampleResult>();
            bool denseSplinePath = false;
            if (!KimodoSplinePathEditorBridge.TryBuildConstraintSamples(
                    clip,
                    sourceClip,
                    generationFrames,
                    KimodoMotionModelProfiles.ResolveGenerationFrameRate(clip.bridgeModelName),
                    out splinePathSamples,
                    out denseSplinePath,
                    out string splinePathError))
            {
                throw new InvalidOperationException($"Build spline path constraints failed: {splinePathError}");
            }

            bool ok = KimodoInOutConstraintAdapter.TryBuildConstraints(
                sourceClip,
                disableTimelineInOut ? KimodoInOutConstraintMode.None : clip.inOutConstraintMode,
                enableAutoBeginAnchor && clip.autoBeginAnchor,
                deferNormalization,
                // Mode=None prevents boundary sampling; true keeps manual-marker normalization independent of the In toggle.
                disableTimelineInOut || clip.enableInConstraint,
                !disableTimelineInOut && clip.enableOutConstraint,
                generationFrames,
                sampleTimeOffsetSeconds,
                out KimodoInOutConstraintResult result,
                out string error,
                splinePathSamples);

            if (!ok)
            {
                throw new InvalidOperationException($"Build constraints failed: {error}");
            }

            result ??= new KimodoInOutConstraintResult();
            if (splinePathSamples.Count > 0)
            {
                float frameRate = KimodoMotionModelProfiles.ResolveGenerationFrameRate(clip.bridgeModelName);
                result.DenseRootPath = denseSplinePath;
                result.ConstraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                    result.CombinedSamples,
                    ResolveExportContext(sourceClip),
                    clipStartSeconds: 0.0,
                    clipDurationSeconds: KimodoInOutConstraintTools.ResolveConstraintClipDurationSeconds(generationFrames, frameRate),
                    exportFps: frameRate,
                    denseRootPath: denseSplinePath);
            }
            return result;
        }

        public TimelineClip FindTimelineClipForAsset(PlayableAsset asset)
        {
            return KimodoTimelineClipResolver.FindTimelineClipForAsset(asset);
        }

        public GameObject FindTimelineBindingObjectForAsset(
            PlayableAsset asset,
            TimelineClip timelineClipOverride = null)
        {
            TimelineClip sourceClip = timelineClipOverride ?? FindTimelineClipForAsset(asset);
            if (sourceClip == null)
            {
                return null;
            }

            TrackAsset track = sourceClip.GetParentTrack();
            if (track == null)
            {
                return null;
            }

            if (!KimodoInOutConstraintAdapter.TryResolveDirector(
                    sourceClip,
                    track,
                    out PlayableDirector director,
                    out _))
            {
                return null;
            }

            TrackAsset currentTrack = track;
            while (currentTrack != null)
            {
                UnityEngine.Object binding = director.GetGenericBinding(currentTrack);
                if (binding is Animator animator && animator != null)
                {
                    return animator.gameObject;
                }

                if (binding is GameObject go && go != null)
                {
                    return go;
                }

                currentTrack = currentTrack.parent as TrackAsset;
            }

            return null;
        }
            private static KimodoConstraintExportContext ResolveExportContext(TimelineClip timelineClip)
        {
            if (timelineClip != null &&
                KimodoInOutConstraintAdapter.TryResolveTimelineContext(timelineClip, out KimodoTimelineInOutConstraintContext context, out _) &&
                context?.SourceAvatar != null)
            {
                return new KimodoConstraintExportContext(
                    KimodoConstraintNormalizationUtility.ResolveHumanScale(context.SourceAvatar),
                    KimodoConstraintExportProjector.Create(context.ModelName));
            }
            return new KimodoConstraintExportContext();
        }
}

}
//touch 7ec98321-518c-4133-8a2b-0e9dcc4436b4
