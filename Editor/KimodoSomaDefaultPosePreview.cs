using System;
using System.Collections.Generic;
using UnityEngine;

namespace KimodoUnityMotionTools
{
    public class KimodoSomaDefaultPosePreview : MonoBehaviour
    {
        [Header("Skeleton")]
        [Tooltip("Use SOMA full skeleton (77 joints). Off = compact SOMA30.")]
        public bool useSoma77 = false;

        [Header("Build")]
        [Tooltip("Scale applied to neutral joint positions.")]
        public float skeletonScale = 1.0f;
        public string previewRootName = "KimodoSomaDefaultPosePreview";
        public bool clearPreviousPreview = true;

        [Header("Visualization")]
        public bool buildJointSpheres = true;
        [Tooltip("Joint sphere diameter in meters.")]
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

        [Serializable]
        private class JointDef
        {
            public string Name;
            public int Parent;
            public Vector3 WorldPos;
        }

        private class BoneSegmentTrack
        {
            public Transform Parent;
            public Transform Child;
            public Transform Segment;
        }

        private readonly List<BoneSegmentTrack> boneSegmentTracks = new List<BoneSegmentTrack>();

        private static readonly (string Name, string Parent)[] Soma30Topology = new (string Name, string Parent)[]
        {
            ("Hips", null),
            ("Spine1", "Hips"),
            ("Spine2", "Spine1"),
            ("Chest", "Spine2"),
            ("Neck1", "Chest"),
            ("Neck2", "Neck1"),
            ("Head", "Neck2"),
            ("Jaw", "Head"),
            ("LeftEye", "Head"),
            ("RightEye", "Head"),
            ("LeftShoulder", "Chest"),
            ("LeftArm", "LeftShoulder"),
            ("LeftForeArm", "LeftArm"),
            ("LeftHand", "LeftForeArm"),
            ("LeftHandThumbEnd", "LeftHand"),
            ("LeftHandMiddleEnd", "LeftHand"),
            ("RightShoulder", "Chest"),
            ("RightArm", "RightShoulder"),
            ("RightForeArm", "RightArm"),
            ("RightHand", "RightForeArm"),
            ("RightHandThumbEnd", "RightHand"),
            ("RightHandMiddleEnd", "RightHand"),
            ("LeftLeg", "Hips"),
            ("LeftShin", "LeftLeg"),
            ("LeftFoot", "LeftShin"),
            ("LeftToeBase", "LeftFoot"),
            ("RightLeg", "Hips"),
            ("RightShin", "RightLeg"),
            ("RightFoot", "RightShin"),
            ("RightToeBase", "RightFoot"),
        };

