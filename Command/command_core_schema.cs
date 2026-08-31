using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KimodoUnityBridge;
using KimodoBridge;
using KimodoBridge.Editor;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;

namespace KimodoUnityBridge.Command

{
    internal static partial class command_context
    {
        private static CommandCatalog BuildCommandCatalog()
        {
            JObject document = JObject.Parse(GetCommandDefinitionsJson());
            var handlers = new Dictionary<string, Func<string, string>>(StringComparer.Ordinal)
            {
                [HelpCommand] = GetCommandHelp,
                [InstallServerCommand] = InstallServer,
                [SessionGetOrCreateCommand] = SessionGetOrCreate,
                [SessionGetRawCommand] = SessionGetRaw,
                [SessionCloseCommand] = SessionClose,
                [SessionAddCommand] = SessionAdd,
                [AnimationAnalyzeCommand] = AnimationAnalyze,
                [AnimationCompareCommand] = AnimationCompare,
                [RecordRangeCommand] = RecordRange,
                [RetargetAnimationCommand] = RetargetAnimation,
                [GenerateAnimationCommand] = GenerateAnimationAsset,
                [PoseGetCommand] = PoseGet,
                [PoseContractCommand] = PoseContract,
                [PoseSetRootTransformCommand] = PoseSetRootTransform,
                [PoseSetMuscleCommand] = PoseSetMuscle,
                [GetGenerationCommand] = GetGeneration,
                [CancelGenerationCommand] = CancelGeneration
            };

            var registrations = new List<CommandRegistration>();
            foreach (JObject definition in document["tools"]?.Values<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string name = definition.Value<string>("name");
                if (string.IsNullOrWhiteSpace(name) || !handlers.TryGetValue(name, out Func<string, string> handler))
                {
                    throw new InvalidOperationException($"Command definition '{name ?? string.Empty}' has no handler.");
                }
                registrations.Add(new CommandRegistration(definition, handler));
            }

            if (registrations.Count != handlers.Count)
            {
                throw new InvalidOperationException("Command definitions and handlers are out of sync.");
            }
            return new CommandCatalog(registrations);
        }

        [MenuItem("Kimodo/Command/Export Help JSON")]
        public static void ExportCommandDefinitionsJson()
        {
            UnityEditor.PackageManager.PackageInfo package =
                UnityEditor.PackageManager.PackageInfo.FindForAssetPath(HelpAssetPath);
            if (package == null || string.IsNullOrWhiteSpace(package.resolvedPath))
            {
                throw new InvalidOperationException($"Kimodo package was not found for '{HelpAssetPath}'.");
            }

            File.WriteAllText(
                Path.Combine(package.resolvedPath, "Command", "help.json"),
                JObject.Parse(BuildCommandDefinitionsJson()).ToString(Formatting.Indented));
            AssetDatabase.Refresh();
        }

        private static JObject CommandDefinition(string name, string description, JObject inputSchema)
        {
            return new JObject
            {
                ["name"] = name,
                ["description"] = description,
                ["inputSchema"] = inputSchema
            };
        }

        private static JObject Properties(params PropertyDefinition[] definitions)
        {
            var properties = new JObject();
            var required = new JArray();
            foreach (PropertyDefinition definition in definitions)
            {
                properties[definition.Name] = definition.Schema;
                if (definition.IsRequired)
                {
                    required.Add(definition.Name);
                }
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false
            };
        }

        private static PropertyDefinition Required(string name, string type, string description)
        {
            return new PropertyDefinition(name, type, description, true);
        }

        private static PropertyDefinition Optional(string name, string type, string description)
        {
            return new PropertyDefinition(name, type, description, false);
        }

