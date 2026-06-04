using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using TimelineInject;

namespace KimodoBridge.Editor
{
    public static class KimodoPreviewConstants
    {
        public static readonly GUIStyle PreviewBackgroundSolid = "PreBackgroundSolid";
        public const string PreviewTag = KimodoEditorPreviewConstants.PreviewTag;
    }

    public static class KimodoPreviewEditorHelper
    {
        public static GameObject InstantiateGoByPrefab(GameObject prefab, GameObject parent)
        {
            if (prefab == null) return null;
            GameObject obj = UnityEngine.Object.Instantiate(prefab);
            if (obj == null) return null;
            obj.name = prefab.name;
            if (parent != null) obj.transform.SetParent(parent.transform, false);
            obj.transform.localPosition = prefab.transform.localPosition;
            obj.transform.localRotation = prefab.transform.localRotation;
            obj.transform.localScale = prefab.transform.localScale;
            return obj;
        }
    }
    public class KimodoPreviewTimeControl
    {
        // currentTime will be clamped to preview range.
        // Make sure it's initially at the beginning, even if the clip start is negative.
        public float currentTime = Mathf.NegativeInfinity;
        public float nextCurrentTime
        {
            set { deltaTime = value - currentTime; m_NextCurrentTimeSet = true; }
        }
        private bool m_NextCurrentTimeSet = false;
        public float startTime = 0.0f;
        public float stopTime = 1.0f;
        public bool playSelection = false;
        public bool loop = true;
        public float playbackSpeed = 1.0f;
        private float m_DeltaTime = 0.0f;
        private bool m_DeltaTimeSet = false;

        class Styles2
        {
            public GUIStyle timelineTick = "AnimationTimelineTick";
            public GUIStyle labelTickMarks = "CurveEditorLabelTickMarks";
            public GUIStyle playhead = "AnimationPlayHead";
        }

        static Styles2 timeAreaStyles;

        public Action<bool> PlayStatusChanged
        {
            get;
            set;
        }

        public Action OnPlayEnd
        {
            get;
            set;
        }

        public float deltaTime
        {
            get { return m_DeltaTime; }
            set { m_DeltaTime = value; m_DeltaTimeSet = true; }
        }
        public float normalizedTime
        {
            // Don't use InverseLerp and Lerp since they clamp between 0 and 1
            get { return (stopTime == startTime) ? 0 : ((currentTime - startTime) / (stopTime - startTime)); }
            set { currentTime = startTime * (1 - value) + stopTime * value; }
        }
        public bool playing
        {
            get { return m_Playing; }
            set
            {
                if (m_Playing != value)
                {
                    // Start Playing
                    if (value)
                    {
                        m_LastFrameEditorTime = EditorApplication.timeSinceStartup;

                        if (m_ResetOnPlay)
                        {
                            nextCurrentTime = startTime;
                            m_ResetOnPlay = false;
                        }
                    }
                    // Stop Playing
                    else
                    {
                    }

                    PlayStatusChanged?.Invoke(value);
                }

                m_Playing = value;
            }
        }

        private double m_LastFrameEditorTime = 0.0f;
        private bool m_Playing = false;
        private bool m_ResetOnPlay = false;
        private float m_MouseDrag = 0.0f;
        private bool m_WrapForwardDrag = false;
        private bool m_IsScrubbing = false;

        public bool IsScrubbing => m_IsScrubbing;
        public bool HasPendingManualTimeStep => m_DeltaTimeSet || m_NextCurrentTimeSet;

        private const float kStepTime = 0.01f;
        private const float kScrubberHeight = 21;
        private const float kPlayButtonWidth = 33;

        private class Styles
        {
            public GUIContent playIcon = EditorGUIUtility.IconContent("PlayButton");
            public GUIContent pauseIcon = EditorGUIUtility.IconContent("PauseButton");

            public GUIStyle playButton = "TimeScrubberButton";
            public GUIStyle timeScrubber = "TimeScrubber";
        }
        private static Styles s_Styles;

