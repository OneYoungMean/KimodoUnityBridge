using System;
using System.Collections.Generic;
using KimodoUnityMotionTools.ProjectEditor.Manager;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor
{
    [InitializeOnLoad]
    internal static class KimodoConstraintOverrideEditSession
    {
        private static readonly Dictionary<int, SessionData> Sessions = new Dictionary<int, SessionData>();

        static KimodoConstraintOverrideEditSession()
        {
            AssemblyReloadEvents.beforeAssemblyReload += EndAllWithoutCommit;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += EndAllWithoutCommit;
            EditorApplication.update += OnEditorUpdate;
        }

        internal static bool TryBegin(KimodoConstraintMarkerBase marker, out string error)
        {
            error = string.Empty;
            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            int key = marker.GetInstanceID();
            if (Sessions.ContainsKey(key))
            {
                KimodoConstraintOverrideEditWindow.ShowWindow(marker);
                return true;
            }

            if (!TryBuildSession(marker, out SessionData session, out error))
            {
                return false;
            }

            Sessions[key] = session;
            KimodoConstraintOverrideEditWindow.ShowWindow(marker);
            KimodoConstraintMarkerEditorUtility.NotifyInspectorChanged(marker);
            return true;
        }

        internal static bool HasActiveSession(KimodoConstraintMarkerBase marker)
        {
            return marker != null && Sessions.ContainsKey(marker.GetInstanceID());
        }

        internal static bool TryGetSession(KimodoConstraintMarkerBase marker, out SessionData session)
        {
            session = null;
            return marker != null && Sessions.TryGetValue(marker.GetInstanceID(), out session) && session != null;
        }

        internal static bool TryCommit(KimodoConstraintMarkerBase marker, out string error)
        {
            error = string.Empty;
            if (!TryGetSession(marker, out SessionData session))
            {
                error = "session not found";
                return false;
            }

            if (!CapturePoseAndWriteBack(session, out error))
            {
                return false;
            }

            session.Marker.useOverride = true;
            EditorUtility.SetDirty(session.Marker);
            AssetDatabase.SaveAssets();
            EndSession(session, keepMarkerChanges: true);
            return true;
        }

        internal static void Cancel(KimodoConstraintMarkerBase marker)
        {
            if (!TryGetSession(marker, out SessionData session))
            {
                return;
            }

            EndSession(session, keepMarkerChanges: false);
        }

        internal static void PingSession(KimodoConstraintMarkerBase marker)
        {
            if (!TryGetSession(marker, out SessionData session))
            {
                return;
            }

            session.LastPingTime = EditorApplication.timeSinceStartup;
        }

        internal static string DescribeMarker(KimodoConstraintMarkerBase marker)
        {
            if (marker == null)
            {
                return "(null)";
            }

            return $"{marker.name} ({marker.ConstraintType}) @ {marker.time:F3}s";
        }

        private static bool TryBuildSession(KimodoConstraintMarkerBase marker, out SessionData session, out string error)
        {
            session = null;
            error = string.Empty;

            if (!KimodoConstraintMarkerEditorUtility.TryGetClipRangeForMarker(marker, out TimelineClip clipRange) || clipRange == null)
            {
                error = "clip range not found";
                return false;
            }

            if (!(clipRange.asset is KimodoPlayableClip playableClip))
            {
                error = "clip asset is not KimodoPlayableClip";
                return false;
            }

            if (!KimodoConstraintOverridePoseUtility.TryBuildUnityPoseFromMarker(marker, out KimodoMarkerSampleResult initialPose, out error))
            {
                return false;
            }

            if (!KimodoConstraintSnapshotVisualizer.TryCreateStandalonePreviewRigForModel(
                    playableClip.bridgeModelName,
                    "__KimodoOverrideEditRig",
                    out GameObject previewRig,
                    out Transform[] transforms,
                    out error))
            {
                return false;
            }

            string serializedMarkerSnapshot = EditorJsonUtility.ToJson(marker);
            if (string.IsNullOrWhiteSpace(serializedMarkerSnapshot))
            {
                UnityEngine.Object.DestroyImmediate(previewRig);
                error = "failed to snapshot marker";
                return false;
            }

            if (!ApplyUnityPoseToTransforms(transforms, initialPose, out error))
            {
                UnityEngine.Object.DestroyImmediate(previewRig);
                return false;
            }

            marker.useOverride = true;
            EditorUtility.SetDirty(marker);

            EnsureAnimationModeOn();
            Selection.activeObject = previewRig;
            SceneView.RepaintAll();

            session = new SessionData
            {
                Marker = marker,
                ClipRange = clipRange,
                PlayableClip = playableClip,
                PreviewRig = previewRig,
                RigTransforms = transforms,
                SerializedMarkerSnapshot = serializedMarkerSnapshot,
                LastPoseSignature = ComputePoseSignature(transforms),
                LastPingTime = EditorApplication.timeSinceStartup
            };
            return true;
        }

        private static void OnEditorUpdate()
        {
            if (Sessions.Count == 0)
            {
                return;
            }

            var removed = new List<int>();
            foreach (KeyValuePair<int, SessionData> kv in Sessions)
            {
                SessionData session = kv.Value;
                if (session == null || session.Marker == null || session.PreviewRig == null)
                {
                    removed.Add(kv.Key);
                    continue;
                }

                if (!KimodoConstraintOverrideEditWindow.IsOpenForMarker(session.Marker))
                {
                    EndSession(session, keepMarkerChanges: false, removeFromMap: false);
                    removed.Add(kv.Key);
                    continue;
                }

                if (!CapturePoseAndWriteBack(session, out _))
                {
                    continue;
                }
            }

            for (int i = 0; i < removed.Count; i++)
            {
                Sessions.Remove(removed[i]);
            }
        }

        private static bool CapturePoseAndWriteBack(SessionData session, out string error)
        {
            error = string.Empty;
            if (session?.Marker == null || session.RigTransforms == null)
            {
                error = "session is invalid";
                return false;
            }

            int currentSig = ComputePoseSignature(session.RigTransforms);
            if (currentSig == session.LastPoseSignature)
            {
                return true;
            }

            session.LastPoseSignature = currentSig;

            bool captureHeading = session.Marker is KimodoRoot2DConstraintMarker root2D && root2D.includeGlobalHeading;
            if (!KimodoConstraintOverridePoseUtility.TryCapturePoseFromRig(session.RigTransforms, captureHeading, out KimodoMarkerSampleResult pose, out error))
            {
                return false;
            }

            if (!KimodoConstraintOverridePoseUtility.TryWriteUnityPoseToMarker(session.Marker, pose, out error))
            {
                return false;
            }

            KimodoEditorCommandManager.Dispatch(new ConstraintSnapshotRefreshCommand());
            SceneView.RepaintAll();
            return true;
        }

        private static bool ApplyUnityPoseToTransforms(Transform[] transforms, KimodoMarkerSampleResult pose, out string error)
        {
            error = string.Empty;
            if (transforms == null || transforms.Length == 0 || pose == null)
            {
                error = "invalid pose or transforms";
                return false;
            }

            transforms[0].position = pose.rootPosition;

            int count = Mathf.Min(transforms.Length, pose.localAxisAngles != null ? pose.localAxisAngles.Count : 0);
            if (count <= 0)
            {
                return true;
            }

            for (int i = 0; i < count; i++)
            {
                Quaternion q = Quaternion.identity;
                Vector3 aa = pose.localAxisAngles[i];
                float angleRad = aa.magnitude;
                if (angleRad > 1e-8f)
                {
                    q = Quaternion.AngleAxis(angleRad * Mathf.Rad2Deg, aa / angleRad);
                }

                transforms[i].localRotation = q;
            }

            return true;
        }

        private static int ComputePoseSignature(Transform[] transforms)
        {
            unchecked
            {
                int hash = 486187739;
                if (transforms == null)
                {
                    return hash;
                }

                for (int i = 0; i < transforms.Length; i++)
                {
                    Transform t = transforms[i];
                    if (t == null)
                    {
                        hash = hash * 31 + 17;
                        continue;
                    }

                    Vector3 p = t.localPosition;
                    Quaternion r = t.localRotation;
                    hash = hash * 31 + Quantize(p.x);
                    hash = hash * 31 + Quantize(p.y);
                    hash = hash * 31 + Quantize(p.z);
                    hash = hash * 31 + Quantize(r.x);
                    hash = hash * 31 + Quantize(r.y);
                    hash = hash * 31 + Quantize(r.z);
                    hash = hash * 31 + Quantize(r.w);
                }

                return hash;
            }
        }

        private static int Quantize(float v)
        {
            return Mathf.RoundToInt(v * 100000f);
        }

        private static void EndAllWithoutCommit()
        {
            if (Sessions.Count == 0)
            {
                return;
            }

            var sessions = new List<SessionData>(Sessions.Values);
            Sessions.Clear();
            for (int i = 0; i < sessions.Count; i++)
            {
                EndSession(sessions[i], keepMarkerChanges: false, removeFromMap: false);
            }
        }

        private static void EndSession(SessionData session, bool keepMarkerChanges, bool removeFromMap = true)
        {
            if (session == null)
            {
                return;
            }

            if (session.Marker != null && !keepMarkerChanges && !string.IsNullOrWhiteSpace(session.SerializedMarkerSnapshot))
            {
                EditorJsonUtility.FromJsonOverwrite(session.SerializedMarkerSnapshot, session.Marker);
                EditorUtility.SetDirty(session.Marker);
            }

            if (session.PreviewRig != null)
            {
                UnityEngine.Object.DestroyImmediate(session.PreviewRig);
            }

            if (removeFromMap && session.Marker != null)
            {
                Sessions.Remove(session.Marker.GetInstanceID());
            }

            EnsureAnimationModeOffWhenNoSessions();
            KimodoConstraintMarkerEditorUtility.NotifyInspectorChanged(session.Marker);
            SceneView.RepaintAll();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange _)
        {
            EndAllWithoutCommit();
        }

        private static void EnsureAnimationModeOn()
        {
            if (!AnimationMode.InAnimationMode())
            {
                AnimationMode.StartAnimationMode();
            }
        }

        private static void EnsureAnimationModeOffWhenNoSessions()
        {
            if (Sessions.Count == 0 && AnimationMode.InAnimationMode())
            {
                AnimationMode.StopAnimationMode();
            }
        }

        internal sealed class SessionData
        {
            public KimodoConstraintMarkerBase Marker;
            public TimelineClip ClipRange;
            public KimodoPlayableClip PlayableClip;
            public GameObject PreviewRig;
            public Transform[] RigTransforms;
            public string SerializedMarkerSnapshot;
            public int LastPoseSignature;
            public double LastPingTime;
        }
    }
}
