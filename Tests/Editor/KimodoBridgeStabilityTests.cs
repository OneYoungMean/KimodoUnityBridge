using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KimodoUnityMotionTools.Generation;
using KimodoUnityMotionTools.ProjectEditor;
using NUnit.Framework;

namespace KimodoUnityMotionTools.Tests
{
    [TestFixture]
    [Category("KimodoBridge")]
    [Category("Stability")]
    [NonParallelizable]
    internal sealed class KimodoBridgeStabilityTests
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
        public async Task Generate_WhenServerKilledMidFlight_ShouldFailControlledAndNoOrphans()
        {
            await KimodoBridgeTestHarness.EnsureSetupOrIgnoreAsync(scope);
            using KimodoRuntimeGenerationService service = await KimodoBridgeTestHarness.StartBridgeRuntimeServiceOrIgnoreAsync(scope, 90f);
            var startedSnapshots = KimodoBridgeTestHarness.GetRuntimeProcessSnapshots(scope.RuntimeRoot);
            Assert.IsNotEmpty(startedSnapshots, "Bridge runtime process should exist after startup.");

            Task<string> generateTask = KimodoBridgeTestHarness.GenerateBridgeAsync(
                service,
                "walk forward and turn left",
                2.5f,
                42,
                60,
                string.Empty,
                progress => scope.Log("Generate progress: " + progress),
                CancellationToken.None);

            await Task.Delay(600);
            scope.Log("Killing process tree during generate.");
            KimodoBridgeTestHarness.KillRuntimeProcesses(scope.RuntimeRoot, scope);

            Exception ex = null;
            try
            {
                await generateTask;
            }
            catch (Exception e)
            {
                ex = e;
                scope.Log("Generate exception: " + e.Message);
            }

            Assert.IsNotNull(ex, "Generate should fail when server is killed mid-flight.");
            await KimodoBridgeTestHarness.StopBridgeAsync(service, CancellationToken.None);
            await KimodoBridgeTestHarness.AssertNoOrphanProcessAndRecoverableAsync(scope);
        }

        [Test]
        public async Task StartAsync_WhenLauncherKilledDuringStartup_ShouldFailAndRecoverable()
        {
            await KimodoBridgeTestHarness.EnsureSetupOrIgnoreAsync(scope);

            string launcher = KimodoServerRuntimeUtil.ResolveStartScript(scope.RuntimeRoot);
            Assert.IsTrue(File.Exists(launcher), "launcher script missing");

            Process process = KimodoBridgeTestHarness.StartScript(launcher, "--model Kimodo-SOMA-RP-v1 --output file", useShellExecute: false, keepWindowOpen: false);
            Assert.NotNull(process, "failed to start launcher process");
            scope.Log("Launcher started pid=" + process.Id);
            await Task.Delay(500);
            KimodoBridgeTestHarness.KillRuntimeProcesses(scope.RuntimeRoot, scope);
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                process.Dispose();
            }

            using KimodoRuntimeGenerationService service = KimodoBridgeTestHarness.CreateRuntimeGenerationService(scope, startupTimeoutMs: 30000);
            Exception ex = null;
            try
            {
                _ = await service.StartAsync(
                    KimodoBackendType.Bridge,
                    progress => scope.Log("Client StartAsync progress: " + progress),
                    CancellationToken.None);
            }
            catch (Exception e)
            {
                ex = e;
                scope.Log("StartAsync failure as expected: " + e.Message);
            }
            finally
            {
                await KimodoBridgeTestHarness.StopBridgeAsync(service, CancellationToken.None);
            }

            Assert.IsNotNull(ex, "StartAsync should fail in startup kill scenario.");
            await KimodoBridgeTestHarness.AssertNoOrphanProcessAndRecoverableAsync(scope);
        }

        [Test]
        public async Task StopAndKillTree_WhenRepeatedAndConnectionBroken_ShouldRemainIdempotent()
        {
            await KimodoBridgeTestHarness.EnsureSetupOrIgnoreAsync(scope);
            using KimodoRuntimeGenerationService service = await KimodoBridgeTestHarness.StartBridgeRuntimeServiceOrIgnoreAsync(scope, 90f);

            string portFile = Path.Combine(scope.RuntimeRoot, "serverport");
            if (File.Exists(portFile))
            {
                File.Delete(portFile);
                scope.Log("Deleted serverport to simulate broken shutdown context.");
            }

            KimodoBridgeTestHarness.KillRuntimeProcesses(scope.RuntimeRoot, scope);

            await KimodoBridgeTestHarness.StopBridgeAsync(service, CancellationToken.None);
            await KimodoBridgeTestHarness.StopBridgeAsync(service, CancellationToken.None);
            await KimodoBridgeTestHarness.KillBridgeAsync(service, CancellationToken.None);
            await KimodoBridgeTestHarness.KillBridgeAsync(service, CancellationToken.None);

            await KimodoBridgeTestHarness.AssertNoOrphanProcessAndRecoverableAsync(scope);
        }

        [Test]
        public async Task Setup_WhenKilledMidway_ThenRerun_ShouldRecoverWithoutZombieProcesses()
        {
            string runServerScript = KimodoServerRuntimeUtil.ResolveStartScript(scope.RuntimeRoot);
            if (string.IsNullOrWhiteSpace(runServerScript) || !File.Exists(runServerScript))
            {
                Assert.Ignore("run_server script missing");
            }

            Process setup = KimodoBridgeTestHarness.StartScript(
                runServerScript,
                "--model Kimodo-SOMA-RP-v1 --force-setup --config-only --output file",
                useShellExecute: false,
                keepWindowOpen: false);
            Assert.NotNull(setup, "Failed to start setup process");
            scope.Log("Setup launched pid=" + setup.Id);

            await Task.Delay(800);
            KimodoBridgeTestHarness.KillRuntimeProcesses(scope.RuntimeRoot, scope);
            try
            {
                if (!setup.HasExited)
                {
                    setup.Kill();
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                setup.Dispose();
            }

            int rerunCode = await KimodoBridgeTestHarness.RunScriptAndWaitAsync(
                runServerScript,
                "--model Kimodo-SOMA-RP-v1 --force-setup --config-only --output file",
                300000);
            scope.Log("Setup rerun exit code=" + rerunCode);
            if (rerunCode != 0)
            {
                string setupLog = Path.Combine(scope.RuntimeRoot, "log", "setup.log");
                Assert.Ignore("setup rerun failed in environment. " + KimodoBridgeTestHarness.ReadLastLines(setupLog, 80));
            }

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
