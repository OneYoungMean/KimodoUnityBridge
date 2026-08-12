using System;
using UnityEngine.Timeline;

[Serializable]
[HideInMenu]
public abstract class KimodoEndEffectorConstraintMarker : KimodoConstraintMarkerBase
{
    public override string ConstraintType => "end-effector";
}
