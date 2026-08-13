
using System;
using UnityEngine.Timeline;

[Serializable]
[HideInMenu]
public sealed class KimodoFullBodyConstraintMarker : KimodoConstraintMarkerBase
{
    public override string ConstraintType => "fullbody";
}
