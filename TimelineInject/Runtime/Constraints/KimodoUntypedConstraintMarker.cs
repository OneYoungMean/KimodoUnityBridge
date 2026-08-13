using System;
using UnityEngine.Timeline;

[Serializable]
[HideInMenu]
public sealed class KimodoUntypedConstraintMarker : KimodoConstraintMarkerBase
{
    public override string ConstraintType => "untyped";
}
