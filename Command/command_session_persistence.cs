using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KimodoBridge;
using KimodoBridge.Editor;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private const string SessionJsonSchemaVersion = "vNext.transition_clip.1";
        // Session state and analysis evidence are runtime data, not Unity assets.
        // Keeping them below Library prevents every JSON/PNG write from entering
        // the AssetDatabase import pipeline.
        private const string GeneratedSessionsFolder = "Library/KimodoData/Sessions";

        private static void PersistTimelineSessionJson(TimelineSessionRecord session)
        {
            if (session?.TimelineAsset == null) return;
            string sessionFolder = GetSessionGeneratedFolder(session);
            string analysisFolder = Path.Combine(sessionFolder, "Analyses");
            string motionFolder = Path.Combine(sessionFolder, "Motion");
            Directory.CreateDirectory(sessionFolder);
            Directory.CreateDirectory(analysisFolder);
            Directory.CreateDirectory(motionFolder);
            KimodoSessionContextBinding binding = session.SessionRoot != null
                ? session.SessionRoot.GetComponent<KimodoSessionContextBinding>()
                : null;
            session.SessionRevision = Math.Max(Math.Max(0, session.SessionRevision), binding?.sessionRevision ?? 0) + 1;
            if (binding != null)
            {
                binding.sessionRevision = session.SessionRevision;
                binding.sessionJsonPath = session.SessionJsonPath;
                binding.timelineAssetPath = session.TimelineAssetPath ?? string.Empty;
                EditorUtility.SetDirty(binding);
                if (session.SessionRoot.scene.IsValid()) EditorSceneManager.MarkSceneDirty(session.SessionRoot.scene);
            }

            var animationPaths = new Dictionary<Guid, string[]>();
            foreach (TimelineCharacterRecord character in session.Characters)
            foreach (TimelineAnimationRecord animation in character.Animations)
            {
                string analysisPath = string.Empty;
                string motionPath = string.Empty;
                if (animation.Analysis != null)
                {
                    string absolute = Path.Combine(analysisFolder, $"animation_{animation.Id:D}_analysis.json");
                    File.WriteAllText(absolute, animation.Analysis.ToString());
                    analysisPath = ToProjectRelativePath(absolute);
                }
                if (animation.KmbBytes != null && animation.KmbBytes.Length > 0)
                {
                    string absolute = Path.Combine(motionFolder, $"motion_{animation.Id:D}.kmb");
                    File.WriteAllBytes(absolute, animation.KmbBytes);
                    motionPath = ToProjectRelativePath(absolute);
                }
                animationPaths[animation.Id] = new[] { analysisPath, motionPath };
            }

            JObject json = new JObject
            {
                ["schema_version"] = SessionJsonSchemaVersion,
                ["session_id"] = session.Id.ToString("D"),
                ["name"] = session.Name,
                ["scene_path"] = session.SessionRoot != null && session.SessionRoot.scene.IsValid()
                    ? session.SessionRoot.scene.path
                    : string.Empty,
                ["session_revision"] = session.SessionRevision,
                ["session_json_path"] = session.SessionJsonPath,
                ["timeline_asset_path"] = session.TimelineAssetPath ?? string.Empty,
                ["characters"] = new JArray(),
                ["poses"] = new JArray()
            };
            JArray characters = (JArray)json["characters"];
            JArray poses = (JArray)json["poses"];
            foreach (TimelineCharacterRecord character in session.Characters)
            {
                var characterJson = new JObject
                {
                    ["character_ref"] = character.CharacterRef ?? string.Empty,
                    ["name"] = character.Name,
                    ["track_name"] = character.Track != null ? character.Track.name : string.Empty,
                    ["pose_cache_track_name"] = character.PoseCacheTrack != null ? character.PoseCacheTrack.name : string.Empty,
                    ["animations"] = new JArray(),
                    ["constraints"] = new JArray()
                };
                foreach (TimelineAnimationRecord animation in character.Animations)
                {
                    JObject animationJson = DescribeAnimation(animation);
                    animationJson["animation_id"] = animation.Id.ToString("D");
                    animationJson["source"] = animation.Source;
                    string[] paths = animationPaths[animation.Id];
                    animationJson["analysis_path"] = paths[0];
                    animationJson["motion_path"] = paths[1];
                    animationJson["analysis_start_frame"] = animation.StartFrame;
                    animationJson["analysis_end_frame_exclusive"] = animation.EndFrameExclusive;
                    animationJson["kind"] = animation.Kind;
                    if (animation.Transition != null) animationJson["transition"] = animation.Transition.DeepClone();
                    ((JArray)characterJson["animations"]).Add(animationJson);
                }
                if (character.Track != null)
                {
                    foreach (KimodoConstraintMarker marker in character.Track.GetMarkers().OfType<KimodoConstraintMarker>()
                        .Where(item => item.constraintEnabled && item.ParticipatesInGeneration))
                    {
                        ((JArray)characterJson["constraints"]).Add(DescribeTimelineConstraint(marker, 0));
                    }
                }
                if (character.PoseCacheTrack != null)
                {
                    foreach (KimodoConstraintMarker marker in character.PoseCacheTrack.GetMarkers().OfType<KimodoConstraintMarker>())
                    {
                        var markerJson = new JObject
                        {
                            ["character"] = character.Name,
                            ["track"] = character.PoseCacheTrack.name,
                            ["index"] = Mathf.RoundToInt((float)(marker.time * SessionFrameRate)),
                            ["marker_type"] = marker.ConstraintType
                        };
                        if (marker.IsExternalPath) markerJson["path_data"] = BuildPathJson(marker.PathData);
                        else markerJson["sample_result"] = SampleResultJson(marker.SampleData);
                        poses.Add(markerJson);
                    }
                }
                characters.Add(characterJson);
            }
            WriteJsonAtomically(GetSessionJsonAbsolutePath(session), json);
        }

        private static string GetSessionGeneratedFolder(TimelineSessionRecord session)
        {
            if (session == null)
            {
                throw new InvalidOperationException("Session is required to resolve its generated folder.");
            }
            return Path.GetDirectoryName(GetSessionJsonAbsolutePath(session));
        }

        private static string GetSessionJsonAbsolutePath(TimelineSessionRecord session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.SessionJsonPath))
                throw new InvalidOperationException("The scene context has no bound session JSON path.");
            return Path.GetFullPath(Path.IsPathRooted(session.SessionJsonPath)
                ? session.SessionJsonPath
                : Path.Combine(Directory.GetCurrentDirectory(), session.SessionJsonPath));
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

        private static JArray GetAnalysisIndex(TimelineSessionRecord session)
        {
            string folder = Path.Combine(GetSessionGeneratedFolder(session), "Analyses");
            if (!Directory.Exists(folder)) return new JArray();
            var records = new JArray();
            foreach (string path in Directory.GetFiles(folder, "analysis_*.json").OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    JObject record = JObject.Parse(File.ReadAllText(path));
                    JArray picturePaths = new JArray();
                    string imagePath = record["pictures"]?.Value<string>("image_path");
                    if (!string.IsNullOrWhiteSpace(imagePath)) picturePaths.Add(imagePath);
                    foreach (string tilePath in (record["pictures"]?["tile_paths"] as JArray ?? new JArray()).Values<string>())
                    {
                        if (!string.IsNullOrWhiteSpace(tilePath) && !picturePaths.Values<string>().Contains(tilePath, StringComparer.OrdinalIgnoreCase))
                            picturePaths.Add(tilePath);
                    }
                    foreach (JObject picture in (record["pictures"]?["images"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        string tilePath = picture.Value<string>("image_path");
                        if (!string.IsNullOrWhiteSpace(tilePath) && !picturePaths.Values<string>().Contains(tilePath, StringComparer.OrdinalIgnoreCase))
                            picturePaths.Add(tilePath);
                    }
                    records.Add(new JObject
                    {
                        ["analysis_id"] = record.Value<string>("analysis_id") ?? string.Empty,
                        ["session_id"] = record.Value<string>("session_id") ?? string.Empty,
                        ["character"] = record.Value<string>("character") ?? string.Empty,
                        ["animation_id"] = record.Value<string>("animation_id") ?? string.Empty,
                        ["animation_name"] = record.Value<string>("animation_name") ?? string.Empty,
                        ["input_signature"] = record.Value<string>("input_signature") ?? string.Empty,
                        ["created_at_utc"] = record.Value<string>("created_at_utc") ?? string.Empty,
                        ["analysis_path"] = ToProjectRelativePath(path),
                        ["picture_paths"] = picturePaths
                    });
                }
                catch (Exception exception)
                {
                    records.Add(new JObject
                    {
                        ["analysis_path"] = ToProjectRelativePath(path),
                        ["error"] = exception.Message
                    });
                }
            }
            return records;
        }

        private static JArray GetGenerationIndex(TimelineSessionRecord session)
        {
            string folder = Path.Combine(GetSessionGeneratedFolder(session), "Generations");
            if (!Directory.Exists(folder)) return new JArray();
            var records = new JArray();
            foreach (string path in Directory.GetFiles(folder, "generation_*.json")
                         .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    JObject status = JObject.Parse(File.ReadAllText(path));
                    JObject clip = status["clip"] as JObject;
                    records.Add(DescribeGeneration(status, clip));
                }
                catch
                {
                    // History is an index over complete generation records; malformed files are not entries.
                }
            }
            return records;
        }

        private static JObject DescribeGeneration(JObject status, JObject clip) => new JObject
        {
            ["request_id"] = status.Value<string>("request_id") ?? string.Empty,
            ["animation_id"] = status.Value<string>("animation_id") ?? string.Empty,
            ["character"] = clip?.Value<string>("character") ?? status.Value<string>("character") ?? string.Empty,
            ["animation"] = clip?.Value<string>("name") ?? status.Value<string>("animation") ?? string.Empty,
            ["status"] = status.Value<string>("status") ?? string.Empty,
            ["stage"] = status.Value<string>("stage") ?? string.Empty,
            ["message"] = status.Value<string>("message") ?? string.Empty,
            ["error"] = status.Value<string>("error") ?? string.Empty,
            ["started_at_utc"] = status.Value<string>("started_at_utc") ?? string.Empty,
            ["updated_at_utc"] = status.Value<string>("updated_at_utc") ?? string.Empty
        };

    }
}
