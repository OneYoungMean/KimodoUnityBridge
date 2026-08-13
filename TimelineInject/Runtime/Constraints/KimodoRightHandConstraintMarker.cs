
using System;
using UnityEngine.Timeline;

[Serializable]
[HideInMenu]
public sealed class KimodoRightHandConstraintMarker : KimodoEndEffectorConstraintMarker
{
    public override string ConstraintType => "right-hand";
}
