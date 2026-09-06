using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using KimodoUnityBridge;
using KimodoBridge;
using TimelineInject;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace KimodoUnityBridge.Command

{
    internal static partial class command_context
    {
        private static void CreatePictureEnvironment(List<GameObject> objects, Bounds bounds)
        {
            const int captureLayer = SessionCaptureLayer;
            float size = Mathf.Ceil(Mathf.Max(bounds.size.x, bounds.size.z) * .5f) * 2f;
            GameObject floor = MoveToAnalysisSessionRoot(GameObject.CreatePrimitive(PrimitiveType.Plane));
            floor.hideFlags = HideFlags.HideAndDontSave;
            floor.transform.position = new Vector3(bounds.center.x, 0f, bounds.center.z);
            floor.transform.localScale = Vector3.one * (size / 10f);
            SetLayerRecursively(floor, captureLayer);
            floor.GetComponent<Renderer>().sharedMaterial = MakeMaterial(new Color(.31f, .31f, .31f, 1f));
            objects.Add(floor);
            for (float x = bounds.min.x; x <= bounds.max.x; x += .25f)
            {
                CreateWorldLine(objects, new Vector3(x, .006f, bounds.min.z), new Vector3(x, .006f, bounds.max.z),
                    Mathf.Abs(x % 1f) < .01f ? .010f : .003f, new Color(.65f, .65f, .65f, .25f));
            }
            for (float z = bounds.min.z; z <= bounds.max.z; z += .25f)
            {
                CreateWorldLine(objects, new Vector3(bounds.min.x, .006f, z), new Vector3(bounds.max.x, .006f, z),
                    Mathf.Abs(z % 1f) < .01f ? .010f : .003f, new Color(.65f, .65f, .65f, .25f));
            }
            CreateEvidenceLights(objects, bounds.center);
        }

        private static Bounds IncludeGroundInBounds(Bounds bounds)
        {
            bounds.Encapsulate(new Vector3(bounds.min.x, 0f, bounds.min.z));
            bounds.Encapsulate(new Vector3(bounds.max.x, 0f, bounds.max.z));
            bounds.Expand(new Vector3(.5f, .25f, .5f));
            return bounds;
        }

        private static void CreateTestPictureEnvironment(List<GameObject> objects, Bounds bounds)
        {
            const float tileSize = 16f;
            Vector3 center = bounds.center;
            int minX = Mathf.FloorToInt(bounds.min.x / tileSize) - 1;
            int maxX = Mathf.FloorToInt(bounds.max.x / tileSize) + 1;
            int minZ = Mathf.FloorToInt(bounds.min.z / tileSize) - 1;
            int maxZ = Mathf.FloorToInt(bounds.max.z / tileSize) + 1;
            Texture2D gridTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Packages/com.unity.kimodo_unity_motion_tools/Editor/Model/UVCheckGrid.png")
                ?? AssetDatabase.LoadAssetAtPath<Texture2D>("Editor/Model/UVCheckGrid.png");

            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    GameObject floor = CreateTestGridFloor(
                        new Vector3((x + .5f) * tileSize, 0f, (z + .5f) * tileSize), tileSize, gridTexture);
                    objects.Add(floor);
                }
            }
            CreateEvidenceLights(objects, center);
        }

        private static GameObject CreateTestGridFloor(Vector3 center, float size, Texture2D gridTexture)
        {
            const int subdivisions = 16;
            const int captureLayer = SessionCaptureLayer;
            var mesh = new Mesh { name = "Kimodo Test 16x16 UV Grid", hideFlags = HideFlags.HideAndDontSave };
            int vertexSide = subdivisions + 1;
            var vertices = new Vector3[vertexSide * vertexSide];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[subdivisions * subdivisions * 6];
            for (int z = 0; z < vertexSide; z++)
            {
                for (int x = 0; x < vertexSide; x++)
                {
                    int index = z * vertexSide + x;
                    vertices[index] = new Vector3(
                        (x / (float)subdivisions - .5f) * size,
                        0f,
                        (z / (float)subdivisions - .5f) * size);
                    uv[index] = new Vector2(x / (float)subdivisions, z / (float)subdivisions);
                }
            }
            int triangle = 0;
            for (int z = 0; z < subdivisions; z++)
            {
                for (int x = 0; x < subdivisions; x++)
                {
                    int a = z * vertexSide + x;
                    int b = a + 1;
                    int c = a + vertexSide;
                    int d = c + 1;
                    triangles[triangle++] = a;
                    triangles[triangle++] = c;
                    triangles[triangle++] = b;
                    triangles[triangle++] = b;
                    triangles[triangle++] = c;
                    triangles[triangle++] = d;
                }
            }
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();

            GameObject floor = MoveToAnalysisSessionRoot(
                new GameObject("Kimodo Test UV Grid") { hideFlags = HideFlags.HideAndDontSave });
            floor.transform.position = center;
            floor.layer = captureLayer;
            MeshFilter filter = floor.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = floor.AddComponent<MeshRenderer>();
            Material material = MakeMaterial(Color.white);
            if (gridTexture != null)
            {
                if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", gridTexture);
                if (material.HasProperty("_BaseColorMap")) material.SetTexture("_BaseColorMap", gridTexture);
                if (material.HasProperty("_UnlitColorMap")) material.SetTexture("_UnlitColorMap", gridTexture);
                if (material.HasProperty("_MainTex")) material.mainTexture = gridTexture;
            }
            renderer.sharedMaterial = material;
            return floor;
        }

        private static Camera CreateAnalysisPictureCamera(Bounds bounds, Vector3 direction, bool orthographic)
        {
            Camera camera = CreateAnalysisPictureCamera("Kimodo Analysis Picture Camera");
            camera.cullingMask = 1 << SessionCaptureLayer;
            camera.orthographic = orthographic;
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.orthographicSize = Mathf.Max(2.5f, bounds.extents.magnitude * 1.05f);
            camera.fieldOfView = 35f;
            camera.transform.position = bounds.center + direction.normalized * Mathf.Max(7f, bounds.extents.magnitude * 3.2f);
            Vector3 up = Mathf.Abs(Vector3.Dot(direction.normalized, Vector3.up)) > .95f ? Vector3.forward : Vector3.up;
            camera.transform.LookAt(bounds.center + Vector3.up, up);
            return camera;
        }

        private static Camera CreateTestAnalysisPictureCamera(
            Bounds bounds,
            SubjectPictureData subject,
            Vector3 direction,
            float aspect)
        {
            Camera camera = CreateAnalysisPictureCamera("Kimodo Test Analysis Picture Camera");
            camera.cullingMask = 1 << SessionCaptureLayer;
            camera.orthographic = true;
            camera.aspect = Mathf.Max(.1f, aspect);
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 1000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            Vector3 normalizedDirection = direction.sqrMagnitude > .0001f ? direction.normalized : new Vector3(1f, .75f, -1f).normalized;
            CalculateTestViewExtents(
                subject,
                normalizedDirection,
                out Vector3 viewCenter,
                out float maxHorizontal,
                out float maxVertical,
                out float maxDepth);
            float distance = Mathf.Max(8f, bounds.extents.magnitude * 4f);
            camera.transform.position = viewCenter + normalizedDirection * distance;
            Vector3 up = Mathf.Abs(Vector3.Dot(normalizedDirection, Vector3.up)) > .95f ? Vector3.forward : Vector3.up;
            camera.transform.LookAt(viewCenter, up);

            float horizontalHalf = maxHorizontal * TestCameraFitScale + TestCameraMarginMeters;
            float verticalHalf = maxVertical * TestCameraFitScale + TestCameraMarginMeters;
            camera.orthographicSize = Mathf.Max(
                .5f,
                verticalHalf,
                horizontalHalf / camera.aspect);
            camera.farClipPlane = Mathf.Max(100f, distance + maxDepth + 10f);
            return camera;
        }

        private static Camera CreateTestAnalysisPictureCamera(
            Bounds bounds,
            Vector3 direction,
            float aspect)
        {
            Camera camera = CreateAnalysisPictureCamera("Kimodo Test Pose Camera");
            camera.cullingMask = 1 << SessionCaptureLayer;
            camera.orthographic = true;
            camera.aspect = Mathf.Max(.1f, aspect);
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 1000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            Vector3 normalizedDirection = direction.sqrMagnitude > .0001f ? direction.normalized : new Vector3(1f, .75f, -1f).normalized;
            CalculateTestViewExtents(
                bounds,
                normalizedDirection,
                out Vector3 viewCenter,
                out float maxHorizontal,
                out float maxVertical,
                out float maxDepth);
            float distance = Mathf.Max(8f, bounds.extents.magnitude * 4f);
            camera.transform.position = viewCenter + normalizedDirection * distance;
            Vector3 up = Mathf.Abs(Vector3.Dot(normalizedDirection, Vector3.up)) > .95f ? Vector3.forward : Vector3.up;
            camera.transform.LookAt(viewCenter, up);

            float horizontalHalf = maxHorizontal * TestCameraFitScale + TestCameraMarginMeters;
            float verticalHalf = maxVertical * TestCameraFitScale + TestCameraMarginMeters;
            camera.orthographicSize = Mathf.Max(
                .5f,
                verticalHalf,
                horizontalHalf / camera.aspect);
            camera.farClipPlane = Mathf.Max(100f, distance + maxDepth + 10f);
            return camera;
        }

        private static Camera CreateTestAnalysisPictureCamera(
            IEnumerable<Vector3> points,
            Vector3 direction,
            float aspect)
        {
            Camera camera = CreateAnalysisPictureCamera("Kimodo Test Pose Camera");
            camera.cullingMask = 1 << SessionCaptureLayer;
            camera.orthographic = true;
            camera.aspect = Mathf.Max(.0001f, aspect);
            camera.nearClipPlane = .01f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            Vector3 normalizedDirection = direction.sqrMagnitude > .0001f ? direction.normalized : new Vector3(1f, .75f, -1f).normalized;
            CalculateTestViewExtents(points, normalizedDirection, out Vector3 viewCenter, out _, out float vertical, out float maxDepth);
            float distance = Mathf.Max(8f, maxDepth + 8f);
            Vector3 up = Mathf.Abs(Vector3.Dot(normalizedDirection, Vector3.up)) > .95f ? Vector3.forward : Vector3.up;
            camera.transform.position = viewCenter + normalizedDirection * distance;
            camera.transform.LookAt(viewCenter, up);
            camera.orthographicSize = Mathf.Max(.0001f, vertical);
            camera.farClipPlane = Mathf.Max(100f, distance + maxDepth + 10f);
            return camera;
        }

        private static Camera CreateAnalysisPictureCamera(string name)
        {
            GameObject cameraObject = MoveToAnalysisSessionRoot(
                new GameObject(name) { hideFlags = HideFlags.HideAndDontSave });
            Camera camera = cameraObject.AddComponent<Camera>();
            ConfigureRenderPipelineAnalysisCamera(camera);
            return camera;
        }

        private static void ConfigureRenderPipelineAnalysisCamera(Camera camera)
        {
            if (camera == null || GraphicsSettings.currentRenderPipeline == null) return;

            string pipelineName = GraphicsSettings.currentRenderPipeline.GetType().FullName ?? string.Empty;
            string cameraDataTypeName = pipelineName.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) >= 0 ||
                pipelineName.IndexOf("HDRP", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData"
                    : pipelineName.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData"
                        : null;
            if (cameraDataTypeName == null) return;

            Type additionalCameraDataType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(cameraDataTypeName, false))
                .FirstOrDefault(type => type != null);
            if (additionalCameraDataType == null) return;

            Component additionalCameraData = camera.GetComponent(additionalCameraDataType) ??
                camera.gameObject.AddComponent(additionalCameraDataType);
            var volumeLayerMask = additionalCameraDataType.GetField("volumeLayerMask");
            if (volumeLayerMask != null)
            {
                volumeLayerMask.SetValue(additionalCameraData, (LayerMask)0);
            }
            else
            {
                additionalCameraDataType.GetProperty("volumeLayerMask")?.SetValue(additionalCameraData, (LayerMask)0);
            }
            if (pipelineName.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) >= 0 ||
                pipelineName.IndexOf("HDRP", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Type clearColorModeType = additionalCameraDataType.GetNestedType(
                    "ClearColorMode", BindingFlags.Public | BindingFlags.NonPublic);
                if (clearColorModeType != null)
                {
                    object colorMode = System.Enum.Parse(clearColorModeType, "Color");
                    additionalCameraDataType.GetField("clearColorMode")?.SetValue(additionalCameraData, colorMode);
                    additionalCameraDataType.GetProperty("clearColorMode")?.SetValue(additionalCameraData, colorMode);
                }
            }
        }

        private static TrajectoryScale BuildTrajectoryScale(IReadOnlyList<SubjectPictureData> subjects, bool includeEndEffectors = false)
        {
            var speeds = new List<float>();
            var accelerations = new List<float>();
            foreach (SubjectPictureData subject in subjects)
            {
                CollectTrajectoryMeasurements(subject.Pelvis, speeds, accelerations);
                if (!includeEndEffectors) continue;
                CollectTrajectoryMeasurements(subject.LeftHand, speeds, accelerations);
                CollectTrajectoryMeasurements(subject.RightHand, speeds, accelerations);
                CollectTrajectoryMeasurements(subject.LeftFoot, speeds, accelerations);
                CollectTrajectoryMeasurements(subject.RightFoot, speeds, accelerations);
            }
            return new TrajectoryScale(Percentile(speeds, .05f), Percentile(speeds, .95f), Percentile(accelerations, .05f), Percentile(accelerations, .95f));
        }

        private static void CollectTrajectoryMeasurements(Vector3[] points, List<float> speeds, List<float> accelerations)
        {
            float previousSpeed = 0f;
            for (int index = 1; index < points.Length; index++)
            {
                float speed = (points[index] - points[index - 1]).magnitude * (float)SessionFrameRate;
                speeds.Add(speed);
                accelerations.Add(Mathf.Abs(speed - previousSpeed) * (float)SessionFrameRate);
                previousSpeed = speed;
            }
        }

        private static float Percentile(List<float> values, float percent)
        {
            if (values == null || values.Count == 0) return 0f;
            values.Sort();
            return values[Mathf.Clamp(Mathf.RoundToInt((values.Count - 1) * percent), 0, values.Count - 1)];
        }

        private static void CreateTestBodyTrajectories(
            List<GameObject> objects,
            SubjectPictureData subject)
        {
            CreateTestTrajectory(objects, subject.Pelvis, new Color(.1f, .8f, .2f, .9f), .09f);
            CreateTestTrajectory(objects, subject.LeftHand, new Color(.2f, .45f, 1f, .65f), .035f);
            CreateTestTrajectory(objects, subject.LeftFoot, new Color(.2f, .45f, 1f, .8f), .05f);
            CreateTestTrajectory(objects, subject.RightHand, new Color(1f, .2f, .2f, .65f), .035f);
            CreateTestTrajectory(objects, subject.RightFoot, new Color(1f, .2f, .2f, .8f), .05f);
        }

        private static void CreateTestTrajectory(
            List<GameObject> objects,
            Vector3[] points,
            Color color,
            float lineWidth)
        {
            if (points == null || points.Length < 2) return;
            GameObject lineObject = MoveToAnalysisSessionRoot(
                new GameObject("Kimodo Test Body Trajectory") { hideFlags = HideFlags.HideAndDontSave });
            SetLayerRecursively(lineObject, SessionCaptureLayer);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.positionCount = points.Length;
            line.SetPositions(points.Select(point => point + Vector3.up * .02f).ToArray());
            line.startWidth = line.endWidth = lineWidth;
            line.useWorldSpace = true;
            line.sharedMaterial = MakeUnlitMaterial(color);
            line.startColor = line.endColor = color;
            objects.Add(lineObject);
        }

        private static void Fill(Texture2D texture, Color color)
        {
            var pixels = new Color[texture.width * texture.height];
            for (int index = 0; index < pixels.Length; index++) pixels[index] = color;
            texture.SetPixels(pixels);
        }

        private static bool IsHighFootPose(PictureTile tile)
        {
            return tile != null && tile.Presentation == "test_pose" &&
                string.Equals(tile.PoseKind, "foot_transition", StringComparison.Ordinal);
        }

        private static void DrawPictureGrid(
            Texture2D texture,
            IReadOnlyList<RectInt> imageRects,
            int panels,
            int panelHeight,
            int rows)
        {
            foreach (RectInt rect in imageRects)
            {
                if (rect.xMax < texture.width)
                {
                    FillRect(texture, rect.xMax - 2, rect.y, 4, rect.height, Color.white);
                }
            }
            for (int panel = 0; panel < panels; panel++)
            {
                int origin = panel * panelHeight;
                for (int row = 1; row < rows; row++)
                {
                    FillRect(texture, 0, origin + row * (panelHeight / rows) - 2, texture.width, 4, Color.white);
                }
            }
            for (int panel = 1; panel < panels; panel++)
            {
                FillRect(texture, 0, panel * panelHeight - 2, texture.width, 4, Color.white);
            }
        }

        private static void DrawTileNumber(Texture2D texture, string value)
        {
            string text = value ?? string.Empty;
            int size = texture.width >= 256 ? 4 : 2;
            int width = 0;
            foreach (char character in text)
            {
                width += character == '.' ? size * 2 : size * 5;
            }
            width = Math.Max(1, width - size);
            int x = texture.width - width - size * 2;
            int y = texture.height - size * 8;
            foreach (char digit in text)
            {
                if (digit == '.')
                {
                    FillRect(texture, x, y, size, size, Color.white);
                    x += size * 2;
                }
                else
                {
                    DrawSevenSegmentDigit(texture, x, y, digit, size, Color.white);
                    x += size * 5;
                }
            }
            texture.Apply(false, false);
        }

        private static void DrawFrameNumber(Texture2D texture, int frame)
        {
            string text = Math.Max(0, frame).ToString(CultureInfo.InvariantCulture);
            int size = texture.width >= 256 ? 4 : 2;
            int width = 0;
            foreach (char character in text) width += size * 5;
            width = Math.Max(1, width - size);
            int x = size * 2;
            int y = size * 2;
            FillRect(texture, 0, 0, width + size * 4, size * 8, new Color(0f, 0f, 0f, .65f));
            foreach (char digit in text)
            {
                DrawSevenSegmentDigit(texture, x, y, digit, size, Color.white);
                x += size * 5;
            }
            texture.Apply(false, false);
        }

        private static void DrawSevenSegmentDigit(Texture2D texture, int x, int y, char digit, int size, Color color)
        {
            bool[] map = digit switch
            {
                '0' => new[] { true, true, true, true, true, true, false },
                '1' => new[] { false, true, true, false, false, false, false },
                '2' => new[] { true, true, false, true, true, false, true },
                '3' => new[] { true, true, true, true, false, false, true },
                '4' => new[] { false, true, true, false, false, true, true },
                '5' => new[] { true, false, true, true, false, true, true },
                '6' => new[] { true, false, true, true, true, true, true },
                '7' => new[] { true, true, true, false, false, false, false },
                '8' => new[] { true, true, true, true, true, true, true },
                '9' => new[] { true, true, true, true, false, true, true },
                _ => new bool[7]
            };
            int w = size * 3;
            int h = size * 6;
            if (map[0]) FillRect(texture, x + size, y + h - size, w, size, color);
            if (map[1]) FillRect(texture, x + w + size, y + h / 2, size, h / 2, color);
            if (map[2]) FillRect(texture, x + w + size, y, size, h / 2, color);
            if (map[3]) FillRect(texture, x + size, y, w, size, color);
            if (map[4]) FillRect(texture, x, y, size, h / 2, color);
            if (map[5]) FillRect(texture, x, y + h / 2, size, h / 2, color);
            if (map[6]) FillRect(texture, x + size, y + h / 2 - size / 2, w, size, color);
        }

        private static void FillRect(Texture2D texture, int x, int y, int width, int height, Color color)
        {
            int minX = Mathf.Clamp(x, 0, texture.width);
            int maxX = Mathf.Clamp(x + width, 0, texture.width);
            int minY = Mathf.Clamp(y, 0, texture.height);
            int maxY = Mathf.Clamp(y + height, 0, texture.height);
            for (int row = minY; row < maxY; row++)
            {
                for (int column = minX; column < maxX; column++) texture.SetPixel(column, row, color);
            }
        }

        private static string CacheAnalysisResult(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            double start,
            double end,
            JArray poses,
            JObject analysis,
            byte[] motionBytes,
            TimelineAnimationRecord animation = null,
            string inputSignature = null)
        {
            string id = Guid.NewGuid().ToString("D");
            string motionPath = AnalysisMotionCachePath(session, id);
            Directory.CreateDirectory(Path.GetDirectoryName(motionPath));
            if (motionBytes != null && motionBytes.Length > 0)
            {
                File.WriteAllBytes(motionPath, motionBytes);
            }
            var record = new AnalysisCacheRecord
            {
                Id = id,
                SessionId = session.Id.ToString("D"),
                TimelineAssetGuid = AssetDatabase.AssetPathToGUID(session.TimelineAssetPath),
                SessionName = session.Name,
                CharacterRef = character.CharacterRef,
                CharacterName = character.Name,
                Start = start,
                End = end,
                CreatedAtUtc = DateTime.UtcNow,
                Poses = poses != null ? (JArray)poses.DeepClone() : new JArray(),
                Analysis = analysis != null ? (JObject)analysis.DeepClone() : new JObject(),
                MotionPath = motionBytes != null && motionBytes.Length > 0
                    ? ToProjectRelativePath(motionPath)
                    : string.Empty,
                AnimationId = animation?.Id.ToString("D") ?? string.Empty,
                AnimationName = animation?.Name ?? string.Empty,
                InputSignature = inputSignature ?? string.Empty
            };
            AnalysisCache[id] = record;
            WriteJsonAtomically(AnalysisCachePath(session, id), record.ToJson());
            return id;
        }

        private static string AnalysisCachePath(TimelineSessionRecord session, string id) =>
            Path.Combine(GetSessionGeneratedFolder(session), "Analyses", $"analysis_{id}.json");

        private static string AnalysisMotionCachePath(TimelineSessionRecord session, string id) =>
            Path.Combine(GetSessionGeneratedFolder(session), "Analyses", $"analysis_{id}.kmb");

        private static string EvidenceFolder(TimelineSessionRecord session) =>
            Path.Combine(GetSessionGeneratedFolder(session), "Pictures");

        private static bool TryGetRoot2DWorld(
            KimodoMarkerSampleResult sample,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (sample?.validMask?.rootPosition != true || sample.rootOverride == null)
            {
                return false;
            }

            position = sample.rootOverride.t;
            rotation = sample.rootOverride.q.normalized;
            return true;
        }

        private static Vector3 PreviewRootPosition(GameObject preview)
        {
            Animator animator = preview.GetComponentInChildren<Animator>(true);
            return animator != null ? animator.transform.position : preview.transform.position;
        }

        private static void CreateEvidenceLights(List<GameObject> objects, Vector3 center)
        {
            bool isBuiltIn = IsBuiltInCapturePipeline();
            foreach (var setup in new[]
            {
                (position: new Vector3(-4f, 6f, -4f), intensity: isBuiltIn ? 1.125f : 3.3f),
                (position: new Vector3(4f, 3f, -2f), intensity: isBuiltIn ? .525f : 1.65f),
                (position: new Vector3(0f, 5f, 5f), intensity: isBuiltIn ? .30f : 1.05f)
            })
            {
                GameObject lightObject = MoveToAnalysisSessionRoot(
                    new GameObject("Kimodo Evidence Light") { hideFlags = HideFlags.HideAndDontSave });
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = setup.intensity;
                lightObject.transform.position = center + setup.position;
                lightObject.transform.LookAt(center);
                objects.Add(lightObject);
            }
        }

        private static void CreateWorldLine(List<GameObject> objects, Vector3 from, Vector3 to, float width, Color color, bool unlit = false)
        {
            GameObject lineObject = MoveToAnalysisSessionRoot(
                new GameObject("Kimodo Evidence Line") { hideFlags = HideFlags.HideAndDontSave });
            SetLayerRecursively(lineObject, SessionCaptureLayer);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.SetPositions(new[] { from, to });
            line.startWidth = line.endWidth = width;
            line.useWorldSpace = true;
            line.sharedMaterial = unlit ? MakeUnlitMaterial(color) : MakeMaterial(color);
            line.startColor = line.endColor = color;
            objects.Add(lineObject);
        }

        private static Material MakeMaterial(Color color)
        {
            string pipelineName = GraphicsSettings.currentRenderPipeline == null
                ? string.Empty
                : GraphicsSettings.currentRenderPipeline.GetType().FullName ?? string.Empty;
            bool isHdrp = pipelineName.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) >= 0 ||
                pipelineName.IndexOf("HDRP", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isUrp = pipelineName.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                pipelineName.IndexOf("URP", StringComparison.OrdinalIgnoreCase) >= 0;
            Shader shader = isHdrp
                ? Shader.Find("HDRP/Unlit")
                : isUrp
                    ? Shader.Find("Universal Render Pipeline/Unlit")
                    : Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            shader ??= Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, color = color };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", color);
            return material;
        }

        private static Material MakeUnlitMaterial(Color color)
        {
            return MakeMaterial(color);
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = layer;
        }

        private static GameObject MoveToAnalysisSessionRoot(GameObject gameObject)
        {
            if (gameObject != null && captureSessionRoot != null)
            {
                gameObject.transform.SetParent(captureSessionRoot.transform, true);
                SetLayerRecursively(gameObject, SessionCaptureLayer);
            }
            return gameObject;
        }

        private static Texture2D RenderCamera(Camera camera, int size, Color background)
        {
            return RenderCamera(camera, size, size, background);
        }

        private static Texture2D RenderCamera(Camera camera, int width, int height, Color background)
        {
            RenderTexture renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            try
            {
                camera.backgroundColor = background;
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                var image = new Texture2D(width, height, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply(false, false);
                return image;
            }
            finally
            {
                RenderTexture.active = previous;
                camera.targetTexture = null;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        private static Texture2D ReadRenderTexture(RenderTexture source, int width, int height)
        {
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = source;
                var image = new Texture2D(width, height, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply(false, false);
                return image;
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        private static Texture2D ResizeTexture(Texture2D source, int width, int height)
        {
            RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                var image = new Texture2D(width, height, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply(false, false);
                return image;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        private static void Composite(Texture2D destination, Texture2D source, float alpha)
        {
            Color[] destinationPixels = destination.GetPixels();
            Color[] sourcePixels = source.GetPixels();
            for (int index = 0; index < destinationPixels.Length; index++)
            {
                if (sourcePixels[index].a > .01f)
                {
                    destinationPixels[index] = Color.Lerp(
                        destinationPixels[index], sourcePixels[index], alpha * sourcePixels[index].a);
                }
            }
            destination.SetPixels(destinationPixels);
            destination.Apply(false, false);
        }

        private static void SetEvidenceVisualsEnabled(
            IReadOnlyList<GameObject> objects,
            bool enabled,
            bool preserveLineRenderers = false)
        {
            foreach (GameObject item in objects)
            {
                if (item == null) continue;
                foreach (Renderer renderer in item.GetComponentsInChildren<Renderer>(true))
                {
                    if (preserveLineRenderers && renderer is LineRenderer) continue;
                    renderer.enabled = enabled;
                }
            }
        }

        private static Bounds CalculateBounds(GameObject root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                return new Bounds(root.transform.position + Vector3.up, new Vector3(1f, 2f, 1f));
            }
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }
            return bounds;
        }

        private static AnalysisCacheRecord GetCachedAnalysis(TimelineSessionRecord session, string id)
        {
            if (!Guid.TryParse(id, out _))
            {
                throw new InvalidOperationException("analysis_id is not a valid GUID.");
            }
            if (AnalysisCache.TryGetValue(id, out AnalysisCacheRecord cached))
            {
                if (string.Equals(cached.SessionId, session.Id.ToString("D"), StringComparison.OrdinalIgnoreCase)) return cached;
                throw new InvalidOperationException("analysis_id belongs to a different Session.");
            }
            string path = AnalysisCachePath(session, id);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"Unknown analysis_id '{id}' in the selected Session.");
            }
            cached = AnalysisCacheRecord.FromJson(JObject.Parse(File.ReadAllText(path)));
            if (!string.Equals(cached.SessionId, session.Id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("analysis_id belongs to a different Session.");
            }
            AnalysisCache[id] = cached;
            return cached;
        }

        private static bool TryFindCachedAnimationAnalysis(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            TimelineAnimationRecord animation,
            string inputSignature,
            out AnalysisCacheRecord cached)
        {
            cached = null;
            if (session == null || character == null || animation == null || string.IsNullOrWhiteSpace(inputSignature))
            {
                return false;
            }

            string animationId = animation.Id.ToString("D");
            IEnumerable<AnalysisCacheRecord> records = AnalysisCache.Values
                .Concat(EnumerateAnalysisCacheRecords(session))
                .GroupBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First());
            cached = records
                .Where(record => record != null &&
                    string.Equals(record.SessionId, session.Id.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(record.CharacterRef, character.CharacterRef, StringComparison.Ordinal) &&
                    string.Equals(record.AnimationId, animationId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(record.InputSignature, inputSignature, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(record.MotionPath) &&
                    File.Exists(ProjectRelativePathToAbsolute(record.MotionPath)))
                .OrderByDescending(record => record.CreatedAtUtc)
                .FirstOrDefault();
            if (cached == null)
            {
                return false;
            }

            AnalysisCache[cached.Id] = cached;
            return true;
        }

        private static IEnumerable<AnalysisCacheRecord> EnumerateAnalysisCacheRecords(TimelineSessionRecord session)
        {
            string folder = Path.Combine(GetSessionGeneratedFolder(session), "Analyses");
            if (!Directory.Exists(folder))
            {
                yield break;
            }
            foreach (string path in Directory.GetFiles(folder, "analysis_*.json"))
            {
                AnalysisCacheRecord record = null;
                try
                {
                    record = AnalysisCacheRecord.FromJson(JObject.Parse(File.ReadAllText(path)));
                }
                catch
                {
                    // A malformed cache entry is not a valid analysis result and must never be reused.
                }
                if (record != null)
                {
                    yield return record;
                }
            }
        }

        private static string BuildAnimationAnalysisSignature(
            TimelineCharacterRecord character,
            TimelineAnimationRecord animation,
            JObject effectiveOptions)
        {
            var signature = new JObject
            {
                // v4 aligns native KMB analysis frames to the 60 FPS Session
                // time base before keyframe/contact extraction. Do not reuse
                // v3 caches, whose markers are in the model's native FPS.
                ["contract"] = "animation_analysis_picture_v4",
                ["character_ref"] = character?.CharacterRef ?? string.Empty,
                ["rig_type"] = IsHumanoidCharacter(character) ? "humanoid" : "mesh",
                ["animation_id"] = animation?.Id.ToString("D") ?? string.Empty,
                ["start_frame"] = animation?.StartFrame ?? 0,
                ["end_frame_exclusive"] = animation?.EndFrameExclusive ?? 0,
                ["options"] = CanonicalizeJson(effectiveOptions ?? new JObject())
            };
            return signature.ToString(Formatting.None);
        }

        private static JToken CanonicalizeJson(JToken value)
        {
            if (value is JObject source)
            {
                var result = new JObject();
                foreach (JProperty property in source.Properties().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    result[property.Name] = CanonicalizeJson(property.Value);
                }
                return result;
            }
            if (value is JArray array)
            {
                return new JArray(array.Select(CanonicalizeJson));
            }
            return value?.DeepClone() ?? JValue.CreateNull();
        }

        private static string ProjectRelativePathToAbsolute(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }
            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }
            return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path.Replace('/', Path.DirectorySeparatorChar)));
        }

    }
}
