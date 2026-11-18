using System;
using System.Collections.Generic;
using KimodoBridge;
using Newtonsoft.Json.Linq;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoGenerationConstraintHipsComparer
    {
        internal static JObject Compare(
            AnimationClip generatedClip,
            TimelineClip timelineClip,
            string modelName,
            IReadOnlyList<KimodoMarkerSampleResult> constraints,
            int trimStartFrame,
            float frameRate,
            KimodoTimelineInOutConstraintContext timelineContext)
        {
            var frames = new JArray();
            var errors = new JArray();
            if (generatedClip == null || constraints == null || constraints.Count == 0)
            {
                return CreateResult("not_applicable", frames, errors);
            }

            if (timelineClip == null || timelineClip.asset is not KimodoPlayableClip playableClip ||
                playableClip.clip != generatedClip)
            {
                return CreateUnavailable("The generated Clip is not assigned to its Timeline clip.");
            }

            if (timelineContext?.SourceClip != timelineClip)
            {
                return CreateUnavailable("The Timeline sampling context does not match the generated Clip.");
            }

            Animator sourceAnimator = timelineContext.Animator;
            if (sourceAnimator == null || timelineContext.Director == null)
            {
                return CreateUnavailable("The source Character Animator or Timeline director is unavailable.");
            }

            float samplingRate = frameRate > 0f ? frameRate : generatedClip.frameRate;
            if (samplingRate <= 0f || float.IsNaN(samplingRate) || float.IsInfinity(samplingRate))
            {
                return CreateUnavailable("The generated Clip frame rate is invalid.");
            }
            if (double.IsNaN(timelineClip.timeScale) || double.IsInfinity(timelineClip.timeScale) ||
                Math.Abs(timelineClip.timeScale) < 1e-9)
            {
                return CreateUnavailable("The generated Timeline Clip has an invalid time scale.");
            }

            var validSamples = new List<KimodoMarkerSampleResult>();
            var constraintIndices = new List<int>();
            var runtimeFrames = new List<int>();
            var outputFrames = new List<int>();
            var timelineTimes = new List<double>();
            for (int i = 0; i < constraints.Count; i++)
            {
                KimodoMarkerSampleResult sample = constraints[i];
                if (sample == null || !sample.enabled || !sample.sampleValid)
                {
                    continue;
                }

                int runtimeFrame = KimodoFrameTimeUtility.SecondsToFrameIndex(sample.sampleTime, samplingRate);
                int outputFrame = runtimeFrame - Mathf.Max(0, trimStartFrame);
                float clipTime = outputFrame / samplingRate;
                if (outputFrame < 0 || clipTime < 0f || clipTime > generatedClip.length)
                {
                    continue;
                }

                validSamples.Add(sample);
                constraintIndices.Add(i);
                runtimeFrames.Add(runtimeFrame);
                outputFrames.Add(outputFrame);
                // TimelineClip.ToLocalTime maps global timeline time to clip time:
                // clipIn + (globalTime - start) * timeScale. Invert that mapping
                // so each generated output frame is sampled at its Timeline time.
                timelineTimes.Add(timelineClip.start + (clipTime - timelineClip.clipIn) / timelineClip.timeScale);
            }

            if (validSamples.Count == 0)
            {
                return CreateResult("not_applicable", frames, errors);
            }

            if (!KimodoTimelineSamplingSession.TryCreate(
                    timelineContext,
                    modelName,
                    out KimodoTimelineSamplingSession timelineSampler,
                    out string timelineError))
            {
                return CreateUnavailable($"Could not sample the generated Timeline Clip: {timelineError}");
            }

            try
            {
                if (!timelineSampler.TryCaptureHipsWorldPositions(
                        timelineTimes,
                        out Vector3[] generatedHipsPositions,
                        out timelineError))
                {
                    return CreateUnavailable($"Could not sample Timeline Hips positions: {timelineError}");
                }

                if (!KimodoConstraintPoseRigFactory.TryCreatePoseRig(
                        modelName,
                        sourceAnimator,
                        timelineSampler.SourceAvatar,
                        out KimodoConstraintPoseRigFactory.PoseRigInstance rig,
                        out string rigError))
                {
                    return CreateUnavailable($"Could not create the source Character Humanoid rig: {rigError}");
                }

                try
                {
                    for (int i = 0; i < validSamples.Count; i++)
                    {
                        try
                        {
                            if (!KimodoConstraintPoseRigFactory.TryApplyPose(
                                    rig,
                                    validSamples[i],
                                    modelName,
                                    out string applyError))
                            {
                                throw new InvalidOperationException(applyError);
                            }
                            if (!rig.TargetCache.GetBonePose(
                                    HumanBodyBones.Hips,
                                    out Vector3 constraintHips,
                                    out _))
                            {
                                throw new InvalidOperationException("The source Character Humanoid rig has no Hips bone.");
                            }

                            Vector3 generatedHips = generatedHipsPositions[i];
                            Vector3 delta = generatedHips - constraintHips;
                            frames.Add(new JObject
                            {
                                ["constraint_index"] = constraintIndices[i],
                                ["constraint_frame"] = runtimeFrames[i],
                                ["frame"] = outputFrames[i],
                                ["timeline_time_seconds"] = timelineTimes[i],
                                ["constraint_hips_world"] = Vector3ToJson(constraintHips),
                                ["generated_hips_world"] = Vector3ToJson(generatedHips),
                                ["delta_xyz_m"] = Vector3ToJson(delta),
                                ["distance_m"] = delta.magnitude
                            });
                        }
                        catch (Exception error)
                        {
                            errors.Add(new JObject
                            {
                                ["frame"] = outputFrames[i],
                                ["constraint_index"] = constraintIndices[i],
                                ["message"] = error.Message
                            });
                        }
                    }
                }
                finally
                {
                    KimodoConstraintPoseRigFactory.DisposePoseRig(rig);
                }
            }
            finally
            {
                timelineSampler.Dispose();
            }

            return CreateResult(errors.Count > 0 ? frames.Count > 0 ? "partial" : "unavailable" :
                frames.Count > 0 ? "ok" : "not_applicable", frames, errors);
        }

        private static JObject CreateUnavailable(string message)
        {
            var errors = new JArray(new JObject { ["message"] = message });
            return CreateResult("unavailable", new JArray(), errors);
        }

        private static JObject CreateResult(string status, JArray frames, JArray errors)
        {
            var result = new JObject
            {
                ["status"] = status,
                ["metric"] = "Timeline-evaluated generated Clip Hips world position minus the source Character Humanoid rig after applying the constraint; XYZ and Euclidean distance in meters",
                ["comparison_basis"] = "timeline_evaluated_source_character_humanoid_rig",
                ["compared_count"] = frames.Count,
                ["frames"] = frames
            };
            if (errors.Count > 0) result["errors"] = errors;
            return result;
        }

        private static JArray Vector3ToJson(Vector3 value) => new JArray(value.x, value.y, value.z);
    }
}
