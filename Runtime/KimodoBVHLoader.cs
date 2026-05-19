using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class KimodoBVHLoader : MonoBehaviour
{
    [Header("Input")]
    public string bvhFilePath;

    [Header("Space Conversion")]
    [Tooltip("Enable recommended Kimodo BVH -> Unity conversion (mirror X only).")]
    public bool useKimodoSpaceConversion = true;
    [Tooltip("Use legacy conversion logic compatible with old BVHAnimationLoader behavior.")]
    public bool useLegacyConversion = false;
    [Tooltip("Only used when Use Legacy Conversion is enabled.")]
    public bool blender = false;

    [Tooltip("If true, use Frame Time from BVH.")]
    public bool respectBVHTime = true;
    [Tooltip("Used when respectBVHTime is false.")]
    public float frameRate = 30f;

    [Header("Build")]
    [Tooltip("Scale applied to parsed BVH offsets and root translations. 0.01 = cm -> m.")]
    public float skeletonScale = 0.01f;
    public string previewRootName = "KimodoBVHPreview";
    public bool autoPlay = true;
    public bool clearPreviousPreview = true;

    [Header("Idle Reset")]
    [Tooltip("When enabled, skeleton returns to initial pose whenever no animation is playing.")]
    public bool resetToInitialPoseWhenIdle = true;

    [Header("Visualization")]
    public bool buildJointSpheres = true;
    [Tooltip("Joint sphere diameter in meters. 0.05 = 5 cm.")]
    public float jointSphereDiameter = 0.06f;
    public Material jointMaterial;
    public Color jointColor = new Color(0.1f, 0.85f, 0.55f, 1f);

    public bool buildBoneCylinders = true;
    [Tooltip("Cylinder scale x/z.")]
    public float boneCylinderXZ = 0.03f;
    public Material boneCylinderMaterial;
    public Color boneCylinderColor = new Color(0.45f, 0.62f, 0.95f, 1f);

    [Header("Output")]
    public GameObject builtRoot;
    public AnimationClip builtClip;

    private BVHParser parser;
    private bool idlePoseApplied;

    [Serializable]
    private class BoneRuntime
    {
        public BVHParser.BVHBone bvh;
        public Transform transform;
        public string path;
        public Vector3 initialLocalPosition;
        public Quaternion initialLocalRotation;
    }

    private class BoneSegmentTrack
    {
        public Transform parent;
        public Transform child;
        public Transform segment;
    }

    private readonly List<BoneRuntime> bones = new List<BoneRuntime>();
    private readonly List<BoneSegmentTrack> boneSegmentTracks = new List<BoneSegmentTrack>();

    public void BuildPreviewFromFile()
    {
        if (string.IsNullOrWhiteSpace(bvhFilePath))
        {
            throw new InvalidOperationException("bvhFilePath is empty.");
        }
        if (!File.Exists(bvhFilePath))
        {
            throw new FileNotFoundException("BVH file not found.", bvhFilePath);
        }

        string bvhText = File.ReadAllText(bvhFilePath);
        BuildPreviewFromText(bvhText);
    }

    public void BuildPreviewFromText(string bvhText)
    {
        if (string.IsNullOrWhiteSpace(bvhText))
        {
            throw new InvalidOperationException("BVH text is empty.");
        }

        parser = respectBVHTime ? new BVHParser(bvhText) : new BVHParser(bvhText, 1f / Mathf.Max(1f, frameRate));
        frameRate = 1f / Mathf.Max(1e-6f, parser.frameTime);

        if (clearPreviousPreview && builtRoot != null)
        {
            DestroySafe(builtRoot);
            builtRoot = null;
        }

        bones.Clear();
        boneSegmentTracks.Clear();
        idlePoseApplied = false;

        builtRoot = new GameObject(string.IsNullOrWhiteSpace(previewRootName) ? "KimodoBVHPreview" : previewRootName);
        builtRoot.transform.SetParent(transform, false);

        BuildSkeletonRecursive(parser.root, builtRoot.transform, builtRoot.transform.name);
        builtClip = BuildAnimationClip();

        Animation anim = builtRoot.GetComponent<Animation>();
        if (anim == null)
        {
            anim = builtRoot.AddComponent<Animation>();
        }

        string clipName = string.IsNullOrWhiteSpace(builtClip.name) ? "KimodoBVHClip" : builtClip.name;
        anim.AddClip(builtClip, clipName);
        anim.clip = builtClip;
        anim.playAutomatically = autoPlay;

        UpdateBoneSegments();

        if (autoPlay)
        {
            anim.Play(clipName);
        }
        else
        {
            RestoreInitialPose();
            idlePoseApplied = true;
        }
    }

    private void LateUpdate()
    {
        UpdateBoneSegments();

        if (!resetToInitialPoseWhenIdle || builtRoot == null)
        {
            return;
        }

        Animation anim = builtRoot.GetComponent<Animation>();
        bool isPlaying = anim != null && anim.isPlaying;

        if (isPlaying)
        {
            idlePoseApplied = false;
            return;
        }

        if (!idlePoseApplied)
        {
            RestoreInitialPose();
            idlePoseApplied = true;
        }
    }

    public void RestoreInitialPose()
    {
        for (int i = 0; i < bones.Count; i++)
        {
            BoneRuntime b = bones[i];
            if (b == null || b.transform == null)
            {
                continue;
            }
            b.transform.localPosition = b.initialLocalPosition;
            b.transform.localRotation = b.initialLocalRotation;
        }
        UpdateBoneSegments();
    }

    private void BuildSkeletonRecursive(BVHParser.BVHBone node, Transform parent, string parentPath)
    {
        GameObject go = new GameObject(node.name);
        go.transform.SetParent(parent, false);

        ApplyInitialSkeletonPose(node, go.transform);

        if (buildJointSpheres)
        {
            AddJointSphere(go.transform);
        }

        if (buildBoneCylinders && parent != builtRoot.transform)
        {
            AddBoneCylinder(parent, go.transform);
        }

        string path = parent == builtRoot.transform ? node.name : parentPath + "/" + node.name;
        bones.Add(new BoneRuntime
        {
            bvh = node,
            transform = go.transform,
            path = path,
            initialLocalPosition = go.transform.localPosition,
            initialLocalRotation = go.transform.localRotation,
        });

        foreach (BVHParser.BVHBone child in node.children)
        {
            BuildSkeletonRecursive(child, go.transform, path);
        }
    }

    private void ApplyInitialSkeletonPose(BVHParser.BVHBone bone, Transform t)
    {
        t.localPosition = ConvertOffset(bone.offsetX, bone.offsetY, bone.offsetZ) * skeletonScale;
        t.localRotation = Quaternion.identity;
        t.localScale = Vector3.one;
    }

    private void AddJointSphere(Transform joint)
    {
        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "JointViz";
        sphere.transform.SetParent(joint, false);
        sphere.transform.localPosition = Vector3.zero;
        sphere.transform.localRotation = Quaternion.identity;
        sphere.transform.localScale = Vector3.one * Mathf.Max(0.001f, jointSphereDiameter);

        Collider col = sphere.GetComponent<Collider>();
        if (col != null)
        {
            DestroySafe(col);
        }

        MeshRenderer mr = sphere.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            if (jointMaterial != null)
            {
                mr.sharedMaterial = jointMaterial;
            }
            else if (mr.sharedMaterial != null)
            {
                mr.sharedMaterial.color = jointColor;
            }
        }
    }

    private void AddBoneCylinder(Transform parent, Transform child)
    {
        GameObject cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        cyl.name = "BoneSegment";
        cyl.transform.SetParent(parent, true);

        Collider col = cyl.GetComponent<Collider>();
        if (col != null)
        {
            DestroySafe(col);
        }

        MeshRenderer mr = cyl.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            if (boneCylinderMaterial != null)
            {
                mr.sharedMaterial = boneCylinderMaterial;
            }
            else if (mr.sharedMaterial != null)
            {
                mr.sharedMaterial.color = boneCylinderColor;
            }
        }

        boneSegmentTracks.Add(new BoneSegmentTrack
        {
            parent = parent,
            child = child,
            segment = cyl.transform,
        });
    }

    private void UpdateBoneSegments()
    {
        if (boneSegmentTracks.Count == 0)
        {
            return;
        }

        float xz = Mathf.Max(0.0001f, boneCylinderXZ);

        for (int i = 0; i < boneSegmentTracks.Count; i++)
        {
            BoneSegmentTrack t = boneSegmentTracks[i];
            if (t == null || t.segment == null || t.parent == null || t.child == null)
            {
                continue;
            }

            Vector3 a = t.parent.position;
            Vector3 b = t.child.position;
            Vector3 d = b - a;
            float len = d.magnitude * 0.5f;

            t.segment.position = (a + b) * 0.5f;
            if (len > 1e-6f)
            {
                t.segment.rotation = Quaternion.FromToRotation(Vector3.up, d / len);
            }
            t.segment.localScale = new Vector3(xz, Mathf.Max(0.0001f, len), xz);
        }
    }

    private AnimationClip BuildAnimationClip()
    {
        if (parser == null)
        {
            throw new InvalidOperationException("BVH parser is null.");
        }

        int frames = parser.frames;
        if (frames <= 0)
        {
            throw new InvalidOperationException("BVH has zero frames.");
        }

        AnimationClip clip = new AnimationClip();
        clip.legacy = true;
        clip.frameRate = frameRate;

        string clipName = Path.GetFileNameWithoutExtension(bvhFilePath);
        clip.name = string.IsNullOrWhiteSpace(clipName) ? "KimodoBVHClip" : clipName;

        foreach (BoneRuntime bone in bones)
        {
            BuildCurvesForBone(clip, bone, frames);
        }

        clip.EnsureQuaternionContinuity();
        return clip;
    }

    private void BuildCurvesForBone(AnimationClip clip, BoneRuntime bone, int frames)
    {
        float[] posX = GetChannelValues(bone.bvh, 0);
        float[] posY = GetChannelValues(bone.bvh, 1);
        float[] posZ = GetChannelValues(bone.bvh, 2);

        bool hasPos = posX != null && posY != null && posZ != null;
        bool hasRot = HasRotationChannels(bone.bvh);

        if (hasPos)
        {
            Keyframe[] kx = new Keyframe[frames];
            Keyframe[] ky = new Keyframe[frames];
            Keyframe[] kz = new Keyframe[frames];

            for (int i = 0; i < frames; i++)
            {
                float t = i / frameRate;
                Vector3 p = ConvertPosition(posX[i], posY[i], posZ[i]) * skeletonScale;
                kx[i] = new Keyframe(t, p.x);
                ky[i] = new Keyframe(t, p.y);
                kz[i] = new Keyframe(t, p.z);
            }

            clip.SetCurve(bone.path, typeof(Transform), "localPosition.x", new AnimationCurve(kx));
            clip.SetCurve(bone.path, typeof(Transform), "localPosition.y", new AnimationCurve(ky));
            clip.SetCurve(bone.path, typeof(Transform), "localPosition.z", new AnimationCurve(kz));
        }

        if (hasRot)
        {
            Keyframe[] qx = new Keyframe[frames];
            Keyframe[] qy = new Keyframe[frames];
            Keyframe[] qz = new Keyframe[frames];
            Keyframe[] qw = new Keyframe[frames];

            for (int i = 0; i < frames; i++)
            {
                float t = i / frameRate;
                Quaternion q = EvaluateLocalRotation(bone.bvh, i);
                qx[i] = new Keyframe(t, q.x);
                qy[i] = new Keyframe(t, q.y);
                qz[i] = new Keyframe(t, q.z);
                qw[i] = new Keyframe(t, q.w);
            }

            clip.SetCurve(bone.path, typeof(Transform), "localRotation.x", new AnimationCurve(qx));
            clip.SetCurve(bone.path, typeof(Transform), "localRotation.y", new AnimationCurve(qy));
            clip.SetCurve(bone.path, typeof(Transform), "localRotation.z", new AnimationCurve(qz));
            clip.SetCurve(bone.path, typeof(Transform), "localRotation.w", new AnimationCurve(qw));
        }
    }

    private static float[] GetChannelValues(BVHParser.BVHBone bone, int channelId)
    {
        if (bone.channels == null || channelId < 0 || channelId >= bone.channels.Length)
        {
            return null;
        }
        if (!bone.channels[channelId].enabled)
        {
            return null;
        }
        return bone.channels[channelId].values;
    }

    private static bool HasRotationChannels(BVHParser.BVHBone bone)
    {
        return GetChannelValues(bone, 3) != null && GetChannelValues(bone, 4) != null && GetChannelValues(bone, 5) != null;
    }

    private Quaternion EvaluateLocalRotation(BVHParser.BVHBone bone, int frame)
    {
        Quaternion q = Quaternion.identity;

        int count = Mathf.Clamp(bone.channelNumber, 0, bone.channelOrder != null ? bone.channelOrder.Length : 0);
        for (int i = 0; i < count; i++)
        {
            int channelId = bone.channelOrder[i];
            if (channelId < 3 || channelId > 5)
            {
                continue;
            }

            float[] values = GetChannelValues(bone, channelId);
            if (values == null || frame < 0 || frame >= values.Length)
            {
                continue;
            }

            float angle = WrapAngle(values[frame]);
            Quaternion axisRot;
            switch (channelId)
            {
                case 3: axisRot = Quaternion.AngleAxis(angle, Vector3.right); break;
                case 4: axisRot = Quaternion.AngleAxis(angle, Vector3.up); break;
                case 5: axisRot = Quaternion.AngleAxis(angle, Vector3.forward); break;
                default: axisRot = Quaternion.identity; break;
            }

            // Respect BVH channel order as listed in the file.
            q = q * axisRot;
        }

        return ConvertRotationQuaternion(q);
    }

    private Vector3 ConvertOffset(float x, float y, float z)
    {
        if (useLegacyConversion)
        {
            if (blender) return new Vector3(-x, z, -y);
            return new Vector3(-x, y, z);
        }

        if (useKimodoSpaceConversion)
        {
            return new Vector3(-x, y, z);
        }

        return new Vector3(x, y, z);
    }

    private Vector3 ConvertPosition(float x, float y, float z)
    {
        if (useLegacyConversion)
        {
            if (blender) return new Vector3(-x, z, -y);
            return new Vector3(-x, y, z);
        }

        if (useKimodoSpaceConversion)
        {
            return new Vector3(-x, y, z);
        }

        return new Vector3(x, y, z);
    }

    private Quaternion ConvertRotationQuaternion(Quaternion src)
    {
        if (useLegacyConversion)
        {
            if (blender)
            {
                return new Quaternion(src.x, -src.z, src.y, src.w);
            }
            return new Quaternion(src.x, -src.y, -src.z, src.w);
        }

        if (useKimodoSpaceConversion)
        {
            return new Quaternion(src.x, -src.y, -src.z, src.w);
        }

        return src;
    }

    private static float WrapAngle(float a)
    {
        if (a > 180f) return a - 360f;
        if (a < -180f) return 360f + a;
        return a;
    }

    private static void DestroySafe(UnityEngine.Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying) Destroy(obj);
        else DestroyImmediate(obj);
    }
}
