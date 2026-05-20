using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using KimodoUnityMotionTools.ProjectEditor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.Tests
{
    [TestFixture]
    [Category("KimodoBridge")]
    [Category("UserChaos")]
    [NonParallelizable]
    internal sealed class KimodoBridgeUserChaosTests
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
        public async Task HighFrequencyGenerateClicks_ShouldKeepSingleActiveGenerationAndNoLeaks()
        {
            await KimodoBridgeTestHarness.EnsureSetupOrIgnoreAsync(scope);
            KimodoBridgeClient client = await KimodoBridgeTestHarness.StartClientOrIgnoreAsync(scope, 90f);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task<string>[] tasks = new Task<string>[5];
            for (int i = 0; i < tasks.Length; i++)
            {
                int idx = i;
                tasks[i] = Task.Run(async () =>
                {
                    try
                    {
                        return await client.GenerateAsync(
                            prompt: "chaos click " + idx,
                            durationSeconds: 2.0f,
                            seed: 100 + idx,
                            diffusionSteps: 50,
                            constraintsJson: string.Empty,
                            progress: p => scope.Log($"generate[{idx}]={p}"),
                            token: cts.Token);
                    }
                    catch (Exception e)
                    {
                        scope.Log($"generate[{idx}] failed: {e.Message}");
                        throw;
                    }
                });
            }

            await Task.Delay(600);
            await client.StopAsync(CancellationToken.None);

            int done = 0;
            int failed = 0;
            foreach (Task<string> task in tasks)
            {
                try
                {
                    await task;
                    done++;
                }
                catch
                {
                    failed++;
                }
            }

            scope.Log($"High frequency result: done={done}, failed={failed}");
            Assert.GreaterOrEqual(failed, 1, "At least some concurrent requests should fail after forced stop.");

            client.Dispose();
            await KimodoBridgeTestHarness.AssertNoOrphanProcessAndRecoverableAsync(scope);
        }

        [Test]
        public async Task GenerateThenImmediateCancel_ShouldFinishQuicklyAndAllowRestart()
        {
            await KimodoBridgeTestHarness.EnsureSetupOrIgnoreAsync(scope);
            KimodoBridgeClient client = await KimodoBridgeTestHarness.StartClientOrIgnoreAsync(scope, 90f);

            using var cts = new CancellationTokenSource();
            Task<string> generating = client.GenerateAsync(
                "cancel test",
                2.5f,
                77,
                80,
                string.Empty,
                p => scope.Log("cancel scenario progress: " + p),
                cts.Token);

            await Task.Delay(200);
            cts.Cancel();
            scope.Log("Generation cancellation token requested.");

            Exception canceledEx = null;
            try
            {
                await generating;
            }
            catch (Exception e)
            {
                canceledEx = e;
                scope.Log("Canceled generate exception: " + e.GetType().Name + " " + e.Message);
            }

            Assert.IsNotNull(canceledEx, "Generate should cancel/fail promptly after cancel request.");

            await client.StopAsync(CancellationToken.None);
            client.Dispose();

            KimodoBridgeClient restarted = await KimodoBridgeTestHarness.StartClientOrIgnoreAsync(scope, 90f);
            await restarted.StopAsync(CancellationToken.None);
            restarted.Dispose();
            await KimodoBridgeTestHarness.AssertNoOrphanProcessAndRecoverableAsync(scope);
        }

        [Test]
        public async Task GenerateThenImmediateStopServer_ShouldFailControlledAndStateRecovered()
        {
            await KimodoBridgeTestHarness.EnsureSetupOrIgnoreAsync(scope);
            KimodoBridgeClient client = await KimodoBridgeTestHarness.StartClientOrIgnoreAsync(scope, 90f);

            Task<string> generation = client.GenerateAsync(
                "stop server now",
                3.0f,
                101,
                90,
                string.Empty,
                p => scope.Log("stop-after-generate progress: " + p),
                CancellationToken.None);

            await Task.Delay(300);
            await client.StopAsync(CancellationToken.None);

            Exception ex = null;
            try
            {
                await generation;
            }
            catch (Exception e)
            {
                ex = e;
                scope.Log("Generate after stop exception: " + e.Message);
            }

            Assert.IsNotNull(ex, "Generate should fail when server is stopped immediately.");
            client.Dispose();
            await KimodoBridgeTestHarness.AssertNoOrphanProcessAndRecoverableAsync(scope);
        }

        [Test]
        public async Task GenerateThenTryFixConflict_ShouldNotDeadlockAndCanRestart()
        {
            await KimodoBridgeTestHarness.EnsureSetupOrIgnoreAsync(scope);
            KimodoBridgeClient client = await KimodoBridgeTestHarness.StartClientOrIgnoreAsync(scope, 90f);

            Task<string> generation = client.GenerateAsync(
                "tryfix conflict",
                2.5f,
                202,
                90,
                string.Empty,
                p => scope.Log("tryfix conflict progress: " + p),
                CancellationToken.None);

            await Task.Delay(300);
            bool tryFixResult = await RunTryFixViaReflectionAsync(scope);
            scope.Log("TryFix result=" + tryFixResult);

            Exception ex = null;
            try
            {
                await generation;
            }
            catch (Exception e)
            {
                ex = e;
                scope.Log("Generation exception in tryfix conflict: " + e.Message);
            }

            await client.StopAsync(CancellationToken.None);
            client.Dispose();

            Assert.IsTrue(tryFixResult || ex != null, "Either TryFix should run or generation should fail controlled.");
            KimodoBridgeClient restarted = await KimodoBridgeTestHarness.StartClientOrIgnoreAsync(scope, 90f);
            await restarted.StopAsync(CancellationToken.None);
            restarted.Dispose();
            await KimodoBridgeTestHarness.AssertNoOrphanProcessAndRecoverableAsync(scope);
        }

        private async Task<bool> RunTryFixViaReflectionAsync(KimodoRuntimeScope runtime)
        {
            Type providerType = typeof(KimodoServerManagerSettingsProvider);
            var ctor = providerType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(SettingsScope) }, null);
            if (ctor == null)
            {
                runtime.Log("TryFix reflection failed: constructor missing.");
                return false;
            }

            object provider = ctor.Invoke(new object[] { "Project/Kimodo Server Manager", SettingsScope.Project });
            MethodInfo refresh = providerType.GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo tryFix = providerType.GetMethod("TryFix", BindingFlags.Instance | BindingFlags.NonPublic);
            if (refresh == null || tryFix == null)
            {
                runtime.Log("TryFix reflection failed: methods missing.");
                return false;
            }

            return await Task.Run(() =>
            {
                try
                {
                    refresh.Invoke(provider, null);
                    tryFix.Invoke(provider, null);
                    return true;
                }
                catch (Exception e)
                {
                    runtime.Log("TryFix invocation error: " + e.Message);
                    return false;
                }
            });
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
