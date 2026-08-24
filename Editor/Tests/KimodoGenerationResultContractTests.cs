using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KimodoBridge;
using KimodoBridge.Editor;
using NUnit.Framework;
using UnityEngine;

namespace KimodoUnityBridge.Generation.Tests
{
    /// <summary>
    /// Characterization tests for the result boundary shared by the bridge,
    /// runtime generation command, and Editor pipeline. These tests deliberately
    /// avoid starting QuickServer or creating Unity assets.
    /// </summary>
    public sealed class KimodoGenerationResultContractTests
    {
        private static readonly string[] ResultFieldNames =
        {
            "MotionJsonCompact",
            "MotionData",
            "MotionFormat",
            "RawStatus",
            "Message",
            "MotionBytes",
            "KmbAttachments",
            "MotionRepFingerprint",
            "ResolvedSeed",
            "ArdyPlaybackReserveSeconds",
            "StartFrame",
            "EndFrameExclusive",
            "AnalysisJson"
        };

        private static readonly string[] DtoFieldNames =
        {
            "motionJsonCompact",
            "motionData",
            "motionBytes",
            "kmbAttachments",
            "motionFormat",
            "rawStatus",
            "message",
            "motionRepFingerprint",
            "resolvedSeed",
            "ardyPlaybackReserveSeconds",
            "startFrame",
            "endFrameExclusive",
            "analysisJson"
        };

        [Test]
        public void BridgeDtoAndPipelineResultRetainEveryMappedBoundaryField()
        {
            AssertMembers(typeof(KimodoBridgeGenerationResult), ResultFieldNames);
            AssertMembers(typeof(KimodoGenerationResultDto), DtoFieldNames);
            AssertMembers(typeof(KimodoBridgeCommandResult), ResultFieldNames);
        }

        [Test]
        public void EditorResultRetainsAssetWritebackAndAnalysisFields()
        {
            AssertMembers(typeof(KimodoEditorGenerationResult), new[]
            {
                "ConstraintsPath",
                "Prompt",
                "Seed",
                "MotionJsonCompact",
                "AnalysisJson",
                "MotionBytes",
                "StartFrame",
                "EndFrameExclusive",
                "GeneratedClip",
                "RawBoneClip"
            });
        }

        [Test]
        public void TrimRuntimeResultForOutput_PreservesNonMotionMetadataAndAttachments()
        {
            KimodoRawMotionData source = CreateMotion(4, 1, 30f);
            var attachment = new KimodoBridgeKmbAttachment
            {
                Index = 2,
                Offset = 17,
                MotionBytes = new byte[] { 7, 8, 9 },
                MotionData = source,
                StartFrame = 100,
                EndFrameExclusive = 104
            };
            var attachments = new List<KimodoBridgeKmbAttachment> { attachment };
            byte[] originalMotionBytes = { 1, 2, 3 };
            var result = new KimodoBridgeCommandResult
            {
                MotionJsonCompact = KimodoRawMotionUtility.ToCompactJson(source),
                MotionData = source,
                MotionFormat = "kmb_v1",
                RawStatus = "done",
                Message = "preserve me",
                MotionBytes = originalMotionBytes,
                KmbAttachments = attachments,
                MotionRepFingerprint = "fp-123",
                ResolvedSeed = 42,
                StartFrame = 10,
                EndFrameExclusive = 14,
                AnalysisJson = "{\"keyframes\":[{\"frame\":0},{\"frame\":2},{\"frame\":3}]}"
            };
            var request = new KimodoEditorGenerateRequest
            {
                TargetFrameCount = 2,
                TargetFrameRate = 30f,
                RuntimeFrameCount = 4,
                RuntimeTrimStartFrame = 1
            };
            SetMember(result, "ArdyPlaybackReserveSeconds", 0.75d);

            KimodoBridgeCommandResult trimmed = KimodoEditorGeneratePipeline.TrimRuntimeResultForOutput(
                request,
                result,
                KimodoMotionModelProfiles.DefaultModelName);

            Assert.That(trimmed, Is.SameAs(result));
            Assert.That(trimmed.MotionFormat, Is.EqualTo("kmb_v1"));
            Assert.That(trimmed.RawStatus, Is.EqualTo("done"));
            Assert.That(trimmed.Message, Is.EqualTo("preserve me"));
            Assert.That(trimmed.MotionRepFingerprint, Is.EqualTo("fp-123"));
            Assert.That(trimmed.ResolvedSeed, Is.EqualTo(42));
            Assert.That(GetMemberValue(trimmed, "ArdyPlaybackReserveSeconds"), Is.EqualTo(0.75d));
            Assert.That(trimmed.KmbAttachments, Is.SameAs(attachments));
            Assert.That(trimmed.KmbAttachments[0], Is.SameAs(attachment));
            Assert.That(trimmed.MotionData.FrameCount, Is.EqualTo(2));
            Assert.That(trimmed.StartFrame, Is.EqualTo(11));
            Assert.That(trimmed.EndFrameExclusive, Is.EqualTo(13));
            Assert.That(trimmed.MotionBytes, Is.Not.Null);
            Assert.That(trimmed.MotionBytes, Is.Not.SameAs(originalMotionBytes));
            Assert.That(trimmed.AnalysisJson, Does.Contain("\"frame\":1"));
            Assert.That(trimmed.AnalysisJson, Does.Not.Contain("\"frame\":3"));
        }

