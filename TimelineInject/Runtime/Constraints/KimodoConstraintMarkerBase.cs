
using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

[Serializable]
public abstract class KimodoConstraintMarkerBase : Marker, TimelineInject.IKimodoConstraintPreviewSelectable
{
    [Tooltip("If disabled, this marker is ignored by preview, sampling, and generation.")]
    public bool constraintEnabled = true;
    [Tooltip("If enabled, use manually edited marker values. If disabled, values are sampled from timeline pose at this marker time.")]
    public bool useOverride;
    [SerializeField]
    private KimodoMarkerSampleResult sampleData = new KimodoMarkerSampleResult();

    public abstract string ConstraintType { get; }

    public bool ConstraintPreviewEnabled => constraintEnabled;
    public int ConstraintPreviewPriority => 0;
    public string ConstraintPreviewName => ConstraintType ?? "Constraint";

    public KimodoMarkerSampleResult SampleData
    {
        get
        {
            EnsureSampleData();
            return sampleData;
        }
        set
        {
            sampleData = value ?? new KimodoMarkerSampleResult();
            SyncConstraintType();
        }
    }

    protected void EnsureSampleData()
    {
        if (sampleData == null)
        {
            sampleData = new KimodoMarkerSampleResult();
        }

        SyncConstraintType();
    }

    private void SyncConstraintType()
    {
        if (sampleData != null)
        {
            sampleData.constraintType = ConstraintType;
            sampleData.mask = KimodoConstraintMask.Resolve(sampleData.mask, ConstraintType);
        }
    }

    public Vector3 kimodoRootPosition
    {
        get => SampleData.kimodoRootPosition;
        set => SampleData.kimodoRootPosition = value;
    }

    public Vector2 smoothRoot2D
    {
        get => new Vector2(kimodoRootPosition.x, kimodoRootPosition.z);
        set => kimodoRootPosition = new Vector3(value.x, kimodoRootPosition.y, value.y);
    }

    public Vector3 unityRootPos
    {
        get => SampleData.unityRootPos;
        set => SampleData.unityRootPos = value;
    }

    public Quaternion unityRootRot
    {
        get => SampleData.unityRootRot;
        set => SampleData.unityRootRot = value;
    }

    public bool includeGlobalHeading
    {
        get => SampleData.hasRootHeading;
        set => SampleData.hasRootHeading = value;
    }

    public Vector2 globalRootHeading
    {
        get => SampleData.rootHeading;
        set => SampleData.rootHeading = value;
    }

    public List<string> jointNames
    {
        get => SampleData.jointNames;
        set => SampleData.jointNames = value ?? new List<string>();
    }

    public List<Vector3> localJointRots
    {
        get => SampleData.localAxisAngles;
        set => SampleData.localAxisAngles = value ?? new List<Vector3>();
    }

    protected virtual void OnEnable()
    {
        EnsureSampleData();
    }

}

/// <summary>The only marker created by new tooling.  Older marker classes are
/// retained so existing Timeline assets remain readable.</summary>
[Serializable]
public sealed class KimodoConstraintMarker : KimodoConstraintMarkerBase
{
    public override string ConstraintType => "constraint";

    protected override void OnEnable()
    {
        if (SampleData.mask == null)
        {
            SampleData.mask = KimodoConstraintMask.ForType("fullbody");
        }
        base.OnEnable();
    }
}

[Serializable]
[HideInMenu]
public sealed class KimodoRoot2DConstraintMarker : KimodoConstraintMarkerBase
{
    public override string ConstraintType => "root2d";
}
