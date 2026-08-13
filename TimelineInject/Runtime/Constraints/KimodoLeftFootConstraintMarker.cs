using System;
using UnityEngine.Timeline;

[Serializable]
[HideInMenu]
public sealed class KimodoLeftFootConstraintMarker : KimodoEndEffectorConstraintMarker
{
    public override string ConstraintType => "left-foot";
}