        [Test]
        public void TrimRuntimeResultForOutput_WithNoTrimReturnsTheOriginalResult()
        {
            var result = new KimodoBridgeCommandResult
            {
                MotionFormat = "analysis_only",
                RawStatus = "done",
                Message = "analysis",
                MotionJsonCompact = string.Empty,
                MotionBytes = Array.Empty<byte>(),
                KmbAttachments = Array.Empty<KimodoBridgeKmbAttachment>(),
                MotionRepFingerprint = "fp",
                ResolvedSeed = 9,
                AnalysisJson = "{\"analysis\":true}"
            };
            var request = new KimodoEditorGenerateRequest
            {
                TargetFrameCount = 2,
                TargetFrameRate = 30f,
                RuntimeFrameCount = 2,
                RuntimeTrimStartFrame = 0
            };

            KimodoBridgeCommandResult output = KimodoEditorGeneratePipeline.TrimRuntimeResultForOutput(
                request,
                result,
                KimodoMotionModelProfiles.DefaultModelName);

            Assert.That(output, Is.SameAs(result));
            Assert.That(output.MotionJsonCompact, Is.Empty);
            Assert.That(output.MotionBytes, Is.Empty);
            Assert.That(output.KmbAttachments, Is.Empty);
            Assert.That(output.AnalysisJson, Is.EqualTo("{\"analysis\":true}"));
        }

        [Test]
        public void BridgeCommand_RejectsNullRequestBeforeInvokingBackend()
        {
            var command = new KimodoBridgeCommand();
            Assert.Throws<ArgumentNullException>(() =>
                command.ExecuteAsync(null, null, default).GetAwaiter().GetResult());
        }

        [Test]
        public void BridgeCommand_RejectsMissingGenerationRequestBeforeInvokingBackend()
        {
            var command = new KimodoBridgeCommand();
            Assert.Throws<InvalidOperationException>(() =>
                command.ExecuteAsync(new KimodoBridgeCommandRequest(), null, default).GetAwaiter().GetResult());
        }

        private static void AssertMembers(Type type, IEnumerable<string> names)
        {
            foreach (string name in names)
            {
                MemberInfo member = type.GetMember(name, BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault();
                Assert.That(member, Is.Not.Null, $"{type.FullName} lost result field {name}");
            }
        }

        private static void SetMember(object instance, string name, object value)
        {
            MemberInfo member = instance.GetType().GetMember(name, BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault();
            Assert.That(member, Is.Not.Null, $"{instance.GetType().FullName} lost result field {name}");
            if (member is FieldInfo field)
            {
                field.SetValue(instance, value);
                return;
            }
            if (member is PropertyInfo property && property.CanWrite)
            {
                property.SetValue(instance, value, null);
                return;
            }
            Assert.Fail($"{instance.GetType().FullName} result field {name} is not writable.");
        }

        private static object GetMemberValue(object instance, string name)
        {
            MemberInfo member = instance.GetType().GetMember(name, BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault();
            Assert.That(member, Is.Not.Null, $"{instance.GetType().FullName} lost result field {name}");
            if (member is FieldInfo field)
            {
                return field.GetValue(instance);
            }
            return ((PropertyInfo)member).GetValue(instance, null);
        }

        private static KimodoRawMotionData CreateMotion(int frameCount, int jointCount, float frameRate)
        {
            var roots = new Vector3[frameCount];
            var rotations = new List<float>(frameCount * jointCount * 4);
            for (int frame = 0; frame < frameCount; frame++)
            {
                roots[frame] = new Vector3(frame, 0f, frame * 2f);
                for (int joint = 0; joint < jointCount; joint++)
                {
                    rotations.Add(1f);
                    rotations.Add(0f);
                    rotations.Add(0f);
                    rotations.Add(0f);
                }
            }

            return new KimodoRawMotionData(
                frameCount,
                jointCount,
                frameRate,
                new[] { "Hips" },
                new[] { -1 },
                roots,
                rotations,
                0);
        }
    }
}
