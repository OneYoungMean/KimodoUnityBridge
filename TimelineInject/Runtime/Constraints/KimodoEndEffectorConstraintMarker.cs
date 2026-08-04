using System;

[Serializable]
[UnityEngine.Timeline.HideInMenu]
public class KimodoEndEffectorConstraintMarker : KimodoConstraintMarkerBase
{
    public override string ConstraintType => "end-effector";
}
