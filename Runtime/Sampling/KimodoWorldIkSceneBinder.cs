using System;
using UnityEngine;

namespace KimodoBridge
{
    /// <summary>
    /// Materializes serialized world IK data as short-lived scene transforms.
    /// The sampling job only consumes the resulting TransformSceneHandles.
    /// </summary>
    internal sealed class KimodoWorldIkSceneBinder : IDisposable
    {
        private readonly GameObject[] objects;

        internal KimodoRetargetClipSamplingUtility.HumanoidIkSceneTargets Targets { get; }

        private KimodoWorldIkSceneBinder(
            GameObject[] objects,
            KimodoRetargetClipSamplingUtility.HumanoidIkSceneTargets targets)
        {
            this.objects = objects;
            Targets = targets;
        }

        internal static KimodoWorldIkSceneBinder Create(
            KimodoRetargetClipSamplingUtility.HumanoidWorldIkTargets goals,
            out string error)
        {
            error = string.Empty;
            var objects = new GameObject[4];
            try
            {
                KimodoRetargetClipSamplingUtility.HumanoidIkSceneTargets targets = default;
                CreateTarget(goals.leftHand, goals.leftHandPosition, goals.leftHandRotation,
                    "__KimodoIkLeftHand", ref objects[0], ref targets.leftHandTransform, ref targets.leftHand);
                CreateTarget(goals.rightHand, goals.rightHandPosition, goals.rightHandRotation,
                    "__KimodoIkRightHand", ref objects[1], ref targets.rightHandTransform, ref targets.rightHand);
                CreateTarget(goals.leftFoot, goals.leftFootPosition, goals.leftFootRotation,
                    "__KimodoIkLeftFoot", ref objects[2], ref targets.leftFootTransform, ref targets.leftFoot);
                CreateTarget(goals.rightFoot, goals.rightFootPosition, goals.rightFootRotation,
                    "__KimodoIkRightFoot", ref objects[3], ref targets.rightFootTransform, ref targets.rightFoot);
                return new KimodoWorldIkSceneBinder(objects, targets);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Destroy(objects);
                return null;
            }
        }

        private static void CreateTarget(
            bool enabled,
            Vector3 position,
            Quaternion rotation,
            string name,
            ref GameObject targetObject,
            ref Transform targetTransform,
            ref bool targetEnabled)
        {
            if (!enabled) return;
            targetObject = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            targetTransform = targetObject.transform;
            targetTransform.SetPositionAndRotation(position, rotation);
            targetEnabled = true;
        }

        public void Dispose()
        {
            Destroy(objects);
        }

        private static void Destroy(GameObject[] objects)
        {
            if (objects == null) return;
            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] != null) UnityEngine.Object.DestroyImmediate(objects[i]);
            }
        }
    }
}
