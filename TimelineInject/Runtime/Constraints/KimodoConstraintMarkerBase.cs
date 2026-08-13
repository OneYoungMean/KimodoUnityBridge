using System;
using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

[Serializable]
public sealed class KimodoConstraintMarker : Marker, IKimodoConstraintPreviewSelectable
{
    [Tooltip("If disabled, this marker is ignored by preview, sampling, and generation.")]
    public bool constraintEnabled = true;
    [Tooltip("If enabled, use manually edited marker values. If disabled, values are sampled from timeline pose at this marker time.")]
    public bool useOverride;
    [SerializeField] private KimodoMarkerSampleResult sampleData = new KimodoMarkerSampleResult();

    public string ConstraintType => "constraint";
    public bool ConstraintPreviewEnabled => constraintEnabled;
    public int ConstraintPreviewPriority => 0;
    public string ConstraintPreviewName => "Constraint";
    public KimodoMarkerSampleResult SampleData
    {
        get { EnsureSampleData(); return sampleData; }
        set { sampleData = value ?? new KimodoMarkerSampleResult(); EnsureSampleData(); }
    }

    private void EnsureSampleData()
    {
        sampleData ??= new KimodoMarkerSampleResult();
        sampleData.constraintType = "constraint";
        sampleData.mask = KimodoConstraintMask.Resolve(sampleData.mask, "constraint");
    }

    private void OnEnable() => EnsureSampleData();
}
