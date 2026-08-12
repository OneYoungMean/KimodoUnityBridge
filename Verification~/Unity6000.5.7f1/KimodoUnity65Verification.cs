using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KimodoUnityBridge.Command;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class KimodoUnity65Verification
{
    private const string PackageName = "com.unity.kimodo_unity_motion_tools";
    private const string CharacterName = "Unity65VerifyHumanoid";
    private const string GeneratedName = "Unity65_SingleClip";
    private const string BakedName = "Unity65_BakeProbe";
    private static string outputRoot;
    private static string eventsPath;
    private static int sequence;

    public static async void Run()
    {
        int exitCode = 0;
        try
        {
            outputRoot = CommandLineValue("-kimodoOutput");
            if (string.IsNullOrWhiteSpace(outputRoot))
                throw new InvalidOperationException("-kimodoOutput is required.");
            Directory.CreateDirectory(outputRoot);
            eventsPath = Path.Combine(outputRoot, "dispatcher-events.jsonl");
            if (File.Exists(eventsPath)) File.Delete(eventsPath);

            Record("metadata", "runtime", null, null, new JObject
            {
                ["unity_version"] = Application.unityVersion,
                ["project_path"] = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty,
                ["batch_mode"] = Application.isBatchMode,
                ["graphics_device"] = SystemInfo.graphicsDeviceName,
                ["graphics_device_type"] = SystemInfo.graphicsDeviceType.ToString(),
                ["operating_system"] = SystemInfo.operatingSystem
            });
            RecordPackageResolution();

            // These are intentionally the first two command-dispatcher observations.
            string definitionsRaw = command_dispatcher.GetCommandDefinitionsJson();
            Record("discovery", "GetCommandDefinitionsJson", null, definitionsRaw, Parse(definitionsRaw));
            JObject definitions = Parse(definitionsRaw);
            JObject initialHelp = Invoke("basic", "kimodo_help", new JObject());

            foreach (string name in definitions["tools"]?.Children<JObject>()
                         .Select(tool => tool.Value<string>("name"))
                         .Where(name => !string.IsNullOrWhiteSpace(name)) ?? Enumerable.Empty<string>())
            {
                Invoke("schema", "kimodo_help", new JObject { ["command"] = name });
            }
            Invoke("error", "kimodo_help", new JObject { ["section"] = "not-a-section" });

            Invoke("basic", "kimodo_build_root2d_path", new JObject
            {
                ["shape"] = "turn", ["duration_frames"] = 60, ["max_speed"] = 1.0,
                ["acceleration"] = 2.0, ["direction"] = "left", ["turn_degrees"] = 90
            });
            Invoke("error", "kimodo_build_root2d_path", new JObject { ["shape"] = "triangle" });
            Invoke("error", "kimodo_get_generation", new JObject
            {
                ["request_id"] = "00000000-0000-0000-0000-000000000001"
            });
            Invoke("error", "kimodo_cancel_generation", new JObject
            {
                ["request_id"] = "not-a-guid"
            });

            InvokeRaw("basic", "kimodo_debug_install_server", "{}");
            InvokeRaw("error", "kimodo_debug_install_server", "{");
            Invoke("discovery", "kimodo_help", new JObject { ["section"] = "models" });

            CreateEmptyVerificationScene();
            JObject open = Invoke("basic", "session_open", new JObject());
            InvokeRaw("error", "session_open", "{");
            Invoke("basic", "query_current_session", new JObject { ["query"] = "characters" });
            Invoke("error", "query_current_session", new JObject());

            CreateSceneCharacter();
            JObject add = Invoke("basic", "session_try_add", new JObject
            {
                ["kind"] = "character", ["character"] = CharacterName
            });
            Invoke("error", "session_try_add", new JObject
            {
                ["kind"] = "character", ["character"] = "MissingCharacter"
            });
            Invoke("basic", "query_current_session", new JObject { ["query"] = "characters" });

            JObject readPose = Invoke("basic", "pose_get", new JObject
            {
                ["pose"] = Locator(CharacterName, 0)
            });
            Invoke("error", "pose_get", new JObject { ["pose"] = Locator("MissingPoseSource", 0) });

            JObject copiedPose = Invoke("basic", "pose_copy", new JObject
            {
                ["character"] = CharacterName, ["pose"] = Locator(CharacterName, 0)
            });
            Invoke("error", "pose_copy", new JObject
            {
                ["character"] = CharacterName, ["pose"] = Locator("MissingPoseSource", 0)
            });
            JObject writable = copiedPose["pose"] as JObject;
            if (writable == null) writable = Locator(CharacterName + ".Poses", 0);

            Invoke("basic", "pose_set", new JObject
            {
                ["pose"] = writable.DeepClone(),
                ["data"] = new JObject
                {
                    ["muscles"] = new JObject { ["Left Upper Leg Front-Back"] = 0.2 }
                }
            });
            Invoke("error", "pose_set", new JObject
            {
                ["pose"] = Locator(CharacterName, 0),
                ["data"] = new JObject { ["muscles"] = new JObject() }
            });

            JObject poseData = readPose["data"] as JObject ?? new JObject
            {
                ["root"] = new JObject
                {
                    ["position"] = new JArray(0, 0, 0),
                    ["rotation"] = new JArray(0, 0, 0, 1)
                },
                ["muscles"] = new JObject(),
                ["foot_ik"] = new JObject()
            };
            Invoke("basic", "pose_create", new JObject
            {
                ["character"] = CharacterName, ["pose"] = poseData.DeepClone()
            });
            Invoke("error", "pose_create", new JObject { ["character"] = CharacterName });

            JObject picture = Invoke("basic", "query_picture", new JObject
            {
                ["poses"] = new JArray(writable.DeepClone()), ["resolution"] = 256, ["scale"] = 1.0
            });
            Invoke("error", "query_picture", new JObject());
            RecordFileFromResponse("picture", picture, "image_path");

            Invoke("error", "session_try_remove", new JObject
            {
                ["kind"] = "invalid", ["character"] = CharacterName
            });

            Invoke("error", "kimodo_analyze", new JObject { ["character"] = CharacterName });
            Invoke("error", "kimodo_bake_range", new JObject
            {
                ["character"] = CharacterName, ["start_frame"] = 2, ["end_frame"] = 1
            });
            Invoke("error", "kimodo_generate_animation", new JObject
            {
                ["character"] = CharacterName,
                ["prompt"] = "stand still and breathe naturally",
                ["duration_frames"] = 60,
                ["output_folder"] = "../OutsideAssets"
            });

            JObject started = Invoke("basic", "kimodo_generate_animation", new JObject
            {
                ["character"] = CharacterName,
                ["prompt"] = "stand still and breathe naturally",
                ["duration_frames"] = 60,
                ["seed"] = 650507,
                ["output_mode"] = "humanoid_muscle",
                ["output_folder"] = "Assets/KimodoVerificationGenerated",
                ["name"] = GeneratedName,
                ["constraints"] = new JArray
                {
                    new JObject
                    {
                        ["frame"] = 0, ["type"] = "fullbody", ["pose"] = writable.DeepClone()
                    },
                    new JObject
                    {
                        ["frame"] = 59, ["type"] = "root2d",
                        ["position"] = new JArray(0.5, 0.0), ["heading"] = new JArray(1.0, 0.0)
                    }
                }
            });

            JObject terminal = null;
            string requestId = started.Value<string>("request_id");
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                terminal = await PollGeneration(requestId, TimeSpan.FromMinutes(15));
                Invoke("basic", "kimodo_cancel_generation", new JObject
                {
                    ["request_id"] = requestId, ["reason"] = "terminal-state no-op verification"
                });
                Invoke("basic", "kimodo_get_generation", new JObject { ["request_id"] = requestId });
            }
            else
            {
                Record("workflow", "generation_not_accepted", null, null, started);
            }

            JObject animations = Invoke("basic", "query_current_session", new JObject
            {
                ["query"] = "character_animations", ["character"] = CharacterName
            });
            string generatedAnimation = terminal?.Value<string>("animation") ?? started.Value<string>("animation");
            if (!string.IsNullOrWhiteSpace(generatedAnimation))
            {
                Invoke("basic", "query_current_session", new JObject
                {
                    ["query"] = "animation", ["character"] = CharacterName, ["animation"] = generatedAnimation
                });
                Invoke("basic", "query_current_session", new JObject
                {
                    ["query"] = "animation_constraints", ["character"] = CharacterName, ["animation"] = generatedAnimation
                });
                Invoke("basic", "query_current_session", new JObject
                {
                    ["query"] = "animation_transitions", ["character"] = CharacterName, ["animation"] = generatedAnimation
                });
            }
            Invoke("basic", "query_current_session", new JObject
            {
                ["query"] = "character_constraints", ["character"] = CharacterName
            });

            JObject analysis = Invoke("basic", "kimodo_analyze", !string.IsNullOrWhiteSpace(generatedAnimation)
                ? new JObject { ["character"] = CharacterName, ["animation"] = generatedAnimation }
                : new JObject { ["character"] = CharacterName, ["start_frame"] = 0, ["end_frame"] = 2 });
            if (analysis.Value<bool?>("ok") == true && !string.IsNullOrWhiteSpace(analysis.Value<string>("analysis_id")))
            {
                JObject analysisPicture = Invoke("basic-analysis", "query_picture", new JObject
                {
                    ["analysis_id"] = analysis.Value<string>("analysis_id"), ["resolution"] = 256
                });
                RecordFileFromResponse("analysis_picture", analysisPicture, "image_path");
            }

            JObject bake = Invoke("basic", "kimodo_bake_range", new JObject
            {
                ["character"] = CharacterName,
                ["start_frame"] = 0,
                ["end_frame"] = 2,
                ["speed"] = 1.0,
                ["remove_root_motion"] = false,
                ["name"] = BakedName,
                ["output_folder"] = "Assets/KimodoVerificationGenerated"
            });
            RecordAssetMatches();

            JObject close = Invoke("basic", "session_close", new JObject());
            Invoke("error", "session_close", new JObject());
            Record("workflow", "complete", null, null, new JObject
            {
                ["initial_help_ok"] = initialHelp.Value<bool?>("ok"),
                ["session_open_ok"] = open.Value<bool?>("ok"),
                ["character_add_ok"] = add.Value<bool?>("ok"),
                ["generation_terminal"] = terminal?.Value<string>("status") ?? "not_accepted",
                ["bake_ok"] = bake.Value<bool?>("ok"),
                ["session_close_ok"] = close.Value<bool?>("ok"),
                ["animations_response_ok"] = animations.Value<bool?>("ok")
            });
        }
        catch (Exception exception)
        {
            exitCode = 1;
            try
            {
                Record("harness", "unhandled_exception", null, exception.ToString(), new JObject
                {
                    ["message"] = exception.Message,
                    ["type"] = exception.GetType().FullName
                });
            }
            catch { }
            Debug.LogException(exception);
        }
        finally
        {
            AssetDatabase.SaveAssets();
            EditorApplication.Exit(exitCode);
        }
    }

    private static async Task<JObject> PollGeneration(string requestId, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        JObject latest = null;
        while (DateTime.UtcNow < deadline)
        {
            latest = Invoke("poll", "kimodo_get_generation", new JObject { ["request_id"] = requestId });
            string status = latest.Value<string>("status")?.ToLowerInvariant();
            if (status == "completed" || status == "failed" || status == "canceled")
                return latest;
            await Task.Delay(2000);
        }

        Invoke("timeout-cancel", "kimodo_cancel_generation", new JObject
        {
            ["request_id"] = requestId, ["reason"] = "verification timeout after 15 minutes"
        });
        for (int i = 0; i < 30; i++)
        {
            latest = Invoke("poll-after-cancel", "kimodo_get_generation", new JObject { ["request_id"] = requestId });
            string status = latest.Value<string>("status")?.ToLowerInvariant();
            if (status == "completed" || status == "failed" || status == "canceled")
                return latest;
            await Task.Delay(1000);
        }
        return latest ?? new JObject { ["ok"] = false, ["error"] = "No terminal status after timeout cancellation." };
    }

    private static JObject Invoke(string label, string command, JObject arguments) =>
        InvokeRaw(label, command, (arguments ?? new JObject()).ToString(Formatting.None));

    private static JObject InvokeRaw(string label, string command, string argumentsJson)
    {
        string raw = command_dispatcher.Invoke(command, argumentsJson);
        JObject parsed = Parse(raw);
        Record("invoke", label, command, argumentsJson, raw, parsed);
        return parsed;
    }

    private static void CreateEmptyVerificationScene()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, "Assets/KimodoVerificationScene.unity");
        Record("setup", "empty_scene", null, null, new JObject
        {
            ["path"] = scene.path,
            ["saved"] = File.Exists(Path.GetFullPath(scene.path))
        });
    }

    private static void CreateSceneCharacter()
    {
        string path = $"Packages/{PackageName}/Editor/Model/T-Pose.fbx";
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (model == null) throw new InvalidOperationException($"Humanoid model was not found at {path}.");
        GameObject instance = PrefabUtility.InstantiatePrefab(model) as GameObject ?? UnityEngine.Object.Instantiate(model);
        instance.name = CharacterName;
        Animator animator = instance.GetComponentInChildren<Animator>(true);
        if (animator == null) throw new InvalidOperationException("Instantiated verification character has no Animator.");
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
        Record("setup", "character", null, null, new JObject
        {
            ["asset_path"] = path,
            ["name"] = instance.name,
            ["avatar_present"] = animator.avatar != null,
            ["avatar_valid"] = animator.avatar != null && animator.avatar.isValid,
            ["avatar_human"] = animator.avatar != null && animator.avatar.isHuman
        });
    }

    private static void RecordPackageResolution()
    {
        UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
            .FirstOrDefault(item => string.Equals(item.name, PackageName, StringComparison.Ordinal));
        Record("metadata", "package", null, null, new JObject
        {
            ["name"] = package?.name ?? string.Empty,
            ["version"] = package?.version ?? string.Empty,
            ["source"] = package?.source.ToString() ?? string.Empty,
            ["resolved_path"] = package?.resolvedPath ?? string.Empty,
            ["asset_path"] = package?.assetPath ?? string.Empty
        });
    }

    private static void RecordAssetMatches()
    {
        var matches = new JArray();
        foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { "Assets/KimodoVerificationGenerated" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            FileInfo file = new FileInfo(Path.GetFullPath(path));
            matches.Add(new JObject
            {
                ["guid"] = guid,
                ["path"] = path,
                ["exists"] = file.Exists,
                ["bytes"] = file.Exists ? file.Length : 0
            });
        }
        Record("artifact", "animation_assets", null, null, new JObject { ["assets"] = matches });
    }

    private static void RecordFileFromResponse(string label, JObject response, string field)
    {
        string path = response?.Value<string>(field);
        if (string.IsNullOrWhiteSpace(path)) return;
        FileInfo file = new FileInfo(path);
        Record("artifact", label, null, null, new JObject
        {
            ["path"] = path,
            ["exists"] = file.Exists,
            ["bytes"] = file.Exists ? file.Length : 0
        });
    }

    private static JObject Locator(string source, int frame) =>
        new JObject { ["source"] = source, ["frame"] = frame };

    private static JObject Parse(string raw)
    {
        try { return JObject.Parse(raw ?? "{}"); }
        catch { return new JObject { ["parse_error"] = true, ["raw"] = raw ?? string.Empty }; }
    }

    private static void Record(string kind, string label, string command, string arguments, JObject response) =>
        Record(kind, label, command, arguments, null, response);

    private static void Record(string kind, string label, string command, string arguments, string raw, JObject response)
    {
        var entry = new JObject
        {
            ["sequence"] = ++sequence,
            ["timestamp_utc"] = DateTime.UtcNow.ToString("O"),
            ["kind"] = kind,
            ["label"] = label
        };
        if (!string.IsNullOrWhiteSpace(command)) entry["command"] = command;
        if (arguments != null) entry["arguments_json"] = arguments;
        if (raw != null) entry["response_raw"] = raw;
        if (response != null) entry["response"] = response.DeepClone();
        File.AppendAllText(eventsPath, entry.ToString(Formatting.None) + Environment.NewLine);
        Debug.Log($"[KimodoUnity65Verification] {entry.ToString(Formatting.None)}");
    }

    private static string CommandLineValue(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return string.Empty;
    }
}
