using System;
using UnityEngine.Timeline;

[Serializable]
[HideInMenu]
public sealed class KimodoRightFootConstraintMarker : KimodoEndEffectorConstraintMarker
{
    public override string ConstraintType => "right-foot";
}
