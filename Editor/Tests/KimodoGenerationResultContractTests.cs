using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using KimodoBridge;
using KimodoBridge.Editor;
using KimodoUnityBridge.Command;
using Newtonsoft.Json.Linq;
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
        private static readonly string[] DtoFieldNames =
        {
            "motionJsonCompact",
            "motionData",
            "motionBytes",
            "mp4Path",
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
        public void BridgeAndPipelineShareOneResultDto()
        {
            AssertMembers(typeof(KimodoGenerationResultDto), DtoFieldNames);
        }

        [Test]
        public void BridgeFactory_UsesReplaceableTransportAndReturnsItsResponse()
        {
            var request = new KimodoGenerationRequestDto { prompt = "walk" };
            var response = new KimodoGenerationResultDto
            {
                motionJsonCompact = "{}",
                motionFormat = "json_compact"
            };
            var transport = new FakeBridgeTransport { Result = response };
            var factory = new KimodoGenerationTransportFactory(() => transport);
            var command = new KimodoBridgeCommand(factory.Create());
            var stages = new List<KimodoBridgeCommandStage>();

            KimodoGenerationResultDto result = command.ExecuteAsync(
                request,
                (stage, _) => stages.Add(stage),
                CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(transport.Request, Is.SameAs(request));
            Assert.That(result, Is.SameAs(response));
            CollectionAssert.AreEqual(
                new[]
                {
                    KimodoBridgeCommandStage.Validate,
                    KimodoBridgeCommandStage.InvokeBackend,
                    KimodoBridgeCommandStage.InvokeBackend,
                    KimodoBridgeCommandStage.Completed
                },
                stages);
        }

        [Test]
        public void ModelListCommand_UsesInjectedBridgeTransportAndNormalizesItsResponse()
        {
            var transport = new FakeModelTransport
            {
                ModelListResponse = new JObject
                {
                    ["status"] = "done",
                    ["default"] = new JObject { ["model"] = "test-model" },
                    ["configs"] = new JArray(new JObject
                    {
                        ["model"] = "test-model",
                        ["text_encoder_model"] = "default",
                        ["available"] = true,
                        ["private_field"] = "removed"
                    })
                }
            };

            JObject result = JObject.Parse(command_context.ListModels(
                new JObject { ["compact"] = true },
                transport));

            Assert.That(transport.RequestedModel, Is.Not.Null.And.Not.Empty);
            Assert.That(transport.RequestedTextEncoderMode, Is.Not.Null.And.Not.Empty);
            Assert.That(result["status"], Is.Null);
            Assert.That(result.Value<int>("count"), Is.EqualTo(1));
            Assert.That(result["configs"]?[0]?["model"]?.Value<string>(), Is.EqualTo("test-model"));
            Assert.That(result["configs"]?[0]?["private_field"], Is.Null);
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
                "PostAnalysisJson",
                "PostAnalysisError",
                "PostAnalysisSkipped",
                "ConstraintLoss",
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
            var result = new KimodoGenerationResultDto
            {
                motionJsonCompact = KimodoRawMotionUtility.ToCompactJson(source),
                motionData = source,
                motionFormat = "kmb_v1",
                rawStatus = "done",
                message = "preserve me",
                motionBytes = originalMotionBytes,
                kmbAttachments = attachments,
                motionRepFingerprint = "fp-123",
                resolvedSeed = 42,
                ardyPlaybackReserveSeconds = 0.75d,
                startFrame = 10,
                endFrameExclusive = 14,
                analysisJson = "{\"keyframes\":[{\"frame\":0},{\"frame\":2},{\"frame\":3}]}"
            };
            var request = new KimodoEditorGenerateRequest
            {
                TargetFrameCount = 2,
                TargetFrameRate = 30f,
                RuntimeFrameCount = 4,
                RuntimeTrimStartFrame = 1
            };
            KimodoGenerationResultDto trimmed = KimodoEditorGeneratePipeline.TrimRuntimeResultForOutput(
                request,
                result,
                KimodoMotionModelProfiles.DefaultModelName);

            Assert.That(trimmed, Is.SameAs(result));
            Assert.That(trimmed.motionFormat, Is.EqualTo("kmb_v1"));
            Assert.That(trimmed.rawStatus, Is.EqualTo("done"));
            Assert.That(trimmed.message, Is.EqualTo("preserve me"));
            Assert.That(trimmed.motionRepFingerprint, Is.EqualTo("fp-123"));
            Assert.That(trimmed.resolvedSeed, Is.EqualTo(42));
            Assert.That(trimmed.ardyPlaybackReserveSeconds, Is.EqualTo(0.75d));
            Assert.That(trimmed.kmbAttachments, Is.SameAs(attachments));
            Assert.That(trimmed.kmbAttachments[0], Is.SameAs(attachment));
            Assert.That(trimmed.motionData.FrameCount, Is.EqualTo(2));
            Assert.That(trimmed.startFrame, Is.EqualTo(11));
            Assert.That(trimmed.endFrameExclusive, Is.EqualTo(13));
            Assert.That(trimmed.motionBytes, Is.Not.Null);
            Assert.That(trimmed.motionBytes, Is.Not.SameAs(originalMotionBytes));
            Assert.That(trimmed.analysisJson, Does.Contain("\"frame\":1"));
            Assert.That(trimmed.analysisJson, Does.Not.Contain("\"frame\":3"));
        }

        [TestCase(0, 4)]
        [TestCase(3, 9)]
        public void TrimRuntimeResultForOutput_DiscardsContextAndKeepsExactOutputFrames(int prefix, int runtimeFrames)
        {
            var result = new KimodoGenerationResultDto { motionData = CreateMotion(runtimeFrames, 1, 30f) };
            var request = new KimodoEditorGenerateRequest
            { TargetFrameCount = 2, TargetFrameRate = 30, RuntimeFrameCount = runtimeFrames, RuntimeTrimStartFrame = prefix };
            var output = KimodoEditorGeneratePipeline.TrimRuntimeResultForOutput(request, result,
                KimodoMotionModelProfiles.DefaultModelName);
            Assert.That(output.motionData.FrameCount, Is.EqualTo(2));
            Assert.That(output.motionData.rootPositions[0].x, Is.EqualTo(prefix));
            Assert.That(output.motionData.rootPositions[1].x, Is.EqualTo(prefix + 1));
        }

        [Test]
        public void TrimRuntimeResultForOutput_CapturesRealEndpointAndKeepsRequestedFrames()
        {
            var result = new KimodoGenerationResultDto
            {
                motionData = CreateMotion(3, 1, 30f),
                motionFormat = "kmb_v1"
            };
            var request = new KimodoEditorGenerateRequest
            {
                TargetFrameCount = 2,
                TargetFrameRate = 30f,
                RuntimeFrameCount = 2,
                RuntimeTrimStartFrame = 0,
                RequestEndpointSample = true
            };

            KimodoGenerationResultDto output = KimodoEditorGeneratePipeline.TrimRuntimeResultForOutput(
                request,
                result,
                KimodoMotionModelProfiles.DefaultModelName);

            Assert.That(output.motionData.FrameCount, Is.EqualTo(2));
            Assert.That(output.motionData.rootPositions[1].x, Is.EqualTo(1f));
            Assert.That(KimodoRawMotionUtility.TryParse(
                request.BakeMotionJsonCompact,
                out KimodoRawMotionData bakeMotion,
                out string error), Is.True, error);
            Assert.That(bakeMotion.FrameCount, Is.EqualTo(3));
            Assert.That(bakeMotion.rootPositions[2].x, Is.EqualTo(2f));
            Assert.That(request.BakeMotionData, Is.Not.Null);
            Assert.That(request.BakeMotionData.FrameCount, Is.EqualTo(3));
            Assert.That(request.EffectiveRuntimeFrameCount, Is.EqualTo(2));
            Assert.That(request.EffectiveBackendRuntimeFrameCount, Is.EqualTo(3));
            Assert.That(request.EffectiveBackendRuntimeDurationSeconds, Is.EqualTo(0.1f).Within(1e-6f));
            KimodoGenerationRequestDto backendRequest = KimodoEditorGeneratePipeline.CreateRuntimePipelineRequest(
                request,
                "walk",
                KimodoMotionModelProfiles.DefaultModelName);
            Assert.That(backendRequest.duration.HasValue, Is.True);
            Assert.That(backendRequest.duration.Value, Is.EqualTo(0.1f).Within(1e-6f));
        }

        [Test]
        public void ConstraintLossComparisonWithoutConstraints_IsNotApplicable()
        {
            JObject result = KimodoGenerationConstraintLossComparer.Compare(
                null, string.Empty, null, 0, 0f);

            Assert.That(result.Value<string>("status"), Is.EqualTo("not_applicable"));
            Assert.That(result.Value<int>("compared_count"), Is.Zero);
            Assert.That(result["frames"], Is.Empty);
        }

        [Test]
        public void TrimRuntimeResultForOutput_WithNoTrimReturnsTheOriginalResult()
        {
            var result = new KimodoGenerationResultDto
            {
                motionFormat = "analysis_only",
                rawStatus = "done",
                message = "analysis",
                motionJsonCompact = string.Empty,
                motionBytes = Array.Empty<byte>(),
                kmbAttachments = Array.Empty<KimodoBridgeKmbAttachment>(),
                motionRepFingerprint = "fp",
                resolvedSeed = 9,
                analysisJson = "{\"analysis\":true}"
            };
            var request = new KimodoEditorGenerateRequest
            {
                TargetFrameCount = 2,
                TargetFrameRate = 30f,
                RuntimeFrameCount = 2,
                RuntimeTrimStartFrame = 0
            };

            KimodoGenerationResultDto output = KimodoEditorGeneratePipeline.TrimRuntimeResultForOutput(
                request,
                result,
                KimodoMotionModelProfiles.DefaultModelName);

            Assert.That(output, Is.SameAs(result));
            Assert.That(output.motionJsonCompact, Is.Empty);
            Assert.That(output.motionBytes, Is.Empty);
            Assert.That(output.kmbAttachments, Is.Empty);
            Assert.That(output.analysisJson, Is.EqualTo("{\"analysis\":true}"));
        }

        [Test]
        public void BridgeCommand_RejectsNullRequestBeforeInvokingBackend()
        {
            var command = new KimodoBridgeCommand(new FakeBridgeTransport());
            Assert.Throws<ArgumentNullException>(() =>
                command.ExecuteAsync(null, null, default).GetAwaiter().GetResult());
        }

        private sealed class FakeBridgeTransport : IKimodoGenerationTransport
        {
            public KimodoGenerationRequestDto Request;
            public KimodoGenerationResultDto Result;

            public Task<KimodoGenerationResultDto> SendGenerateAsync(
                KimodoGenerationRequestDto request,
                Action<string> progress,
                CancellationToken token)
            {
                Request = request;
                progress?.Invoke("transport progress");
                return Task.FromResult(Result);
            }

        }

        private sealed class FakeModelTransport : IKimodoModelConfigurationTransport
        {
            public JObject ModelListResponse;
            public string RequestedModel;
            public string RequestedTextEncoderMode;

            public Task<JObject> ListModelConfigurationsAsync(
                string model,
                string textEncoderMode,
                string modelsRoot,
                Action<string> progress,
                CancellationToken token)
            {
                RequestedModel = model;
                RequestedTextEncoderMode = textEncoderMode;
                return Task.FromResult(ModelListResponse);
            }
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
