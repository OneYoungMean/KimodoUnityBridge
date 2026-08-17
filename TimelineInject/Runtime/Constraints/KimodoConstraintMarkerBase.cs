using System;
using TimelineInject;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Timeline;

[Serializable]
public sealed class KimodoConstraintMarker : Marker, IKimodoConstraintPreviewSelectable
{
    [Tooltip("If disabled, this marker is ignored by preview, sampling, and generation.")]
    public bool constraintEnabled = true;
    [Tooltip("When enabled, FullBody muscle and root values follow the Timeline pose at this marker time.")]
    public bool autoSampleFullBody = true;
    // Kept for serialized-marker compatibility. Root2D overrides are command
    // data; the Unity authoring UI never creates or edits them.
    [HideInInspector] public bool autoSampleRoot2D;
    // Migration-only: preserves old manual marker data without exposing the
    // retired Override concept in the authoring UI.
    [FormerlySerializedAs("useOverride")]
    [SerializeField] private bool legacyManualValues;
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

    private void OnEnable()
    {
        if (legacyManualValues)
        {
            autoSampleFullBody = false;
            autoSampleRoot2D = false;
            legacyManualValues = false;
        }
        EnsureSampleData();
    }
}
