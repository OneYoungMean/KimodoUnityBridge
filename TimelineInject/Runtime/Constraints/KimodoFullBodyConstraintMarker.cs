
using System;

[Serializable]
public sealed class KimodoFullBodyConstraintMarker : KimodoConstraintMarkerBase
{
    public override string ConstraintType => "fullbody";
}

[Serializable]
public sealed class KimodoUntypedConstraintMarker : KimodoConstraintMarkerBase
{
    public override string ConstraintType => "untyped";
}