        private static readonly (string Name, string Parent)[] Soma77Topology = new (string Name, string Parent)[]
        {
            ("Hips", null),
            ("Spine1", "Hips"),
            ("Spine2", "Spine1"),
            ("Chest", "Spine2"),
            ("Neck1", "Chest"),
            ("Neck2", "Neck1"),
            ("Head", "Neck2"),
            ("HeadEnd", "Head"),
            ("Jaw", "Head"),
            ("LeftEye", "Head"),
            ("RightEye", "Head"),
            ("LeftShoulder", "Chest"),
            ("LeftArm", "LeftShoulder"),
            ("LeftForeArm", "LeftArm"),
            ("LeftHand", "LeftForeArm"),
            ("LeftHandThumb1", "LeftHand"),
            ("LeftHandThumb2", "LeftHandThumb1"),
            ("LeftHandThumb3", "LeftHandThumb2"),
            ("LeftHandThumbEnd", "LeftHandThumb3"),
            ("LeftHandIndex1", "LeftHand"),
            ("LeftHandIndex2", "LeftHandIndex1"),
            ("LeftHandIndex3", "LeftHandIndex2"),
            ("LeftHandIndex4", "LeftHandIndex3"),
            ("LeftHandIndexEnd", "LeftHandIndex4"),
            ("LeftHandMiddle1", "LeftHand"),
            ("LeftHandMiddle2", "LeftHandMiddle1"),
            ("LeftHandMiddle3", "LeftHandMiddle2"),
            ("LeftHandMiddle4", "LeftHandMiddle3"),
            ("LeftHandMiddleEnd", "LeftHandMiddle4"),
            ("LeftHandRing1", "LeftHand"),
            ("LeftHandRing2", "LeftHandRing1"),
            ("LeftHandRing3", "LeftHandRing2"),
            ("LeftHandRing4", "LeftHandRing3"),
            ("LeftHandRingEnd", "LeftHandRing4"),
            ("LeftHandPinky1", "LeftHand"),
            ("LeftHandPinky2", "LeftHandPinky1"),
            ("LeftHandPinky3", "LeftHandPinky2"),
            ("LeftHandPinky4", "LeftHandPinky3"),
            ("LeftHandPinkyEnd", "LeftHandPinky4"),
            ("RightShoulder", "Chest"),
            ("RightArm", "RightShoulder"),
            ("RightForeArm", "RightArm"),
            ("RightHand", "RightForeArm"),
            ("RightHandThumb1", "RightHand"),
            ("RightHandThumb2", "RightHandThumb1"),
            ("RightHandThumb3", "RightHandThumb2"),
            ("RightHandThumbEnd", "RightHandThumb3"),
            ("RightHandIndex1", "RightHand"),
            ("RightHandIndex2", "RightHandIndex1"),
            ("RightHandIndex3", "RightHandIndex2"),
            ("RightHandIndex4", "RightHandIndex3"),
            ("RightHandIndexEnd", "RightHandIndex4"),
            ("RightHandMiddle1", "RightHand"),
            ("RightHandMiddle2", "RightHandMiddle1"),
            ("RightHandMiddle3", "RightHandMiddle2"),
            ("RightHandMiddle4", "RightHandMiddle3"),
            ("RightHandMiddleEnd", "RightHandMiddle4"),
            ("RightHandRing1", "RightHand"),
            ("RightHandRing2", "RightHandRing1"),
            ("RightHandRing3", "RightHandRing2"),
            ("RightHandRing4", "RightHandRing3"),
            ("RightHandRingEnd", "RightHandRing4"),
            ("RightHandPinky1", "RightHand"),
            ("RightHandPinky2", "RightHandPinky1"),
            ("RightHandPinky3", "RightHandPinky2"),
            ("RightHandPinky4", "RightHandPinky3"),
            ("RightHandPinkyEnd", "RightHandPinky4"),
            ("LeftLeg", "Hips"),
            ("LeftShin", "LeftLeg"),
            ("LeftFoot", "LeftShin"),
            ("LeftToeBase", "LeftFoot"),
            ("LeftToeEnd", "LeftToeBase"),
            ("RightLeg", "Hips"),
            ("RightShin", "RightLeg"),
            ("RightFoot", "RightShin"),
            ("RightToeBase", "RightFoot"),
            ("RightToeEnd", "RightToeBase"),
        };

        public void BuildPreview()
        {
            if (clearPreviousPreview && builtRoot != null)
            {
                DestroySafe(builtRoot);
                builtRoot = null;
            }

            boneSegmentTracks.Clear();
            builtRoot = new GameObject(string.IsNullOrWhiteSpace(previewRootName) ? "KimodoSomaDefaultPosePreview" : previewRootName);
            builtRoot.transform.SetParent(transform, false);

            List<JointDef> joints = BuildDefaultJointWorldPositions(useSoma77);
            GameObject[] nodes = new GameObject[joints.Count];

            for (int i = 0; i < joints.Count; i++)
            {
                nodes[i] = new GameObject(joints[i].Name);
            }

            for (int i = 0; i < joints.Count; i++)
            {
                JointDef jd = joints[i];
                Transform t = nodes[i].transform;
                if (jd.Parent >= 0 && jd.Parent < joints.Count)
                {
                    t.SetParent(nodes[jd.Parent].transform, false);
                    t.localPosition = (jd.WorldPos - joints[jd.Parent].WorldPos) * skeletonScale;
                }
                else
                {
                    t.SetParent(builtRoot.transform, false);
                    t.localPosition = jd.WorldPos * skeletonScale;
                }

                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;

                if (buildJointSpheres)
                {
                    AddJointSphere(t);
                }

                if (buildBoneCylinders && t.parent != builtRoot.transform)
                {
                    AddBoneCylinder(t.parent, t);
                }
            }

            UpdateBoneSegments();
            Debug.Log($"[Kimodo] Built SOMA default-pose preview ({(useSoma77 ? "77" : "30")} joints).");
        }

        public void ClearPreview()
        {
            if (builtRoot == null)
            {
                return;
            }
            DestroySafe(builtRoot);
            builtRoot = null;
            boneSegmentTracks.Clear();
        }

