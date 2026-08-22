using System;
using UnityEngine;

namespace CharacterAnimationCli.Unity
{
    /// <summary>Minimal position/rotation value with no hierarchy or IK semantics.</summary>
    [Serializable]
    public sealed class KimodoRigidTransform
    {
        public Vector3 position;
        public Quaternion rotation;

        public Vector3 t
        {
            get => position;
            set => position = value;
        }

        public Quaternion q
        {
            get => rotation;
            set => rotation = value;
        }

        public static KimodoRigidTransform Identity => new KimodoRigidTransform
        {
            position = Vector3.zero,
            rotation = Quaternion.identity
        };

        public KimodoRigidTransform Clone() => new KimodoRigidTransform
        {
            position = position,
            rotation = rotation
        };
    }
}
