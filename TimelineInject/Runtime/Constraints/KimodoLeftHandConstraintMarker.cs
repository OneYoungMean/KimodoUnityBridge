
using System;
using UnityEngine.Timeline;

[Serializable]
[HideInMenu]
public sealed class KimodoLeftHandConstraintMarker : KimodoEndEffectorConstraintMarker
{
    public override string ConstraintType => "left-hand";
}
