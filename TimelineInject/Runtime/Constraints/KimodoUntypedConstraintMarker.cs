using System;

[Serializable]
public sealed class KimodoUntypedConstraintMarker : KimodoConstraintMarkerBase
{
    public override string ConstraintType => "untyped";
}
