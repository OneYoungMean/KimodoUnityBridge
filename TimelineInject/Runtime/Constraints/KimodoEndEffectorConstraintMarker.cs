using System;

[Serializable]

public abstract class KimodoEndEffectorConstraintMarker : KimodoConstraintMarkerBase
{
    public override string ConstraintType => "end-effector";
}
