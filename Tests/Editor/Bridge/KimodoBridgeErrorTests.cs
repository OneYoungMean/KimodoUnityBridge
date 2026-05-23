using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KimodoUnityMotionTools.Generation;
using KimodoUnityMotionTools.ProjectEditor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.Tests
{
    [TestFixture]
    [Category("KimodoBridge")]
    [Category("Errors")]
    [NonParallelizable]
    internal sealed class KimodoBridgeErrorTests
    {
        private KimodoRuntimeScope scope;

        [SetUp]
        public void SetUp()
        {
            scope = KimodoBridgeTestHarness.CreateRuntimeScope(TestContext.CurrentContext.Test.Name);
        }

        [TearDown]
        public async Task TearDownAsync()
        {
            string workingRoot = scope?.WorkingRoot;
            await KimodoBridgeTestHarness.CleanupScopeAsync(scope);
            scope?.Dispose();
            if (!string.IsNullOrWhiteSpace(workingRoot))
            {
                TryDeleteDirectory(workingRoot);
            }
            scope = null;
        }

        [Test]
        public async Task EmptyPrompt_InEditorFlow_ShouldFailFastWithoutStartingServer()
        {
            await KimodoBridgeTestHarness.EnsureSetupOrIgnoreAsync(scope);

            var clip = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            clip.generationBackend = KimodoGenerationBackend.KimodoBridge;
            clip.motionPrompt = string.Empty;
            clip.generationFrames = 150;
            clip.diffusionSteps = 80;
            clip.randomSeed = false;
            clip.seed = 123;

            var editor = UnityEditor.Editor.CreateEditor(clip, typeof(KimodoPlayableClipEditor)) as KimodoPlayableClipEditor;
            Assert.NotNull(editor);

            try
            {
                editor.SetBridgeGenerationInputsForTests(string.Empty, 150, 80, false, 123);
                await editor.GenerateForTestsAsync();

                Assert.AreEqual("Prompt is empty.", editor.LastErrorForTests);
                var snapshots = KimodoBridgeTestHarness.GetRuntimeProcessSnapshots(scope.RuntimeRoot);
                Assert.IsEmpty(snapshots, "Empty prompt should not launch server process.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(editor);
                UnityEngine.Object.DestroyImmediate(clip);
            }

            await KimodoBridgeTestHarness.AssertNoOrphanProcessAndRecoverableAsync(scope);
        }

        [Test]
        public async Task InvalidServerportContent_ShouldSurfaceUnreachableErrorAndRecoverable()
        {
            await KimodoBridgeTestHarness.EnsureSetupOrIgnoreAsync(scope);

            string portFile = Path.Combine(scope.RuntimeRoot, "serverport");
            File.WriteAllText(portFile, "invalid_port_value");
            scope.Log("Wrote invalid serverport content.");

            using KimodoRuntimeGenerationService service = KimodoBridgeTestHarness.CreateRuntimeGenerationService(scope);
            Exception ex = null;
            try
            {
                _ = await KimodoBridgeTestHarness.GenerateBridgeAsync(
                    service,
                    "test",
                    2f,
                    1,
                    20,
                    string.Empty,
                    _ => { },
                    CancellationToken.None);
            }
            catch (Exception e)
            {
                ex = e;
                scope.Log("Generate expected failure: " + e.Message);
            }
            finally
            {
                await KimodoBridgeTestHarness.StopBridgeAsync(service, CancellationToken.None);
            }

            Assert.IsNotNull(ex);
            StringAssert.Contains("Bridge port is unreachable", ex.Message);
            await KimodoBridgeTestHarness.AssertNoOrphanProcessAndRecoverableAsync(scope);
        }

        [Test]
        public async Task UnreachablePort_ShouldFailWithReadableErrorAndRecoverable()
        {
            await KimodoBridgeTestHarness.EnsureSetupOrIgnoreAsync(scope);

            string portFile = Path.Combine(scope.RuntimeRoot, "serverport");
            File.WriteAllText(portFile, "127.0.0.1:6553");
            scope.Log("Wrote unreachable serverport endpoint.");

            using KimodoRuntimeGenerationService service = KimodoBridgeTestHarness.CreateRuntimeGenerationService(scope);
            Exception ex = null;
            try
            {
                _ = await KimodoBridgeTestHarness.GenerateBridgeAsync(
                    service,
                    "walk",
                    2f,
                    10,
                    30,
                    string.Empty,
                    _ => { },
                    CancellationToken.None);
            }
            catch (Exception e)
            {
                ex = e;
                scope.Log("Generate expected failure: " + e.Message);
            }
            finally
            {
                await KimodoBridgeTestHarness.StopBridgeAsync(service, CancellationToken.None);
            }

            Assert.IsNotNull(ex);
            StringAssert.Contains("Bridge port is unreachable", ex.Message);
            await KimodoBridgeTestHarness.AssertNoOrphanProcessAndRecoverableAsync(scope);
        }

        [Test]
        public async Task GenerateBoundaryInputs_WhenServerReturnsError_ShouldNotDeadlock()
        {
            await KimodoBridgeTestHarness.EnsureSetupOrIgnoreAsync(scope);
            using KimodoRuntimeGenerationService service = await KimodoBridgeTestHarness.StartBridgeRuntimeServiceOrIgnoreAsync(scope, 90f);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Exception ex = null;
            try
            {
                _ = await KimodoBridgeTestHarness.GenerateBridgeAsync(
                    service,
                    prompt: "",
                    durationSeconds: 0.01f,
                    seed: int.MaxValue,
                    diffusionSteps: 1000,
                    constraintsJson: "{not-json}",
                    progress: p => scope.Log("Boundary generate progress: " + p),
                    token: cts.Token);
            }
            catch (Exception e)
            {
                ex = e;
                scope.Log("Boundary generate exception: " + e.Message);
            }
            finally
            {
                await KimodoBridgeTestHarness.StopBridgeAsync(service, CancellationToken.None);
            }

            Assert.IsNotNull(ex, "Boundary-input generate should fail or be canceled in constrained environments.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(ex.Message), "Error should be readable.");
            await KimodoBridgeTestHarness.AssertNoOrphanProcessAndRecoverableAsync(scope);
        }

        [Test]
        public async Task EmptyOrMalformedBridgeResponse_ShouldSurfaceReadableErrorsAndRecover()
        {
            await RunSyntheticBridgeResponseCaseAsync(
                "empty-line",
                writer =>
                {
                    writer.WriteLine();
                    writer.Flush();
                },
                expectedErrorContains: "Empty bridge response");

            await RunSyntheticBridgeResponseCaseAsync(
                "non-json",
                writer =>
                {
                    writer.WriteLine("not-json");
                    writer.Flush();
                },
                expectedErrorContains: "Unexpected character");
        }

        private async Task RunSyntheticBridgeResponseCaseAsync(
            string caseName,
            Action<StreamWriter> responder,
            string expectedErrorContains)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string portFile = Path.Combine(scope.RuntimeRoot, "serverport");
            File.WriteAllText(portFile, $"127.0.0.1:{port}");
            scope.Log($"Synthetic bridge [{caseName}] listening at {port}.");

            Task serverTask = Task.Run(async () =>
            {
                using TcpClient accepted = await listener.AcceptTcpClientAsync();
                using NetworkStream stream = accepted.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                _ = await reader.ReadLineAsync();
                responder(writer);
            });

            using KimodoRuntimeGenerationService service = KimodoBridgeTestHarness.CreateRuntimeGenerationService(scope);
            Exception ex = null;
            try
            {
                _ = await KimodoBridgeTestHarness.GenerateBridgeAsync(
                    service,
                    "synthetic",
                    2f,
                    1,
                    20,
                    string.Empty,
                    _ => { },
                    CancellationToken.None);
            }
            catch (Exception e)
            {
                ex = e;
                scope.Log($"Synthetic bridge [{caseName}] exception: {e.Message}");
            }
            finally
            {
                await KimodoBridgeTestHarness.StopBridgeAsync(service, CancellationToken.None);
                listener.Stop();
                try
                {
                    await serverTask;
                }
                catch
                {
                    // ignore
                }
            }

            Assert.IsNotNull(ex, $"Expected failure for synthetic case '{caseName}'.");
            StringAssert.Contains(expectedErrorContains, ex.Message);
            await KimodoBridgeTestHarness.AssertNoOrphanProcessAndRecoverableAsync(scope);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // ignore cleanup failure
            }
        }
    }
}