        private static readonly int kScrubberIDHash = "ScrubberIDHash".GetHashCode();
        public void DoTimeControl(Rect rect)
        {
            if (s_Styles == null)
                s_Styles = new Styles();

            var evt = Event.current;
            int id = EditorGUIUtility.GetControlID(kScrubberIDHash, FocusType.Keyboard);

            // Play/Pause Button + Scrubber
            Rect timelineRect = rect;
            timelineRect.height = kScrubberHeight;
            // Only Scrubber
            Rect scrubberRect = timelineRect;
            scrubberRect.xMin += kPlayButtonWidth;

            // Handle Input
            switch (evt.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (rect.Contains(evt.mousePosition))
                    {
                        EditorGUIUtility.keyboardControl = id;
                    }
                    if (scrubberRect.Contains(evt.mousePosition))
                    {
                        EditorGUIUtility.SetWantsMouseJumping(1);
                        EditorGUIUtility.hotControl = id;
                        m_IsScrubbing = true;
                        m_MouseDrag = evt.mousePosition.x - scrubberRect.xMin;
                        nextCurrentTime = (m_MouseDrag * (stopTime - startTime) / scrubberRect.width + startTime);
                        m_WrapForwardDrag = false;
                        evt.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (EditorGUIUtility.hotControl == id)
                    {
                        m_MouseDrag += evt.delta.x * playbackSpeed;
                        // We want to not wrap if we immediately drag to the beginning, but we do want to wrap if we drag past the end.
                        if (loop && ((m_MouseDrag < 0.0f && m_WrapForwardDrag) || (m_MouseDrag > scrubberRect.width)))
                        {
                            // scrubing out of range was generating a big deltaTime in wrong time direction
                            // this new code prevent this and it is compliant with new and more robust v5.0 root motion looping of animation clip
                            if (m_MouseDrag > scrubberRect.width)
                            {
                                currentTime -= (stopTime - startTime);
                                OnPlayEnd?.Invoke();
                            }
                            else if (m_MouseDrag < 0)
                            {
                                currentTime += (stopTime - startTime);
                            }

                            m_WrapForwardDrag = true;
                            m_MouseDrag = Mathf.Repeat(m_MouseDrag, scrubberRect.width);
                        }
                        nextCurrentTime = (Mathf.Clamp(m_MouseDrag, 0.0f, scrubberRect.width) * (stopTime - startTime) / scrubberRect.width + startTime);
                        evt.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (EditorGUIUtility.hotControl == id)
                    {
                        EditorGUIUtility.SetWantsMouseJumping(0);
                        EditorGUIUtility.hotControl = 0;
                        m_IsScrubbing = false;
                        evt.Use();
                    }
                    break;
                case EventType.KeyDown:
                    if (EditorGUIUtility.keyboardControl == id)
                    {
                        // TODO: loop?
                        if (evt.keyCode == KeyCode.LeftArrow)
                        {
                            if (currentTime - startTime > kStepTime)
                                deltaTime = -kStepTime;
                            evt.Use();
                        }
                        if (evt.keyCode == KeyCode.RightArrow)
                        {
                            if (stopTime - currentTime > kStepTime)
                                deltaTime = kStepTime;
                            evt.Use();
                        }
                    }
                    break;
            }

            // background
            GUI.Box(timelineRect, GUIContent.none, s_Styles.timeScrubber);

            // Play/Pause Button
            playing = GUI.Toggle(timelineRect, playing, playing ? s_Styles.pauseIcon : s_Styles.playIcon, s_Styles.playButton);

            // Current time indicator
            float normalizedPosition = Mathf.Lerp(scrubberRect.x, scrubberRect.xMax, normalizedTime);
            DrawPlayhead(normalizedPosition, scrubberRect.yMin, scrubberRect.yMax, 2f, (EditorGUIUtility.keyboardControl == id) ? 1f : 0.5f);
        }

        public void OnDisable()
        {
            playing = false;
        }

        public void Update()
        {
            // If the deltaTime was not set, update it when playing
            if (!m_DeltaTimeSet)
            {
                if (playing)
                {
                    double timeSinceStartup = EditorApplication.timeSinceStartup;
                    deltaTime = (float)(timeSinceStartup - m_LastFrameEditorTime) * playbackSpeed;
                    m_LastFrameEditorTime = timeSinceStartup;
                }
                else
                    deltaTime = 0;
            }


            currentTime += deltaTime;

            // If the nextCurrentTime was set explicitly, we don't want to loop
            bool wrap = loop && playing && !m_NextCurrentTimeSet;
            if (wrap)
            {
                if (normalizedTime >= 1f)
                {
                    OnPlayEnd?.Invoke();
                }
                normalizedTime = Mathf.Repeat(normalizedTime, 1.0f);
            }
            else
            {
                if (normalizedTime > 1)
                {
                    OnPlayEnd?.Invoke();
                    playing = false;
                    m_ResetOnPlay = true;
                }
                normalizedTime = Mathf.Clamp01(normalizedTime);
            }

            m_DeltaTimeSet = false;
            m_NextCurrentTimeSet = false;
        }

        public static void DrawPlayhead(float x, float yMin, float yMax, float thickness, float alpha)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            InitStyles();
            float halfThickness = thickness * 0.5f;
            Color lineColor = AlphaMultiplied(timeAreaStyles.playhead.normal.textColor, alpha);
            if (thickness > 1f)
            {
                Rect labelRect = Rect.MinMaxRect(x - halfThickness, yMin, x + halfThickness, yMax);
                EditorGUI.DrawRect(labelRect, lineColor);
            }
            else
            {
                DrawVerticalLine(x, yMin, yMax, lineColor);
            }
        }

        private static void InitStyles()
        {
            if (timeAreaStyles == null)
                timeAreaStyles = new Styles2();
        }

        private static Color AlphaMultiplied(Color color, float multiplier) { return new Color(color.r, color.g, color.b, color.a * multiplier); }

        public static void DrawVerticalLine(float x, float minY, float maxY, Color color)
        {
            if (Event.current.type != EventType.Repaint)
                return;

            Color backupCol = Handles.color;

            KimodoTimelinePreviewRefreshUtility.ApplyWireMaterial();
            if (Application.platform == RuntimePlatform.WindowsEditor)
                GL.Begin(GL.QUADS);
            else
                GL.Begin(GL.LINES);
            DrawVerticalLineFast(x, minY, maxY, color);
            GL.End();

            Handles.color = backupCol;
        }

        public static void DrawVerticalLineFast(float x, float minY, float maxY, Color color)
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                GL.Color(color);
                GL.Vertex(new Vector3(x - 0.5f, minY, 0));
                GL.Vertex(new Vector3(x + 0.5f, minY, 0));
                GL.Vertex(new Vector3(x + 0.5f, maxY, 0));
                GL.Vertex(new Vector3(x - 0.5f, maxY, 0));
            }
            else
            {
                GL.Color(color);
                GL.Vertex(new Vector3(x, minY, 0));
                GL.Vertex(new Vector3(x, maxY, 0));
            }
        }
    }



    public static class KimodoPreviewGameObjectInspector
    {
        public static bool HasRenderableParts(GameObject go)
        {
            MeshRenderer[] renderers = go.GetComponentsInChildren<MeshRenderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshFilter filter = renderers[i].gameObject.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                {
                    return true;
                }
            }

            SkinnedMeshRenderer[] skins = go.GetComponentsInChildren<SkinnedMeshRenderer>();
            for (int i = 0; i < skins.Length; i++)
            {
                if (skins[i].sharedMesh != null)
                {
                    return true;
                }
            }

            SpriteRenderer[] sprites = go.GetComponentsInChildren<SpriteRenderer>();
            for (int i = 0; i < sprites.Length; i++)
            {
                if (sprites[i].sprite != null)
                {
                    return true;
                }
            }

            BillboardRenderer[] billboards = go.GetComponentsInChildren<BillboardRenderer>();
            for (int i = 0; i < billboards.Length; i++)
            {
                if (billboards[i].billboard != null && billboards[i].sharedMaterial != null)
                {
                    return true;
                }
            }

            return false;
        }

        public static void GetRenderableBoundsRecurse(ref Bounds bounds, GameObject go)
        {
            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            MeshFilter filter = go.GetComponent<MeshFilter>();
            if (renderer != null && filter != null && filter.sharedMesh != null)
            {
                if (bounds.extents == Vector3.zero) bounds = renderer.bounds;
                else bounds.Encapsulate(renderer.bounds);
            }

            SkinnedMeshRenderer skin = go.GetComponent<SkinnedMeshRenderer>();
            if (skin != null && skin.sharedMesh != null)
            {
                if (bounds.extents == Vector3.zero) bounds = skin.bounds;
                else bounds.Encapsulate(skin.bounds);
            }

            SpriteRenderer sprite = go.GetComponent<SpriteRenderer>();
            if (sprite != null && sprite.sprite != null)
            {
                if (bounds.extents == Vector3.zero) bounds = sprite.bounds;
                else bounds.Encapsulate(sprite.bounds);
            }

            BillboardRenderer billboard = go.GetComponent<BillboardRenderer>();
            if (billboard != null && billboard.billboard != null && billboard.sharedMaterial != null)
            {
                if (bounds.extents == Vector3.zero) bounds = billboard.bounds;
                else bounds.Encapsulate(billboard.bounds);
            }

            foreach (Transform t in go.transform)
            {
                GetRenderableBoundsRecurse(ref bounds, t.gameObject);
            }
        }

        private static float GetRenderableCenterRecurse(ref Vector3 center, GameObject go, int depth, int minDepth, int maxDepth)
        {
            if (depth > maxDepth) return 0f;

            float ret = 0f;
            if (depth > minDepth)
            {
                MeshRenderer renderer = go.GetComponent<MeshRenderer>();
                MeshFilter filter = go.GetComponent<MeshFilter>();
                SkinnedMeshRenderer skin = go.GetComponent<SkinnedMeshRenderer>();
                SpriteRenderer sprite = go.GetComponent<SpriteRenderer>();
                BillboardRenderer billboard = go.GetComponent<BillboardRenderer>();

                if (renderer == null && filter == null && skin == null && sprite == null && billboard == null)
                {
                    ret = 1f;
                    center += go.transform.position;
                }
                else if (renderer != null && filter != null && Vector3.Distance(renderer.bounds.center, go.transform.position) < 0.01f)
                {
                    ret = 1f;
                    center += go.transform.position;
                }
                else if (skin != null && Vector3.Distance(skin.bounds.center, go.transform.position) < 0.01f)
                {
                    ret = 1f;
                    center += go.transform.position;
                }
                else if (sprite != null && Vector3.Distance(sprite.bounds.center, go.transform.position) < 0.01f)
                {
                    ret = 1f;
                    center += go.transform.position;
                }
                else if (billboard != null && Vector3.Distance(billboard.bounds.center, go.transform.position) < 0.01f)
                {
                    ret = 1f;
                    center += go.transform.position;
                }
            }

            depth++;
            foreach (Transform t in go.transform)
            {
                ret += GetRenderableCenterRecurse(ref center, t.gameObject, depth, minDepth, maxDepth);
            }

            return ret;
        }

        public static Vector3 GetRenderableCenterRecurse(GameObject go, int minDepth, int maxDepth)
        {
            Vector3 center = Vector3.zero;
            float sum = GetRenderableCenterRecurse(ref center, go, 0, minDepth, maxDepth);
            if (sum > 0f) center /= sum;
            else center = go.transform.position;
            return center;
        }

        public static void SetEnabledRecursive(GameObject go, bool enabled)
        {
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                renderers[i].enabled = enabled;
            }
        }
    }



    public class KimodoAvatarPreview
    {
        private static readonly HashSet<KimodoAvatarPreview> ActivePreviews = new HashSet<KimodoAvatarPreview>();
        const string kIkPref = "AvatarpreviewShowIK";
        const string k2DPref = "Avatarpreview2D";
        const string kReferencePref = "AvatarpreviewShowReference";
        const string kSpeedPref = "AvatarpreviewSpeed";
        const float kTimeControlRectHeight = 20;

        public delegate void OnAvatarChange();

        OnAvatarChange m_OnAvatarChangeFunc = null;

        public OnAvatarChange OnAvatarChangeFunc
        {
            set { m_OnAvatarChangeFunc = value; }
        }

        public bool IKOnFeet
        {
            get { return m_IKOnFeet; }
        }

        public bool ShowIKOnFeetButton
        {
            get { return m_ShowIKOnFeetButton; }
            set { m_ShowIKOnFeetButton = value; }
        }

        public bool is2D
        {
            get { return m_2D; }
            set
            {
                m_2D = value;
                if (m_2D)
                {
                    m_PreviewDir = new Vector2();
                }
            }
        }

        public Animator Animator
        {
            get
            {
                return m_PreviewInstance != null ? m_PreviewInstance.GetComponent(typeof(Animator)) as Animator : null;
            }
        }

        public GameObject PreviewObject
        {
            get { return m_PreviewInstance; }
        }

        public ModelImporterAnimationType animationClipType
        {
            get { return GetAnimationType(m_SourcePreviewMotion); }
        }

        public Vector3 bodyPosition
        {
            get
            {
                if (m_PreviewInstance != null)
                    return KimodoPreviewGameObjectInspector.GetRenderableCenterRecurse(m_PreviewInstance, 1, 8);

                if (Animator && Animator.isHuman)
                    return KimodoTimelinePreviewRefreshUtility.GetBodyPosition(Animator);

                return Vector3.zero;
            }
        }

        public Vector3 rootPosition
        {
            get { return m_PreviewInstance ? m_PreviewInstance.transform.position : Vector3.zero; }
        }


        public KimodoPreviewTimeControl timeControl;

        private Material m_FloorMaterial;
        private Material m_FloorMaterialSmall;
        private Material m_ShadowMaskMaterial;
        private Material m_ShadowPlaneMaterial;

        PreviewRenderUtility m_PreviewUtility;
        GameObject m_PreviewInstance;
        GameObject m_ReferenceInstance;
        GameObject m_DirectionInstance;
        GameObject m_PivotInstance;
        GameObject m_RootInstance;
        float m_BoundingVolumeScale;
        Motion m_SourcePreviewMotion;
        Animator m_SourceScenePreviewAnimator;

        const string s_PreviewStr = "Preview";
        int m_PreviewHint = s_PreviewStr.GetHashCode();

        const string s_PreviewSceneStr = "PreviewSene";
        int m_PreviewSceneHint = s_PreviewSceneStr.GetHashCode();

        Texture2D m_FloorTexture;
        Mesh m_FloorPlane;

        bool m_ShowReference = false;

        bool m_IKOnFeet = false;
        bool m_ShowIKOnFeetButton = true;

        bool m_2D;

        bool m_IsValid;

        private const float kFloorFadeDuration = 0.2f;
        private const float kFloorScale = 5;
        private const float kFloorScaleSmall = 0.2f;
        private const float kFloorTextureScale = 4;
        private const float kFloorAlpha = 0.5f;
        private const float kFloorShadowAlpha = 0.3f;

        private float m_PrevFloorHeight = 0;
        private float m_NextFloorHeight = 0;

        private Vector2 m_PreviewDir = new Vector2(120, -20);
        private float m_AvatarScale = 1.0f;
        private float m_ZoomFactor = 1.0f;
        private Vector3 m_PivotPositionOffset = Vector3.zero;

        private class Styles
        {
            public GUIContent speedScale =
                EditorGUIUtility.TrIconContent("SpeedScale", "Changes animation preview speed");

            public GUIContent pivot =
                EditorGUIUtility.TrIconContent("AvatarPivot", "Displays avatar's pivot and mass center");

            public GUIContent ik = EditorGUIUtility.TrTextContent("IK", "Toggles feet IK preview");
            public GUIContent is2D = EditorGUIUtility.TrIconContent("SceneView2D", "Toggles 2D preview mode");

            public GUIContent avatarIcon =
                EditorGUIUtility.TrIconContent("AvatarSelector", "Changes the model to use for previewing.");

            public GUIStyle avatarDropdown = new GUIStyle(EditorStyles.toolbarButton)
            {
                stretchWidth = false
            };

            public GUIStyle preButton = "toolbarbutton";
            public GUIStyle preSlider = "preSlider";
            public GUIStyle preSliderThumb = "preSliderThumb";
            public GUIStyle preLabel = "preLabel";
        }

        private static Styles s_Styles;

        void SetPreviewCharacterEnabled(bool enabled, bool showReference)
        {
            if (m_PreviewInstance != null)
                KimodoPreviewGameObjectInspector.SetEnabledRecursive(m_PreviewInstance, enabled);
            KimodoPreviewGameObjectInspector.SetEnabledRecursive(m_ReferenceInstance, showReference && enabled);
            KimodoPreviewGameObjectInspector.SetEnabledRecursive(m_DirectionInstance, showReference && enabled);
            KimodoPreviewGameObjectInspector.SetEnabledRecursive(m_PivotInstance, showReference && enabled);
            KimodoPreviewGameObjectInspector.SetEnabledRecursive(m_RootInstance, showReference && enabled);
        }

        static AnimationClip GetFirstAnimationClipFromMotion(Motion motion)
        {
            AnimationClip clip = motion as AnimationClip;
            if (clip)
                return clip;

            UnityEditor.Animations.BlendTree blendTree = motion as UnityEditor.Animations.BlendTree;
            if (blendTree != null)
            {
                AnimationClip[] clips = KimodoTimelinePreviewRefreshUtility.GetAnimationClipsFlattened(blendTree);
                if (clips.Length > 0)
                    return clips[0];
            }

            return null;
        }

        static public ModelImporterAnimationType GetAnimationType(GameObject go)
        {
            Animator animator = go.GetComponent<Animator>();
            if (animator)
            {
                Avatar avatar = animator.avatar;
                if (avatar && avatar.isHuman)
                    return ModelImporterAnimationType.Human;
                else
                    return ModelImporterAnimationType.Generic;
            }
            else if (go.GetComponent<Animation>() != null)
            {
                return ModelImporterAnimationType.Legacy;
            }
            else
                return ModelImporterAnimationType.None;
        }

        static public ModelImporterAnimationType GetAnimationType(Motion motion)
        {
            AnimationClip clip = GetFirstAnimationClipFromMotion(motion);
            if (clip)
            {
                if (clip.legacy)
                    return ModelImporterAnimationType.Legacy;
                else if (clip.humanMotion)
                    return ModelImporterAnimationType.Human;
                else
                    return ModelImporterAnimationType.Generic;
            }
            else
                return ModelImporterAnimationType.None;
        }

        static public bool IsValidPreviewGameObject(GameObject target, ModelImporterAnimationType requiredClipType)
        {
            if (target != null && !target.activeSelf)
                Debug.LogWarning("Can't preview inactive object, using fallback object");

            return target != null && target.activeSelf && KimodoPreviewGameObjectInspector.HasRenderableParts(target) &&
                   !(requiredClipType != ModelImporterAnimationType.None &&
                     GetAnimationType(target) != requiredClipType);
        }

        static public GameObject FindBestFittingRenderableGameObjectFromModelAsset(Object asset,
            ModelImporterAnimationType animationType)
        {
            if (asset == null)
                return null;

            ModelImporter importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(asset)) as ModelImporter;
            if (importer == null)
                return null;

            string assetPath = KimodoTimelinePreviewRefreshUtility.CalculateBestFittingPreviewGameObject(importer);
            GameObject tempGO = AssetDatabase.LoadMainAssetAtPath(assetPath) as GameObject;

            // We should also check for isHumanClip matching the animationclip requiremenets...
            if (IsValidPreviewGameObject(tempGO, ModelImporterAnimationType.None))
                return tempGO;
            else
                return null;
        }

        static GameObject CalculatePreviewGameObject(Animator selectedAnimator, Motion motion,
            ModelImporterAnimationType animationType)
        {
            AnimationClip sourceClip = GetFirstAnimationClipFromMotion(motion);

            // Use selected preview
            GameObject selected = KimodoPreviewEditorHelper.InstantiateGoByPrefab(selectedAnimator.gameObject, null);
            TrySetPreviewTag(selected, KimodoPreviewConstants.PreviewTag);
            InitInstantiatedPreviewRecursive(selected);
            if (IsValidPreviewGameObject(selected, ModelImporterAnimationType.None))
                return selected;

            if (selectedAnimator != null && IsValidPreviewGameObject(selectedAnimator.gameObject, animationType))
                return selectedAnimator.gameObject;

            // Find the best fitting preview game object for the asset we are viewing (Handles @ convention, will pick base path for you)
            selected = FindBestFittingRenderableGameObjectFromModelAsset(sourceClip, animationType);
            if (selected != null)
                return selected;

            if (animationType == ModelImporterAnimationType.Human)
                return GetHumanoidFallback();
            else if (animationType == ModelImporterAnimationType.Generic)
                return GetGenericAnimationFallback();

            return null;
        }

        static GameObject GetGenericAnimationFallback()
        {
            return (GameObject)EditorGUIUtility.Load("Avatar/DefaultGeneric.fbx");
        }

        static GameObject GetHumanoidFallback()
        {
            return (GameObject)EditorGUIUtility.Load("Avatar/DefaultAvatar.fbx");
        }

        public void ResetPreviewInstance()
        {
            Object.DestroyImmediate(m_PreviewInstance);
            GameObject go =
                CalculatePreviewGameObject(m_SourceScenePreviewAnimator, m_SourcePreviewMotion, animationClipType);
            SetupBounds(go);
            Object.DestroyImmediate(go);
        }

        void SetupBounds(GameObject go)
        {
            m_IsValid = go != null && go != GetGenericAnimationFallback();

            if (go != null)
            {
                m_PreviewInstance = KimodoTimelinePreviewRefreshUtility.InstantiateForAnimatorPreview(go);
                TrySetPreviewTag(m_PreviewInstance, KimodoPreviewConstants.PreviewTag);
                previewUtility.AddSingleGO(m_PreviewInstance);

                Bounds bounds = new Bounds(m_PreviewInstance.transform.position, Vector3.zero);
                KimodoPreviewGameObjectInspector.GetRenderableBoundsRecurse(ref bounds, m_PreviewInstance);

                m_BoundingVolumeScale = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));


                if (Animator && Animator.isHuman)
                    m_AvatarScale = m_ZoomFactor = Animator.humanScale;
                else
                    m_AvatarScale = m_ZoomFactor = m_BoundingVolumeScale / 2;
            }
        }

        void InitInstance(Animator scenePreviewObject, Motion motion)
        {
            m_SourcePreviewMotion = motion;
            m_SourceScenePreviewAnimator = scenePreviewObject;

            if (m_PreviewInstance == null)
            {
                GameObject go = CalculatePreviewGameObject(scenePreviewObject, motion, animationClipType);
                SetupBounds(go);
                Object.DestroyImmediate(go);
            }

            if (timeControl == null)
            {
                timeControl = new KimodoPreviewTimeControl();
            }

            if (m_ReferenceInstance == null)
            {
                GameObject referenceGO = (GameObject)EditorGUIUtility.Load("Avatar/dial_flat.prefab");
                m_ReferenceInstance = (GameObject)Object.Instantiate(referenceGO, Vector3.zero, Quaternion.identity);
                TrySetPreviewTag(m_ReferenceInstance, KimodoPreviewConstants.PreviewTag);
                InitInstantiatedPreviewRecursive(m_ReferenceInstance);
                previewUtility.AddSingleGO(m_ReferenceInstance);
            }

            if (m_DirectionInstance == null)
            {
                GameObject directionGO = (GameObject)EditorGUIUtility.Load("Avatar/arrow.fbx");
                m_DirectionInstance = (GameObject)Object.Instantiate(directionGO, Vector3.zero, Quaternion.identity);
                TrySetPreviewTag(m_DirectionInstance, KimodoPreviewConstants.PreviewTag);
                InitInstantiatedPreviewRecursive(m_DirectionInstance);
                previewUtility.AddSingleGO(m_DirectionInstance);
            }

            if (m_PivotInstance == null)
            {
                GameObject pivotGO = (GameObject)EditorGUIUtility.Load("Avatar/root.fbx");
                m_PivotInstance = (GameObject)Object.Instantiate(pivotGO, Vector3.zero, Quaternion.identity);
                TrySetPreviewTag(m_PivotInstance, KimodoPreviewConstants.PreviewTag);
                InitInstantiatedPreviewRecursive(m_PivotInstance);
                previewUtility.AddSingleGO(m_PivotInstance);
            }

            if (m_RootInstance == null)
            {
                GameObject rootGO = (GameObject)EditorGUIUtility.Load("Avatar/root.fbx");
                m_RootInstance = (GameObject)Object.Instantiate(rootGO, Vector3.zero, Quaternion.identity);
                TrySetPreviewTag(m_RootInstance, KimodoPreviewConstants.PreviewTag);
                InitInstantiatedPreviewRecursive(m_RootInstance);
                previewUtility.AddSingleGO(m_RootInstance);
            }

            // Load preview settings from prefs
            m_IKOnFeet = EditorPrefs.GetBool(kIkPref, false);
            m_ShowReference = EditorPrefs.GetBool(kReferencePref, true);
            is2D = EditorPrefs.GetBool(k2DPref, EditorSettings.defaultBehaviorMode == EditorBehaviorMode.Mode2D);
            timeControl.playbackSpeed = EditorPrefs.GetFloat(kSpeedPref, 1f);

            SetPreviewCharacterEnabled(false, false);

            m_PivotPositionOffset = Vector3.zero;
        }

        private PreviewRenderUtility previewUtility
        {
            get
            {
                if (m_PreviewUtility == null)
                {
                    m_PreviewUtility = new PreviewRenderUtility();
                    m_PreviewUtility.camera.fieldOfView = 30.0f;
                    m_PreviewUtility.camera.allowHDR = false;
                    m_PreviewUtility.camera.allowMSAA = false;
                    m_PreviewUtility.ambientColor = new Color(.1f, .1f, .1f, 0);
                    m_PreviewUtility.lights[0].intensity = 1.4f;
                    m_PreviewUtility.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0);
                    m_PreviewUtility.lights[1].intensity = 1.4f;
                }

                return m_PreviewUtility;
            }
        }

        private void Init()
        {
            if (s_Styles == null)
                s_Styles = new Styles();

            if (m_FloorPlane == null)
            {
                m_FloorPlane = Resources.GetBuiltinResource(typeof(Mesh), "New-Plane.fbx") as Mesh;
            }

            if (m_FloorTexture == null)
            {
                m_FloorTexture = (Texture2D)EditorGUIUtility.Load("Avatar/Textures/AvatarFloor.png");
            }

            if (m_FloorMaterial == null)
            {
                Shader shader = EditorGUIUtility.LoadRequired("Previews/PreviewPlaneWithShadow.shader") as Shader;
                m_FloorMaterial = new Material(shader);
                m_FloorMaterial.mainTexture = m_FloorTexture;
                m_FloorMaterial.mainTextureScale = Vector2.one * kFloorScale * kFloorTextureScale;
                m_FloorMaterial.SetVector("_Alphas", new Vector4(kFloorAlpha, kFloorShadowAlpha, 0, 0));
                m_FloorMaterial.hideFlags = HideFlags.HideAndDontSave;

                m_FloorMaterialSmall = new Material(m_FloorMaterial);
                m_FloorMaterialSmall.mainTextureScale = Vector2.one * kFloorScaleSmall * kFloorTextureScale;
                m_FloorMaterialSmall.hideFlags = HideFlags.HideAndDontSave;
            }

            if (m_ShadowMaskMaterial == null)
            {
                Shader shader = EditorGUIUtility.LoadRequired("Previews/PreviewShadowMask.shader") as Shader;
                m_ShadowMaskMaterial = new Material(shader);
                m_ShadowMaskMaterial.hideFlags = HideFlags.HideAndDontSave;
            }

            if (m_ShadowPlaneMaterial == null)
            {
                Shader shader = EditorGUIUtility.LoadRequired("Previews/PreviewShadowPlaneClip.shader") as Shader;
                m_ShadowPlaneMaterial = new Material(shader);
                m_ShadowPlaneMaterial.hideFlags = HideFlags.HideAndDontSave;
            }
        }

        public void OnDisable()
        {
            OnDestroy();
        }

        public void OnDestroy()
        {
            ActivePreviews.Remove(this);
            if (m_PreviewUtility != null)
            {
                m_PreviewUtility.Cleanup();
                m_PreviewUtility = null;
            }

            Object.DestroyImmediate(m_PreviewInstance);
            Object.DestroyImmediate(m_ReferenceInstance);
            Object.DestroyImmediate(m_DirectionInstance);
            Object.DestroyImmediate(m_PivotInstance);
            Object.DestroyImmediate(m_RootInstance);

            if (timeControl != null)
                timeControl.OnDisable();
        }

        public void DoSelectionChange()
        {
            m_OnAvatarChangeFunc();
        }

        public KimodoAvatarPreview(Animator previewObjectInScene, Motion objectOnSameAsset)
        {
            ActivePreviews.Add(this);
            InitInstance(previewObjectInScene, objectOnSameAsset);
        }

        public static void CleanupAllActivePreviews()
        {
            if (ActivePreviews.Count == 0)
            {
                return;
            }

            var snapshot = new List<KimodoAvatarPreview>(ActivePreviews);
            for (int i = 0; i < snapshot.Count; i++)
            {
                KimodoAvatarPreview preview = snapshot[i];
                if (preview == null)
                {
                    continue;
                }

                try
                {
                    preview.OnDestroy();
                }
                catch
                {
                }
            }

            ActivePreviews.Clear();
        }

        float PreviewSlider(Rect rect, float val, float snapThreshold)
        {
            val = GUI.HorizontalSlider(rect, val, 0.1f, 2.0f, s_Styles.preSlider,
                s_Styles.preSliderThumb); //, GUILayout.MaxWidth(64));
            if (val > 0.25f - snapThreshold && val < 0.25f + snapThreshold)
                val = 0.25f;
            else if (val > 0.5f - snapThreshold && val < 0.5f + snapThreshold)
                val = 0.5f;
            else if (val > 0.75f - snapThreshold && val < 0.75f + snapThreshold)
                val = 0.75f;
            else if (val > 1.0f - snapThreshold && val < 1.0f + snapThreshold)
                val = 1.0f;
            else if (val > 1.25f - snapThreshold && val < 1.25f + snapThreshold)
                val = 1.25f;
            else if (val > 1.5f - snapThreshold && val < 1.5f + snapThreshold)
                val = 1.5f;
            else if (val > 1.75f - snapThreshold && val < 1.75f + snapThreshold)
                val = 1.75f;

            return val;
        }

        public void DoPreviewSettings()
        {
            Init();

            if (m_ShowIKOnFeetButton)
            {
                EditorGUI.BeginChangeCheck();
                m_IKOnFeet = GUILayout.Toggle(m_IKOnFeet, s_Styles.ik, s_Styles.preButton);
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetBool(kIkPref, m_IKOnFeet);
            }

            EditorGUI.BeginChangeCheck();
            GUILayout.Toggle(is2D, s_Styles.is2D, s_Styles.preButton);
            if (EditorGUI.EndChangeCheck())
            {
                is2D = !is2D;
                EditorPrefs.SetBool(k2DPref, is2D);
            }

            EditorGUI.BeginChangeCheck();
            m_ShowReference = GUILayout.Toggle(m_ShowReference, s_Styles.pivot, s_Styles.preButton);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetBool(kReferencePref, m_ShowReference);
        }

        private RenderTexture RenderPreviewShadowmap(Light light, float scale, Vector3 center, Vector3 floorPos,
            out Matrix4x4 outShadowMatrix)
        {
            Assert.IsTrue(Event.current.type == EventType.Repaint);

            // Set ortho camera and position it
            var cam = previewUtility.camera;
            cam.orthographic = true;
            cam.orthographicSize = scale * 2.0f;
            cam.nearClipPlane = 1 * scale;
            cam.farClipPlane = 25 * scale;
            cam.transform.rotation = is2D ? Quaternion.identity : light.transform.rotation;
            cam.transform.position = center - light.transform.forward * (scale * 5.5f);

            // Clear to black
            CameraClearFlags oldFlags = cam.clearFlags;
            cam.clearFlags = CameraClearFlags.SolidColor;
            Color oldColor = cam.backgroundColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);

            // Create render target for shadow map
            const int kShadowSize = 256;
            RenderTexture oldRT = cam.targetTexture;
            RenderTexture rt = RenderTexture.GetTemporary(kShadowSize, kShadowSize, 16);
            rt.isPowerOfTwo = true;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.filterMode = FilterMode.Bilinear;
            cam.targetTexture = rt;

            // Enable character and render with camera into the shadowmap
            SetPreviewCharacterEnabled(true, false);
            m_PreviewUtility.camera.Render();

            // Draw a quad, with shader that will produce white color everywhere
            // where something was rendered (via inverted depth test)
            RenderTexture.active = rt;
            GL.PushMatrix();
            GL.LoadOrtho();
            m_ShadowMaskMaterial.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Vertex3(0, 0, -99.0f);
            GL.Vertex3(1, 0, -99.0f);
            GL.Vertex3(1, 1, -99.0f);
            GL.Vertex3(0, 1, -99.0f);
            GL.End();

            // Render floor with black color, to mask out any shadow from character
            // parts that are under the preview plane
            GL.LoadProjectionMatrix(cam.projectionMatrix);
            GL.LoadIdentity();
            GL.MultMatrix(cam.worldToCameraMatrix);
            m_ShadowPlaneMaterial.SetPass(0);
            GL.Begin(GL.QUADS);
            float sc = kFloorScale * scale;
            GL.Vertex(floorPos + new Vector3(-sc, 0, -sc));
            GL.Vertex(floorPos + new Vector3(sc, 0, -sc));
            GL.Vertex(floorPos + new Vector3(sc, 0, sc));
            GL.Vertex(floorPos + new Vector3(-sc, 0, sc));
            GL.End();

            GL.PopMatrix();

            // Shadowmap sampling matrix, from world space into shadowmap space
            Matrix4x4 texMatrix = Matrix4x4.TRS(new Vector3(0.5f, 0.5f, 0.5f), Quaternion.identity,
                new Vector3(0.5f, 0.5f, 0.5f));
            outShadowMatrix = texMatrix * cam.projectionMatrix * cam.worldToCameraMatrix;

            // Restore previous camera parameters
            cam.orthographic = false;
            cam.clearFlags = oldFlags;
            cam.backgroundColor = oldColor;
            cam.targetTexture = oldRT;

            return rt;
        }

        public void DoRenderPreview(Rect previewRect, GUIStyle background)
        {
            var probe = RenderSettings.ambientProbe;
            previewUtility.BeginPreview(previewRect, background);

            Quaternion bodyRot;
            Quaternion rootRot;
            Vector3 rootPos;
            Vector3 bodyPos = rootPosition;
            Vector3 pivotPos;

            if (Animator && Animator.isHuman)
            {
                rootRot = Animator.rootRotation;
                rootPos = Animator.rootPosition;

                bodyRot = Animator.bodyRotation;

                pivotPos = Animator.pivotPosition;
            }
            else if (Animator && Animator.hasRootMotion)
            {
                rootRot = Animator.rootRotation;
                rootPos = Animator.rootPosition;

                bodyRot = Quaternion.identity;

                pivotPos = Vector3.zero;
            }
            else
            {
                rootRot = Quaternion.identity;
                rootPos = Vector3.zero;

                bodyRot = Quaternion.identity;

                pivotPos = Vector3.zero;
            }

            SetupPreviewLightingAndFx(probe);

            Vector3 direction = bodyRot * Vector3.forward;
            direction[1] = 0;
            Quaternion directionRot = Quaternion.LookRotation(direction);
            Vector3 directionPos = rootPos;

            Quaternion pivotRot = rootRot;

            // Scale all Preview Objects to fit avatar size.
            PositionPreviewObjects(pivotRot, pivotPos, bodyRot, bodyPosition, directionRot, rootRot, rootPos,
                directionPos, m_AvatarScale);

            bool dynamicFloorHeight =
                is2D ? false : Mathf.Abs(m_NextFloorHeight - m_PrevFloorHeight) > m_ZoomFactor * 0.01f;

            // Calculate floor height and alpha
            float mainFloorHeight, mainFloorAlpha;
            if (dynamicFloorHeight)
            {
                float fadeMoment = m_NextFloorHeight < m_PrevFloorHeight
                    ? kFloorFadeDuration
                    : (1 - kFloorFadeDuration);
                mainFloorHeight = timeControl.normalizedTime < fadeMoment ? m_PrevFloorHeight : m_NextFloorHeight;
                mainFloorAlpha = Mathf.Clamp01(Mathf.Abs(timeControl.normalizedTime - fadeMoment) / kFloorFadeDuration);
            }
            else
            {
                mainFloorHeight = m_PrevFloorHeight;
                mainFloorAlpha = is2D ? 0.5f : 1;
            }

            Quaternion floorRot = is2D ? Quaternion.Euler(-90, 0, 0) : Quaternion.identity;
            Vector3 floorPos = m_ReferenceInstance.transform.position;
            floorPos.y = mainFloorHeight;

            // Render shadow map
            Matrix4x4 shadowMatrix;
            RenderTexture shadowMap = RenderPreviewShadowmap(previewUtility.lights[0], m_BoundingVolumeScale / 2,
                bodyPosition, floorPos, out shadowMatrix);

            float tempZoomFactor = (is2D ? 1.0f : m_ZoomFactor);
            // Position camera
            previewUtility.camera.orthographic = is2D;
            if (is2D)
                previewUtility.camera.orthographicSize = 2.0f * m_ZoomFactor;
            previewUtility.camera.nearClipPlane = 0.5f * tempZoomFactor;
            previewUtility.camera.farClipPlane = 100.0f * m_AvatarScale;
            Quaternion camRot = Quaternion.Euler(-m_PreviewDir.y, -m_PreviewDir.x, 0);

            // Add panning offset
            Vector3 camPos = camRot * (Vector3.forward * -5.5f * tempZoomFactor) + bodyPos + m_PivotPositionOffset;
            previewUtility.camera.transform.position = camPos;
            previewUtility.camera.transform.rotation = camRot;


            SetPreviewCharacterEnabled(true, m_ShowReference);
            previewUtility.Render(m_Option != PreviewPopupOptions.DefaultModel);
            SetPreviewCharacterEnabled(false, false);

            // Texture offset - negative in order to compensate the floor movement.
            Vector2 textureOffset = -new Vector2(floorPos.x, is2D ? floorPos.y : floorPos.z);

            // Render main floor
            {
                Material mat = m_FloorMaterial;
                Matrix4x4 matrix = Matrix4x4.TRS(floorPos, floorRot, Vector3.one * kFloorScale * m_AvatarScale);

                mat.mainTextureOffset = textureOffset * kFloorScale * 0.08f * (1.0f / m_AvatarScale);
                mat.SetTexture("_ShadowTexture", shadowMap);
                mat.SetMatrix("_ShadowTextureMatrix", shadowMatrix);
                mat.SetVector("_Alphas",
                    new Vector4(kFloorAlpha * mainFloorAlpha, kFloorShadowAlpha * mainFloorAlpha, 0, 0));
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Background;

                Graphics.DrawMesh(m_FloorPlane, matrix, mat, KimodoTimelinePreviewRefreshUtility.GetPreviewCullingLayer(),
                    previewUtility.camera, 0);
            }

            // Render small floor
            if (dynamicFloorHeight)
            {
                bool topIsNext = m_NextFloorHeight > m_PrevFloorHeight;
                float floorHeight = topIsNext ? m_NextFloorHeight : m_PrevFloorHeight;
                float otherFloorHeight = topIsNext ? m_PrevFloorHeight : m_NextFloorHeight;
                float floorAlpha = (floorHeight == mainFloorHeight ? 1 - mainFloorAlpha : 1) *
                                   Mathf.InverseLerp(otherFloorHeight, floorHeight, rootPos.y);
                floorPos.y = floorHeight;

                Material mat = m_FloorMaterialSmall;
                mat.mainTextureOffset = textureOffset * kFloorScaleSmall * 0.08f;
                mat.SetTexture("_ShadowTexture", shadowMap);
                mat.SetMatrix("_ShadowTextureMatrix", shadowMatrix);
                mat.SetVector("_Alphas", new Vector4(kFloorAlpha * floorAlpha, 0, 0, 0));
                Matrix4x4 matrix = Matrix4x4.TRS(floorPos, floorRot, Vector3.one * kFloorScaleSmall * m_AvatarScale);
                Graphics.DrawMesh(m_FloorPlane, matrix, mat, KimodoTimelinePreviewRefreshUtility.GetPreviewCullingLayer(),
                    previewUtility.camera, 0);
            }

            var clearMode = previewUtility.camera.clearFlags;
            previewUtility.camera.clearFlags = CameraClearFlags.Nothing;
            previewUtility.Render(false);
            previewUtility.camera.clearFlags = clearMode;
            RenderTexture.ReleaseTemporary(shadowMap);
        }

        private void SetupPreviewLightingAndFx(SphericalHarmonicsL2 probe)
        {
            previewUtility.lights[0].intensity = 1.4f;
            previewUtility.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0);
            previewUtility.lights[1].intensity = 1.4f;
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientLight = new Color(0.1f, 0.1f, 0.1f, 1.0f);
            RenderSettings.ambientProbe = probe;
        }

        private float m_LastNormalizedTime = -1000;
        private float m_LastStartTime = -1000;
        private float m_LastStopTime = -1000;
        private bool m_NextTargetIsForward = true;

        private void PositionPreviewObjects(Quaternion pivotRot, Vector3 pivotPos, Quaternion bodyRot, Vector3 bodyPos,
            Quaternion directionRot, Quaternion rootRot, Vector3 rootPos, Vector3 directionPos,
            float scale)
        {
            m_ReferenceInstance.transform.position = rootPos;
            m_ReferenceInstance.transform.rotation = rootRot;
            m_ReferenceInstance.transform.localScale = Vector3.one * scale * 1.25f;

            m_DirectionInstance.transform.position = directionPos;
            m_DirectionInstance.transform.rotation = directionRot;
            m_DirectionInstance.transform.localScale = Vector3.one * scale * 2;

            m_PivotInstance.transform.position = pivotPos;
            m_PivotInstance.transform.rotation = pivotRot;
            m_PivotInstance.transform.localScale = Vector3.one * scale * 0.1f;

            m_RootInstance.transform.position = bodyPos;
            m_RootInstance.transform.rotation = bodyRot;
            m_RootInstance.transform.localScale = Vector3.one * scale * 0.25f;

            if (Animator)
            {
                float normalizedTime = timeControl.normalizedTime;
                float normalizedDelta = timeControl.deltaTime / (timeControl.stopTime - timeControl.startTime);

                // Always set last height to next height after wrapping the time.
                if (normalizedTime - normalizedDelta < 0 || normalizedTime - normalizedDelta >= 1)
                    m_PrevFloorHeight = m_NextFloorHeight;

                // Check that AvatarPreview is getting reliable info about time and deltaTime.
                if (m_LastNormalizedTime != -1000 && timeControl.startTime == m_LastStartTime &&
                    timeControl.stopTime == m_LastStopTime)
                {
                    float difference = normalizedTime - normalizedDelta - m_LastNormalizedTime;
                    if (difference > 0.5f)
                        difference -= 1;
                    else if (difference < -0.5f)
                        difference += 1;
                }

                m_LastNormalizedTime = normalizedTime;
                m_LastStartTime = timeControl.startTime;
                m_LastStopTime = timeControl.stopTime;

                // Alternate getting the height for next time and previous time.
                if (m_NextTargetIsForward)
                    m_NextFloorHeight = Animator.targetPosition.y;
                else
                    m_PrevFloorHeight = Animator.targetPosition.y;

                // Flip next target time.
                m_NextTargetIsForward = !m_NextTargetIsForward;
                Animator.SetTarget(AvatarTarget.Root, m_NextTargetIsForward ? 1 : 0);
            }
        }

        public void AvatarTimeControlGUI(Rect rect)
        {
            const float kSliderWidth = 150f;
            const float kSpacing = 4f;
            Rect timeControlRect = rect;

            // background
            GUI.Box(rect, GUIContent.none, EditorStyles.toolbar);

            timeControlRect.height = kTimeControlRectHeight;
            timeControlRect.xMax -= kSliderWidth;

            Rect sliderControlRect = rect;
            sliderControlRect.height = kTimeControlRectHeight;
            sliderControlRect.yMin += 1;
            sliderControlRect.yMax -= 1;
            sliderControlRect.xMin = sliderControlRect.xMax - kSliderWidth + kSpacing;

            timeControl.DoTimeControl(timeControlRect);
            timeControl.playbackSpeed = PreviewSlider(sliderControlRect, timeControl.playbackSpeed, 0.03f);

            // Show current time in seconds and normalized progress.
            rect.y = rect.yMax - 24;
            float time = timeControl.currentTime - timeControl.startTime;
            EditorGUI.DropShadowLabel(new Rect(rect.x, rect.y, rect.width, 20),
                string.Format("{0:F2}s ({1:000.0%})", time, timeControl.normalizedTime));
        }

        enum PreviewPopupOptions
        {
            Auto,
            DefaultModel,
            Other
        }

        protected enum ViewTool
        {
            None,
            Pan,
            Zoom,
            Orbit
        }

        protected ViewTool m_ViewTool = ViewTool.None;

        protected ViewTool viewTool
        {
            get
            {
                Event evt = Event.current;
                if (m_ViewTool == ViewTool.None)
                {
                    bool controlKeyOnMac = (evt.control && Application.platform == RuntimePlatform.OSXEditor);

                    // actionKey could be command key on mac or ctrl on windows
                    bool actionKey = EditorGUI.actionKey;

                    bool noModifiers = (!actionKey && !controlKeyOnMac && !evt.alt);

                    // Keep viewport interaction close to Unity preview defaults:
                    // left-drag orbit, middle-drag pan, alt+right zoom.
                    if (evt.button == 2)
                        m_ViewTool = ViewTool.Pan;
                    else if ((evt.button == 1 && evt.alt) || (evt.button <= 0 && controlKeyOnMac))
                        m_ViewTool = ViewTool.Zoom;
                    else if ((evt.button <= 0 && noModifiers) || (evt.button <= 0 && evt.alt) || evt.button == 1 || (evt.button <= 0 && actionKey))
                        m_ViewTool = ViewTool.Orbit;
                }

                return m_ViewTool;
            }
        }

        protected MouseCursor currentCursor
        {
            get
            {
                switch (m_ViewTool)
                {
                    case ViewTool.Orbit: return MouseCursor.Orbit;
                    case ViewTool.Pan: return MouseCursor.Pan;
                    case ViewTool.Zoom: return MouseCursor.Zoom;
                    default: return MouseCursor.Arrow;
                }
            }
        }


        protected void HandleMouseDown(Event evt, int id, Rect previewRect)
        {
            if (viewTool != ViewTool.None && previewRect.Contains(evt.mousePosition))
            {
                EditorGUIUtility.SetWantsMouseJumping(1);
                evt.Use();
                GUIUtility.hotControl = id;
            }
        }

        protected void HandleMouseUp(Event evt, int id)
        {
            if (GUIUtility.hotControl == id)
            {
                m_ViewTool = ViewTool.None;

                GUIUtility.hotControl = 0;
                EditorGUIUtility.SetWantsMouseJumping(0);
                evt.Use();
            }
        }

        protected void HandleMouseDrag(Event evt, int id, Rect previewRect)
        {
            if (m_PreviewInstance == null)
                return;

            if (GUIUtility.hotControl == id)
            {
                switch (m_ViewTool)
                {
                    case ViewTool.Orbit:
                        DoAvatarPreviewOrbit(evt, previewRect);
                        break;
                    case ViewTool.Pan:
                        DoAvatarPreviewPan(evt);
                        break;

                    // case 605415 invert zoom delta to match scene view zooming
                    case ViewTool.Zoom:
                        DoAvatarPreviewZoom(evt, -HandleUtility.niceMouseDeltaZoom * (evt.shift ? 2.0f : 0.5f));
                        break;
                    default:
                        // Ignore unsupported/unknown view tools to avoid console spam in host editors.
                        break;
                }
            }
        }

        protected void HandleViewTool(Event evt, EventType eventType, int id, Rect previewRect)
        {
            switch (eventType)
            {
                case EventType.ScrollWheel:
                    DoAvatarPreviewZoom(evt, HandleUtility.niceMouseDeltaZoom * (evt.shift ? 2.0f : 0.5f));
                    break;
                case EventType.MouseDown:
                    HandleMouseDown(evt, id, previewRect);
                    break;
                case EventType.MouseUp:
                    HandleMouseUp(evt, id);
                    break;
                case EventType.MouseDrag:
                    HandleMouseDrag(evt, id, previewRect);
                    break;
            }
        }

        public void DoAvatarPreviewDrag(Event evt, EventType type)
        {
            if (type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                evt.Use();
            }
            else if (type == EventType.DragPerform)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                GameObject newPreviewObject = DragAndDrop.objectReferences[0] as GameObject;

                if (newPreviewObject)
                {
                    DragAndDrop.AcceptDrag();
                    SetPreview(newPreviewObject);
                }

                evt.Use();
            }
        }

        public void DoAvatarPreviewOrbit(Event evt, Rect previewRect)
        {
            //Reset 2D on Orbit
            if (is2D)
            {
                is2D = false;
            }

            m_PreviewDir -= evt.delta * (evt.shift ? 3 : 1) / Mathf.Min(previewRect.width, previewRect.height) * 140.0f;
            m_PreviewDir.y = Mathf.Clamp(m_PreviewDir.y, -90, 90);
            evt.Use();
        }

        public void DoAvatarPreviewPan(Event evt)
        {
            Camera cam = previewUtility.camera;
            Vector3 screenPos = cam.WorldToScreenPoint(bodyPosition + m_PivotPositionOffset);
            Vector3 delta = new Vector3(-evt.delta.x, evt.delta.y, 0);
            // delta panning is scale with the zoom factor to allow fine tuning when user is zooming closely.
            screenPos += delta * Mathf.Lerp(0.25f, 2.0f, m_ZoomFactor * 0.5f);
            Vector3 worldDelta = cam.ScreenToWorldPoint(screenPos) - (bodyPosition + m_PivotPositionOffset);
            m_PivotPositionOffset += worldDelta;
            evt.Use();
        }

        public void ResetPreviewFocus()
        {
            m_PivotPositionOffset = bodyPosition - rootPosition;
        }

        public void DoAvatarPreviewFrame(Event evt, EventType type, Rect previewRect)
        {
            if (type == EventType.KeyDown && evt.keyCode == KeyCode.F)
            {
                ResetPreviewFocus();
                m_ZoomFactor = m_AvatarScale;
                evt.Use();
            }

            if (type == EventType.KeyDown && Event.current.keyCode == KeyCode.G)
            {
                m_PivotPositionOffset = GetCurrentMouseWorldPosition(evt, previewRect) - bodyPosition;
                evt.Use();
            }
        }

        protected Vector3 GetCurrentMouseWorldPosition(Event evt, Rect previewRect)
        {
            Camera cam = previewUtility.camera;

            float scaleFactor = previewUtility.GetScaleFactor(previewRect.width, previewRect.height);
            Vector3 mouseLocal = new Vector3((evt.mousePosition.x - previewRect.x) * scaleFactor,
                (previewRect.height - (evt.mousePosition.y - previewRect.y)) * scaleFactor, 0);
            mouseLocal.z = Vector3.Distance(bodyPosition, cam.transform.position);
            return cam.ScreenToWorldPoint(mouseLocal);
        }

        public void DoAvatarPreviewZoom(Event evt, float delta)
        {
            float zoomDelta = -delta * 0.05f;
            m_ZoomFactor += m_ZoomFactor * zoomDelta;

            // zoom is clamp too 10 time closer than the original zoom
            m_ZoomFactor = Mathf.Max(m_ZoomFactor, m_AvatarScale / 10.0f);
            evt.Use();
        }

        public void DoAvatarPreview(Rect rect, GUIStyle background)
        {
            Init();

            Rect previewRect = rect;
            previewRect.yMin += kTimeControlRectHeight;
            previewRect.height = Mathf.Max(previewRect.height, 64f);

            int previewID = GUIUtility.GetControlID(m_PreviewHint, FocusType.Passive, previewRect);
            Event evt = Event.current;
            EventType type = evt.GetTypeForControl(previewID);

            if (type == EventType.Repaint && m_IsValid)
            {
                DoRenderPreview(previewRect, background);
                previewUtility.EndAndDrawPreview(previewRect);
            }

            AvatarTimeControlGUI(rect);


            int previewSceneID = GUIUtility.GetControlID(m_PreviewSceneHint, FocusType.Passive);
            type = evt.GetTypeForControl(previewSceneID);

            DoAvatarPreviewDrag(evt, type);
            HandleViewTool(evt, type, previewSceneID, previewRect);
            DoAvatarPreviewFrame(evt, type, previewRect);

            if (!m_IsValid)
            {
                Rect warningRect = previewRect;
                warningRect.yMax -= warningRect.height / 2 - 16;
                EditorGUI.DropShadowLabel(
                    warningRect,
                    "No model is available for preview.\nPlease drag a model into this Preview Area.");
            }

            // Apply the current cursor
            if (evt.type == EventType.Repaint)
                EditorGUIUtility.AddCursorRect(previewRect, currentCursor);
        }

        private PreviewPopupOptions m_Option;


        void SetPreview(GameObject gameObject)
        {
            KimodoTimelinePreviewRefreshUtility.SetPreview(animationClipType, gameObject);

            Object.DestroyImmediate(m_PreviewInstance);
            InitInstance(m_SourceScenePreviewAnimator, m_SourcePreviewMotion);

            if (m_OnAvatarChangeFunc != null)
                m_OnAvatarChangeFunc();
        }

        private static bool HasPreviewTag()
        {
            string[] tags = InternalEditorUtility.tags;
            for (int i = 0; i < tags.Length; i++)
            {
                if (tags[i] == KimodoPreviewConstants.PreviewTag)
                {
                    return true;
                }
            }
            return false;
        }

        private static void TrySetPreviewTag(GameObject go, string tagName)
        {
            if (go == null || string.IsNullOrEmpty(tagName))
            {
                return;
            }

            if (!HasPreviewTag())
            {
                return;
            }

            go.tag = tagName;
        }

        int Repeat(int t, int length)
        {
            // Have to do double modulo in order to work for negative numbers.
            // This is quicker than a branch to test for negative number.
            return ((t % length) + length) % length;
        }

        internal static void InitInstantiatedPreviewRecursive(GameObject go)
        {
            go.hideFlags = HideFlags.HideAndDontSave;
            go.layer = KimodoTimelinePreviewRefreshUtility.GetPreviewCullingLayer();
            foreach (Transform c in go.transform)
                InitInstantiatedPreviewRecursive(c.gameObject);
        }

        internal static void SetEnabledRecursive(GameObject go, bool enabled)
        {
            Renderer[] componentsInChildren = go.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < componentsInChildren.Length; i++)
            {
                Renderer renderer = componentsInChildren[i];
                renderer.enabled = enabled;
            }
        }

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            if (!HasPreviewTag())
            {
                return;
            }

            var gameobjects = GameObject.FindGameObjectsWithTag(KimodoPreviewConstants.PreviewTag);
            foreach (var item in gameobjects)
            {
                GameObject.DestroyImmediate(item);
            }

        }
    }
}



