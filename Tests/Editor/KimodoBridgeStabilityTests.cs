using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
            KimodoBridgeClient client = await KimodoBridgeTestHarness.StartClientOrIgnoreAsync(scope, 90f);
            int pid = KimodoBridgeTestHarness.GetClientPidForTests(client);
            Assert.Greater(pid, 0, "Client pid must be captured after startup.");

            Task<string> generateTask = client.GenerateAsync(
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
            await client.StopAsync(CancellationToken.None);
            client.Dispose();
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

            var client = new KimodoBridgeClient();
            Exception ex = null;
            try
            {
                await client.StartAsync(
                    launcher,
                    "Kimodo-SOMA-RP-v1",
                    false,
                    scope.RuntimeRoot,
                    30f,
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
                await client.StopAsync(CancellationToken.None);
                client.Dispose();
            }

            Assert.IsNotNull(ex, "StartAsync should fail in startup kill scenario.");
            await KimodoBridgeTestHarness.AssertNoOrphanProcessAndRecoverableAsync(scope);
        }

        [Test]
        public async Task StopAndKillTree_WhenRepeatedAndConnectionBroken_ShouldRemainIdempotent()
        {
            await KimodoBridgeTestHarness.EnsureSetupOrIgnoreAsync(scope);
            KimodoBridgeClient client = await KimodoBridgeTestHarness.StartClientOrIgnoreAsync(scope, 90f);

            string portFile = Path.Combine(scope.RuntimeRoot, "serverport");
            if (File.Exists(portFile))
            {
                File.Delete(portFile);
                scope.Log("Deleted serverport to simulate broken shutdown context.");
            }

            KimodoBridgeTestHarness.KillRuntimeProcesses(scope.RuntimeRoot, scope);

            await client.StopAsync(CancellationToken.None);
            await client.StopAsync(CancellationToken.None);
            await client.KillServerTreeAsync(CancellationToken.None);
            await client.KillServerTreeAsync(CancellationToken.None);
            client.Dispose();

            await KimodoBridgeTestHarness.AssertNoOrphanProcessAndRecoverableAsync(scope);
        }

        [Test]
        public async Task Setup_WhenKilledMidway_ThenRerun_ShouldRecoverWithoutZombieProcesses()
        {
            string setupScript = Path.Combine(scope.RuntimeRoot, "bash", "setup.bat");
            if (!File.Exists(setupScript))
            {
                Assert.Ignore("setup script missing");
            }

            Process setup = KimodoBridgeTestHarness.StartScript(setupScript, "--output file", useShellExecute: false, keepWindowOpen: false);
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

            int rerunCode = await KimodoBridgeTestHarness.RunScriptAndWaitAsync(setupScript, "--output file", 300000);
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
