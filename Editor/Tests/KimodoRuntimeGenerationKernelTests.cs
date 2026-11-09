using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoRuntimeGenerationKernelTests
    {
        [Test]
        public void KimodoKernel_BuildsDiscreteRequestAndDiscardsStaleResult()
        {
            var kernel = new KimodoRuntimeGenerationKernel();
            var session = new KimodoRuntimeGenerationSession();
            KimodoRuntimeKernelInput input = CreateInput(KimodoMotionModelProfiles.DefaultModelName);
            input.GenerationDuration = 4f;
            input.DiffusionSteps = 12;
            input.Constraints.StageRoot2D(new KimodoRuntimeRoot2DConstraint
            {
                sampleTime = 1.0,
                protocolRoot = new UnityEngine.Vector2(1f, 2f)
            });
            Assert.That(input.Constraints.Commit(), Is.True);
            input.ConstraintSnapshot = input.Constraints.CaptureSnapshot();

            KimodoRuntimeGenerationRequest request = kernel.CreateRequest(input, session);

            Assert.That(kernel.IsArdy, Is.False);
            Assert.That(request.Payload.duration, Is.EqualTo(4f));
            Assert.That(request.Payload.time_as_double, Is.EqualTo(0d));
            Assert.That(request.Payload.ardy_session_update_only, Is.False);
            Assert.That(
                JArray.Parse(request.Payload.constraints.json)[0]["type"]?.Value<string>(),
                Is.EqualTo(KimodoRuntimeConstraints.Root2DType));
            Assert.That(request.SentPrompt, Is.True);
            Assert.That(request.SentConstraints, Is.True);
            Assert.That(kernel.ShouldDiscardResult(staleRequest: true, lifetimeCancelled: false), Is.True);
        }

        [Test]
        public void ArdyKernel_KeepsSessionSeedAndOnlySendsDirtyInputs()
        {
            Assert.That(
                KimodoMotionModelProfiles.TryGetArdy(
                    KimodoMotionModelProfiles.ArdyCoreModelName,
                    out KimodoMotionModelProfile profile),
                Is.True);
            var kernel = new KimodoRuntimeArdyGenerationKernel();
            kernel.Reset(1.25f);
            var session = new KimodoRuntimeGenerationSession();
            KimodoRuntimeKernelInput input = CreateInput(profile.ModelName);
            input.ArdyProfile = profile;
            input.ArdyAutoHistory = false;
            input.ArdyHistoryWeight = 0.4f;
            input.ArdyPlaybackReserveSetting = 1.25f;
            input.Constraints.StageRoot2D(new KimodoRuntimeRoot2DConstraint
            {
                sampleTime = 1.0,
                protocolRoot = new UnityEngine.Vector2(1f, 2f)
            });
            Assert.That(input.Constraints.Commit(), Is.True);
            input.ConstraintSnapshot = input.Constraints.CaptureSnapshot();

            KimodoRuntimeGenerationRequest first = kernel.CreateRequest(input, session);
            kernel.CompleteRequest(first, staleRequest: false);
            KimodoRuntimeGenerationRequest second = kernel.CreateRequest(input, session);

            Assert.That(kernel.IsArdy, Is.True);
            Assert.That(first.Payload.duration, Is.Null);
            Assert.That(first.Payload.time_as_double, Is.EqualTo(0d));
            Assert.That(first.Payload.ardy_session_update_only, Is.False);
            Assert.That(first.Payload.ardy_history_weight, Is.EqualTo(0.4d).Within(1e-5));
            Assert.That(first.Payload.ardy_playback_reserve_seconds, Is.EqualTo(1.25d).Within(1e-5));
            Assert.That(second.Payload.ardy_session_update_only, Is.True);
            Assert.That(second.Payload.prompt, Is.Null);
            Assert.That(second.Payload.constraints.json, Is.EqualTo("[]"));
            Assert.That(second.Payload.seed, Is.EqualTo(first.Payload.seed));
            Assert.That(second.SentPrompt, Is.False);
            Assert.That(second.SentConstraints, Is.False);
            Assert.That(kernel.ShouldDiscardResult(staleRequest: true, lifetimeCancelled: false), Is.False);

            kernel.ConstraintsChanged();
            KimodoRuntimeGenerationRequest updated = kernel.CreateRequest(input, session);
            Assert.That(updated.SentConstraints, Is.True);
            Assert.That(
                JArray.Parse(updated.Payload.constraints.json)[0]["type"]?.Value<string>(),
                Is.EqualTo(KimodoRuntimeConstraints.Root2DType));
        }

        [Test]
        public void KernelCancellation_CancelsTheSharedInFlightSlot()
        {
            var session = new KimodoRuntimeGenerationSession();
            session.Start();
            Assert.That(session.TryBeginGeneration(
                session.LifetimeToken,
                out CancellationTokenSource generation,
                out _,
                out _), Is.True);

            new KimodoRuntimeArdyGenerationKernel().CancelGeneration(session);

            Assert.That(generation.IsCancellationRequested, Is.True);
            session.EndGeneration(generation);
            session.Dispose();
        }

        private static KimodoRuntimeKernelInput CreateInput(string modelName)
        {
            return new KimodoRuntimeKernelInput
            {
                ModelName = modelName,
                Prompt = "walk",
                ModelsRoot = string.Empty,
                TextEncoderMode = KimodoTextEncoderMode.HighPrecision,
                FixedSeed = 42,
                RandomSeed = false,
                DiffusionSteps = 10,
                MotionPlayer = new KimodoRuntimeMotionPlayer(),
                Constraints = new KimodoRuntimeConstraints()
            };
        }
    }
}
