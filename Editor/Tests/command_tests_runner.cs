using UnityEditor;
using UnityEngine;
using UnityEditor.TestTools.TestRunner.Api;

namespace KimodoBridge.Editor.Tests
{
    internal static class command_tests_runner
    {
        [MenuItem("Kimodo/Tests/Run Command Tests")]
        private static void Run()
        {
            TestRunnerApi api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var filter = new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[] { "KimodoTool.Editor.Tests" },
                testNames = new[] { typeof(command_tests).FullName }
            };
            api.Execute(new ExecutionSettings(filter));
        }
    }
}