        private static List<JointDef> BuildDefaultJointWorldPositions(bool use77)
        {
            var topo = use77 ? Soma77Topology : Soma30Topology;
            var list = new List<JointDef>(topo.Length);
            var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < topo.Length; i++)
            {
                indexByName[topo[i].Name] = i;
                list.Add(new JointDef
                {
                    Name = topo[i].Name,
                    Parent = -1,
                    WorldPos = Vector3.zero,
                });
            }

            for (int i = 0; i < topo.Length; i++)
            {
                string parentName = topo[i].Parent;
                list[i].Parent = string.IsNullOrEmpty(parentName) || !indexByName.TryGetValue(parentName, out int pidx) ? -1 : pidx;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Parent >= 0)
                {
                    list[i].WorldPos = list[list[i].Parent].WorldPos + GuessLocalOffset(list[i].Name);
                }
            }

            return list;
        }

        private static Vector3 GuessLocalOffset(string jointName)
        {
            if (string.IsNullOrEmpty(jointName))
            {
                return Vector3.zero;
            }

            string n = jointName.ToLowerInvariant();
            bool isLeft = n.Contains("left");
            bool isRight = n.Contains("right");
            float side = isLeft ? -1f : (isRight ? 1f : 0f);

            if (n.Contains("spine1")) return new Vector3(0f, 0.12f, 0f);
            if (n.Contains("spine2")) return new Vector3(0f, 0.12f, 0f);
            if (n.Contains("chest")) return new Vector3(0f, 0.12f, 0f);
            if (n.Contains("neck1")) return new Vector3(0f, 0.08f, 0f);
            if (n.Contains("neck2")) return new Vector3(0f, 0.08f, 0f);
            if (n == "head") return new Vector3(0f, 0.10f, 0f);
            if (n.Contains("headend")) return new Vector3(0f, 0.08f, 0f);
            if (n.Contains("jaw")) return new Vector3(0f, -0.05f, 0.05f);
            if (n.Contains("eye")) return new Vector3(side * 0.03f, 0.03f, 0.08f);

            if (n.Contains("shoulder")) return new Vector3(side * 0.10f, 0.05f, 0f);
            if (n.Contains("arm") && !n.Contains("fore")) return new Vector3(side * 0.16f, 0f, 0f);
            if (n.Contains("forearm")) return new Vector3(side * 0.20f, 0f, 0f);
            if (n.Contains("hand") && !n.Contains("thumb") && !n.Contains("index") && !n.Contains("middle") && !n.Contains("ring") && !n.Contains("pinky"))
                return new Vector3(side * 0.12f, 0f, 0f);
            if (n.Contains("thumb")) return new Vector3(side * 0.03f, -0.01f, 0.03f);
            if (n.Contains("index")) return new Vector3(side * 0.035f, 0f, 0.05f);
            if (n.Contains("middle")) return new Vector3(side * 0.03f, 0f, 0.06f);
            if (n.Contains("ring")) return new Vector3(side * 0.028f, 0f, 0.05f);
            if (n.Contains("pinky")) return new Vector3(side * 0.025f, 0f, 0.04f);

            if (n.Contains("leg") && !n.Contains("shin")) return new Vector3(side * 0.10f, -0.12f, 0f);
            if (n.Contains("shin")) return new Vector3(0f, -0.40f, 0f);
            if (n.Contains("foot")) return new Vector3(0f, -0.40f, 0.05f);
            if (n.Contains("toebase")) return new Vector3(0f, 0f, 0.16f);
            if (n.Contains("toeend")) return new Vector3(0f, 0f, 0.10f);

            return Vector3.zero;
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
                Parent = parent,
                Child = child,
                Segment = cyl.transform,
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
                if (t == null || t.Segment == null || t.Parent == null || t.Child == null)
                {
                    continue;
                }

                Vector3 a = t.Parent.position;
                Vector3 b = t.Child.position;
                Vector3 d = b - a;
                float len = d.magnitude * 0.5f;

                t.Segment.position = (a + b) * 0.5f;
                if (len > 1e-6f)
                {
                    t.Segment.rotation = Quaternion.FromToRotation(Vector3.up, d / len);
                }
                t.Segment.localScale = new Vector3(xz, Mathf.Max(0.0001f, len), xz);
            }
        }

        private static void DestroySafe(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }
    }
}
