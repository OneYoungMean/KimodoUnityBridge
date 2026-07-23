using System;
using System.IO;
using NUnit.Framework;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoQuickServerSettingsTests
    {
        [Test]
        public void ConfiguredDirectoryProvidesRuntimeRootAndPackageVersion()
        {
            string directory = Path.Combine(Path.GetTempPath(), "kimodo-quickserver-" + Guid.NewGuid().ToString("N"));
            string previousPath = KimodoPlayableClipGenerationSettings.instance.QuickServerPath;
            string previousOverride = KimodoServerRuntimeUtil.RuntimeRootOverrideForTests;
            Directory.CreateDirectory(directory);

            try
            {
                File.WriteAllText(Path.Combine(directory, "package.json"), "{\"version\":\"1.3.0\"}");
                KimodoServerRuntimeUtil.RuntimeRootOverrideForTests = null;
                KimodoPlayableClipGenerationSettings.instance.QuickServerPath = directory;

                Assert.That(KimodoServerRuntimeUtil.GetRuntimeRootPath(), Is.EqualTo(Path.GetFullPath(directory)));
                Assert.That(KimodoServerRuntimeUtil.ReadQuickServerVersion(directory), Is.EqualTo("1.3.0"));
                File.Delete(Path.Combine(directory, "package.json"));
                Assert.That(KimodoServerRuntimeUtil.ReadQuickServerVersion(directory), Is.EqualTo("unknown"));
            }
            finally
            {
                KimodoPlayableClipGenerationSettings.instance.QuickServerPath = previousPath;
                KimodoServerRuntimeUtil.RuntimeRootOverrideForTests = previousOverride;
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
