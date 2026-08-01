using UnityEditor;
using UnityEngine;
using UnityEditor.TestTools.TestRunner.Api;

namespace KimodoBridge.Editor.Tests
{
    internal static class KimodoMcpToolsEditModeRunner
    {
        [MenuItem("Kimodo/Tests/Run MCP Tool Tests")]
        private static void Run()
        {
            TestRunnerApi api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var filter = new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[] { "KimodoTool.Editor.Tests" },
                testNames = new[] { typeof(KimodoMcpToolsTests).FullName }
            };
            api.Execute(new ExecutionSettings(filter));
        }
    }
}
