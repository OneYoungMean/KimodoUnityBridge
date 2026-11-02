using System;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace KimodoUnityBridge.Command
{
    /// <summary>
    /// Framework-neutral command discovery and dispatch entry point.
    /// </summary>
    public static class command_dispatcher
    {
        public static string GetCommandDefinitionsJson()
        {
            return command_context.GetCommandDefinitionsJson();
        }

        public static string Invoke(string commandName, string argumentsJson = "{}")
        {
            DateTime startedUtc = DateTime.UtcNow;
            long startedTimestamp = Stopwatch.GetTimestamp();
            string sceneStateBefore = KimodoCommandExecutionDiagnostics.DescribeLoadedScenes();
            string safeCommandName = KimodoCommandExecutionDiagnostics.Escape(commandName ?? string.Empty);
            string outcome = "returned";

            Debug.Log($"[KimodoCommandTiming] event=start utc={startedUtc:O} command=\"{safeCommandName}\" scenes={sceneStateBefore}");
            try
            {
                return command_context.Invoke(commandName, argumentsJson);
            }
            catch (Exception exception)
            {
                outcome = $"exception:{exception.GetType().Name}";
                throw;
            }
            finally
            {
                double elapsedMilliseconds = (Stopwatch.GetTimestamp() - startedTimestamp) * 1000d / Stopwatch.Frequency;
                string sceneStateAfter = KimodoCommandExecutionDiagnostics.DescribeLoadedScenes();
                Debug.Log(
                    $"[KimodoCommandTiming] event=complete startedUtc={startedUtc:O} completedUtc={DateTime.UtcNow:O} " +
                    $"elapsedMs={elapsedMilliseconds:F1} command=\"{safeCommandName}\" outcome={outcome} " +
                    $"scenesBefore={sceneStateBefore} scenesAfter={sceneStateAfter}");
            }
        }

        /// <summary>
        /// Awaitable command entry for hosts that need the cooperative Editor wait window.
        /// The synchronous Invoke entry remains immediate for existing editor scripts.
        /// </summary>
        public static Task<string> InvokeAsync(string commandName, string argumentsJson = "{}")
        {
            return command_context.InvokeAsync(commandName, argumentsJson);
        }
    }

    [InitializeOnLoad]
    internal static class KimodoCommandExecutionDiagnostics
    {
        static KimodoCommandExecutionDiagnostics()
        {
            EditorSceneManager.sceneClosing -= OnSceneClosing;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorSceneManager.sceneClosed -= OnSceneClosed;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
        }

        internal static string DescribeLoadedScenes()
        {
            var scenes = new StringBuilder("[");
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                if (index > 0)
                {
                    scenes.Append(",");
                }

                Scene scene = SceneManager.GetSceneAt(index);
                scenes.Append("{name=\"").Append(Escape(scene.name))
                    .Append("\",path=\"").Append(Escape(scene.path))
                    .Append("\",loaded=").Append(scene.isLoaded)
                    .Append(",dirty=").Append(scene.isDirty)
                    .Append("}");
            }

            Scene activeScene = SceneManager.GetActiveScene();
            scenes.Append("] active=\"").Append(Escape(activeScene.name))
                .Append("\" activePath=\"").Append(Escape(activeScene.path)).Append("\"");
            return scenes.ToString();
        }

        internal static string Escape(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static void OnSceneClosing(Scene scene, bool removingScene)
        {
            Debug.Log(
                $"[KimodoSceneLifecycle] event=closing utc={DateTime.UtcNow:O} " +
                $"scene=\"{Escape(scene.name)}\" path=\"{Escape(scene.path)}\" removing={removingScene} " +
                $"scenes={DescribeLoadedScenes()}");
        }

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            Debug.Log(
                $"[KimodoSceneLifecycle] event=opened utc={DateTime.UtcNow:O} mode={mode} " +
                $"scene=\"{Escape(scene.name)}\" path=\"{Escape(scene.path)}\" scenes={DescribeLoadedScenes()}");
        }

        private static void OnSceneClosed(Scene scene)
        {
            Debug.Log(
                $"[KimodoSceneLifecycle] event=closed utc={DateTime.UtcNow:O} " +
                $"scene=\"{Escape(scene.name)}\" path=\"{Escape(scene.path)}\" scenes={DescribeLoadedScenes()}");
        }

        private static void OnActiveSceneChanged(Scene previousScene, Scene nextScene)
        {
            Debug.Log(
                $"[KimodoSceneLifecycle] event=active_changed utc={DateTime.UtcNow:O} " +
                $"from=\"{Escape(previousScene.path)}\" to=\"{Escape(nextScene.path)}\" " +
                $"scenes={DescribeLoadedScenes()}");
        }
    }
}