        private static PropertyDefinition OptionalArray(string name, string itemType, string description)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = itemType },
                ["description"] = description
            }, false);
        }

        private static PropertyDefinition RequiredArray(string name, string itemType, string description)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = itemType },
                ["description"] = description
            }, true);
        }

        private static PropertyDefinition RequiredAnalysisClips()
        {
            return new PropertyDefinition("clips", new JObject
            {
                ["type"] = "array",
                ["description"] = "One or two immutable Session clip references. Every item explicitly names its Session character; role defaults to source for the first item and target for the second.",
                ["minItems"] = 1,
                ["maxItems"] = 2,
                ["items"] = new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JObject
                    {
                        ["role"] = new JObject { ["type"] = "string", ["enum"] = new JArray("source", "target") },
                        ["character"] = new JObject { ["type"] = "string" },
                        ["clip"] = new JObject { ["type"] = "string" }
                    },
                    ["required"] = new JArray("character", "clip")
                }
            }, true);
        }

        private static PropertyDefinition OptionalEnumArray(string name, string description, params string[] values)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray(values)
                },
                ["description"] = description
            }, false);
        }

        private static PropertyDefinition RequiredEnumArray(string name, string description, params string[] values)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray(values)
                },
                ["description"] = description
            }, true);
        }

        private static PropertyDefinition OptionalConstraints(string name, string description)
        {
            JObject poseReference = PoseReferenceSchema();
            var vector2 = new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = "number" },
                ["minItems"] = 2,
                ["maxItems"] = 2
            };
            var fullBody = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject { ["pose"] = poseReference.DeepClone() },
                ["required"] = new JArray("pose")
            };
            var root2D = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["pose"] = poseReference.DeepClone(),
                    ["position"] = vector2.DeepClone(),
                    ["heading"] = vector2.DeepClone()
                },
                ["anyOf"] = new JArray(
                    new JObject { ["required"] = new JArray("pose") },
                    new JObject { ["required"] = new JArray("position", "heading") })
            };
            var endEffector = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject { ["pose"] = poseReference.DeepClone() },
                ["required"] = new JArray("pose")
            };
            var sparseItem = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["frame"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Relative frame in the generated clip at 60 FPS." },
                    ["fullbody"] = fullBody,
                    ["root2d"] = root2D,
                    ["left_hand"] = endEffector.DeepClone(),
                    ["right_hand"] = endEffector.DeepClone(),
                    ["left_foot"] = endEffector.DeepClone(),
                    ["right_foot"] = endEffector.DeepClone()
                },
                ["required"] = new JArray("frame"),
                ["anyOf"] = new JArray(
                    new JObject { ["required"] = new JArray("fullbody") },
                    new JObject { ["required"] = new JArray("root2d") },
                    new JObject { ["required"] = new JArray("left_hand") },
                    new JObject { ["required"] = new JArray("right_hand") },
                    new JObject { ["required"] = new JArray("left_foot") },
                    new JObject { ["required"] = new JArray("right_foot") })
            };
            var rootPathItem = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["frame"] = new JObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["default"] = 0,
                        ["description"] = "First path frame; defaults to the clip start."
                    },
                    ["root_path"] = new JObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new JObject
                        {
                            ["path"] = poseReference.DeepClone()
                        },
                        ["required"] = new JArray("path")
                    }
                },
                ["required"] = new JArray("root_path")
            };
            ((JObject)rootPathItem["properties"]?["root_path"]?["properties"]?["path"])["description"] =
                "Analyzed Root Path slot returned by animation_analyze at clips[].root_trajectory.path.";
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["description"] = description,
                ["items"] = new JObject { ["oneOf"] = new JArray(sparseItem, rootPathItem) }
            }, false);
        }

        private static PropertyDefinition RequiredPoseSource(string name)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["character"] = new JObject { ["type"] = "string" },
                    ["clip"] = new JObject { ["type"] = "string" },
                    ["frame"] = new JObject { ["type"] = "integer", ["minimum"] = 0 }
                },
                ["required"] = new JArray("character", "clip", "frame")
            }, true);
        }

        private static PropertyDefinition RequiredPoseReference(string name)
        {
            return new PropertyDefinition(name, PoseReferenceSchema(), true);
        }

        private static JObject PoseReferenceSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = "External Pose slot in the current Session.",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["track"] = new JObject { ["type"] = "string" },
                    ["index"] = new JObject { ["type"] = "integer", ["minimum"] = 0 }
                },
                ["required"] = new JArray("track", "index")
            };
        }

        private static PropertyDefinition RequiredSamples(string name)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["items"] = new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JObject
                    {
                        ["character"] = new JObject { ["type"] = "string" },
                        ["time"] = new JObject { ["type"] = "number" }
                    },
                    ["required"] = new JArray("character", "time")
                }
            }, true);
        }

        private static PropertyDefinition Enum(string name, params string[] values)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray(values),
                ["default"] = values[0]
            }, false);
        }

        private static PropertyDefinition OptionalEnumWithDefault(string name, string defaultValue, params string[] values)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray(values),
                ["default"] = defaultValue
            }, false);
        }

        private static PropertyDefinition RequiredEnum(string name, params string[] values)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray(values)
            }, true);
        }

        private sealed class CommandCatalog
        {
            private readonly IReadOnlyList<CommandRegistration> registrations;
            private readonly Dictionary<string, CommandRegistration> byName;

            public CommandCatalog(IEnumerable<CommandRegistration> registrations)
            {
                this.registrations = registrations.ToList();
                byName = new Dictionary<string, CommandRegistration>(StringComparer.Ordinal);
                foreach (CommandRegistration registration in this.registrations)
                {
                    if (byName.ContainsKey(registration.Name))
                    {
                        throw new InvalidOperationException($"Duplicate Kimodo command '{registration.Name}'.");
                    }
                    byName.Add(registration.Name, registration);
                }
            }

            public bool TryGet(string name, out CommandRegistration registration)
            {
                registration = null;
                return name != null && byName.TryGetValue(name, out registration);
            }

            public string ToJson()
            {
                var tools = new JArray();
                foreach (CommandRegistration registration in registrations)
                {
                    tools.Add(registration.ToJson());
                }
                return new JObject { ["tools"] = tools }.ToString(Formatting.None);
            }
        }

        private sealed class CommandRegistration
        {
            private readonly JObject definition;

            public CommandRegistration(JObject definition, Func<string, string> handler)
            {
                this.definition = (JObject)definition.DeepClone();
                Handler = handler ?? throw new ArgumentNullException(nameof(handler));
                Name = this.definition.Value<string>("name");
            }

            public string Name { get; }
            public Func<string, string> Handler { get; }

            public JObject ToJson()
            {
                return (JObject)definition.DeepClone();
            }
        }

        private sealed class JobRecord
        {
            public JobRecord(
                UnityEngine.Object target,
                KimodoEditorGenerationJobSession session,
                TimelineGenerationTrace timelineGenerationTrace)
            {
                Target = target;
                Session = session;
                TimelineGenerationTrace = timelineGenerationTrace;
            }

            public UnityEngine.Object Target { get; }
            public KimodoEditorGenerationJobSession Session { get; }
            public TimelineGenerationTrace TimelineGenerationTrace { get; }
        }

        private sealed class CommandException : InvalidOperationException
        {
            public CommandException(string code, string message) : base(message)
            {
                Code = string.IsNullOrWhiteSpace(code) ? "invalid_argument" : code;
            }

            public string Code { get; }
        }

        private sealed class GenerationRangeLockedException : InvalidOperationException
        {
            public GenerationRangeLockedException(
                string command,
                Guid requestId,
                string character,
                string track,
                int lockedStartFrame,
                int lockedEndFrame,
                int requestedStartFrame,
                int requestedEndFrame)
                : base($"{command} cannot access [{requestedStartFrame},{requestedEndFrame}) on '{track}' while generation {requestId:D} locks [{lockedStartFrame},{lockedEndFrame}).")
            {
                Command = command;
                RequestId = requestId;
                Character = character;
                Track = track;
                LockedStartFrame = lockedStartFrame;
                LockedEndFrame = lockedEndFrame;
                RequestedStartFrame = requestedStartFrame;
                RequestedEndFrame = requestedEndFrame;
            }

            public string Command { get; }
            public Guid RequestId { get; }
            public string Character { get; }
            public string Track { get; }
            public int LockedStartFrame { get; }
            public int LockedEndFrame { get; }
            public int RequestedStartFrame { get; }
            public int RequestedEndFrame { get; }
        }

        private readonly struct ResolvedCharacter
        {
            public ResolvedCharacter(GameObject root, Animator animator, Avatar avatar, string name)
            {
                Root = root;
                Animator = animator;
                Avatar = avatar;
                Name = name;
            }

            public GameObject Root { get; }
            public Animator Animator { get; }
            public Avatar Avatar { get; }
            public UnityEngine.Object Target => Root;
            public string Name { get; }
        }

        private readonly struct PropertyDefinition
        {
            public PropertyDefinition(string name, string type, string description, bool required)
                : this(name, new JObject { ["type"] = type, ["description"] = description }, required)
            {
            }

            public PropertyDefinition(string name, JObject schema, bool required)
            {
                Name = name;
                Schema = schema;
                IsRequired = required;
            }

            public string Name { get; }
            public JObject Schema { get; }
            public bool IsRequired { get; }
        }
    }
}
