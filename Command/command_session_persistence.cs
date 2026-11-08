using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using KimodoBridge;
using KimodoBridge.Editor;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        internal const string SessionHistoryFormatVersion = "3.0";
        internal const string PackageVersion = "3.0.0";
        // Session state and analysis evidence are runtime data, not Unity assets.
        // Keeping them below Library prevents every JSON/PNG write from entering
        // the AssetDatabase import pipeline.
        private const string GeneratedSessionsFolder = "Library/KimodoData/Sessions";

        private static void PersistSessionArtifacts(TimelineSessionRecord session)
        {
            if (session?.TimelineAsset == null) return;
            string sessionFolder = GetSessionGeneratedFolder(session);
            string analysisFolder = Path.Combine(sessionFolder, "Analyses");
            string motionFolder = Path.Combine(sessionFolder, "Motion");
            Directory.CreateDirectory(sessionFolder);
            Directory.CreateDirectory(analysisFolder);
            Directory.CreateDirectory(motionFolder);
            foreach (TimelineCharacterRecord character in session.Characters)
            foreach (TimelineAnimationRecord animation in character.Animations)
            {
                if (animation.Analysis != null)
                {
                    string absolute = Path.Combine(analysisFolder, $"animation_{animation.Id:D}_analysis.json");
                    File.WriteAllText(absolute, animation.Analysis.ToString());
                }
                if (animation.KmbBytes != null && animation.KmbBytes.Length > 0)
                {
                    string absolute = Path.Combine(motionFolder, $"motion_{animation.Id:D}.kmb");
                    File.WriteAllBytes(absolute, animation.KmbBytes);
                }
            }
        }

        private static string GetSessionGeneratedFolder(TimelineSessionRecord session)
        {
            if (session == null)
            {
                throw new InvalidOperationException("Session is required to resolve its generated folder.");
            }
            return Path.Combine(Directory.GetCurrentDirectory(), GeneratedSessionsFolder, session.Id.ToString("D"));
        }

        internal static string GetSessionHistoryAbsolutePath(TimelineSessionRecord session)
        {
            if (session == null) throw new InvalidOperationException("Session is required to resolve history.");
            return Path.Combine(GetSessionGeneratedFolder(session), "history.jsonl");
        }

        private static string ToProjectRelativePath(string absolutePath)
        {
            string root = Directory.GetCurrentDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string normalizedRoot = root.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string normalizedPath = Path.GetFullPath(absolutePath).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? normalizedPath.Substring(normalizedRoot.Length).Replace('\\', '/')
                : normalizedPath.Replace('\\', '/');
        }

        private static JObject SampleResultJson(KimodoMarkerSampleResult sample)
        {
            sample ??= new KimodoMarkerSampleResult();
            return new JObject
            {
                ["sample_data"] = new JArray(sample.sampleData?.data ?? Array.Empty<float>()),
                ["sample_valid"] = sample.sampleValid,
                ["enable_mask"] = ConstraintMaskJson(sample.enableMask),
                ["effectors"] = new JObject
                {
                    ["left_hand"] = RigidTransformJson(sample.effectors?.leftHand),
                    ["right_hand"] = RigidTransformJson(sample.effectors?.rightHand),
                    ["left_foot"] = RigidTransformJson(sample.effectors?.leftFoot),
                    ["right_foot"] = RigidTransformJson(sample.effectors?.rightFoot)
                },
                ["root_override"] = RigidTransformJson(sample.rootOverride),
                ["enabled"] = sample.enabled,
                ["creation_order"] = sample.creationOrder,
                ["constraint_mode"] = sample.constraintMode ?? string.Empty,
                ["sample_time"] = sample.sampleTime
            };
        }

        private static JObject ConstraintMaskJson(KimodoConstraintMask mask) => new JObject
        {
            ["root_heading"] = mask?.rootHeading == true,
            ["left_foot"] = mask?.leftFoot == true,
            ["right_foot"] = mask?.rightFoot == true,
            ["left_hand"] = mask?.leftHand == true,
            ["right_hand"] = mask?.rightHand == true
        };

        private static JObject RigidTransformJson(KimodoRigidTransform transform)
        {
            transform ??= KimodoRigidTransform.Identity;
            return new JObject
            {
                ["position"] = new JArray(transform.t.x, transform.t.y, transform.t.z),
                ["rotation"] = new JArray(transform.q.x, transform.q.y, transform.q.z, transform.q.w)
            };
        }

        internal static void WriteJsonAtomically(string path, JObject json)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A JSON output path is required.", nameof(path));
            string tempPath = path + ".tmp_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(tempPath, (json ?? new JObject()).ToString(Formatting.Indented));
            try
            {
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tempPath, path, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(tempPath, path, overwrite: true);
                        File.Delete(tempPath);
                    }
                    catch (IOException)
                    {
                        File.Copy(tempPath, path, overwrite: true);
                        File.Delete(tempPath);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

    }
}
