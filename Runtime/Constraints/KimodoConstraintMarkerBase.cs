using System;
using KimodoUnityBridge;
using KimodoBridge;
using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

[Serializable]
public class KimodoConstraintMarker : Marker, IKimodoConstraintPreviewSelectable
{
    // Analysis-preview metadata lives on the common marker type so editor
    // preview/inspector code can read it without down-casting. These fields
    // are ignored by generation for regular constraint markers.
    public int frame;
    public string eventKind = string.Empty;
    public string message = string.Empty;
    public Color color = Color.yellow;
    public string sourceClipKey = string.Empty;
    public string sourceRole = string.Empty;

    [Tooltip("If disabled, this marker is ignored by preview, sampling, and generation.")]
    public bool constraintEnabled = true;

    [Tooltip("When enabled, the active constraint follows the Timeline pose at this marker time.")]
    public bool autoSample = true;

    [SerializeField] private KimodoConstraintMode constraintMode = KimodoConstraintMode.FullBody;
    [SerializeField] private KimodoConstraintMarkerType markerType = KimodoConstraintMarkerType.Constraint;
    [SerializeField] private KimodoMarkerSampleResult sampleData = new KimodoMarkerSampleResult();
    [SerializeField] private KimodoRootPathData pathData;

    public string ConstraintType => markerType switch
    {
        KimodoConstraintMarkerType.External => "external",
        KimodoConstraintMarkerType.ExternalPath => "external-path",
        KimodoConstraintMarkerType.Analysis => "analysis",
        _ => "constraint"
    };
    public bool ConstraintPreviewEnabled => constraintEnabled &&
        (markerType == KimodoConstraintMarkerType.Constraint || markerType == KimodoConstraintMarkerType.Analysis);
    public int ConstraintPreviewPriority => 0;
    public string ConstraintPreviewName => markerType switch
    {
        KimodoConstraintMarkerType.External => "External Pose",
        KimodoConstraintMarkerType.ExternalPath => "External Path",
        KimodoConstraintMarkerType.Analysis => "Analysis Pose",
        _ => ModeLabel(constraintMode)
    };

    public KimodoConstraintMarkerType MarkerType
    {
        get => markerType;
        set => markerType = value;
    }

    public bool ParticipatesInGeneration => markerType == KimodoConstraintMarkerType.Constraint;
    public bool IsAnalysis => markerType == KimodoConstraintMarkerType.Analysis;
    public bool IsExternal => markerType == KimodoConstraintMarkerType.External || markerType == KimodoConstraintMarkerType.ExternalPath;
    public bool IsExternalPath => markerType == KimodoConstraintMarkerType.ExternalPath;

    public KimodoRootPathData PathData
    {
        get => pathData?.Clone();
        set => pathData = value?.Clone();
    }

    public KimodoConstraintMode ConstraintMode
    {
        get => constraintMode;
        set
        {
            EnsureSampleData();
            constraintMode = value;
            sampleData.constraintMode = ModeProtocolName(value);
        }
    }

    /// <summary>Single serialized source of truth for Inspector, window,
    /// AutoSample and generation.</summary>
    public KimodoMarkerSampleResult SampleData
    {
        get { EnsureSampleData(); return sampleData; }
        set
        {
            sampleData = value?.Clone() ?? new KimodoMarkerSampleResult();
            constraintMode = ResolveMode(sampleData.constraintMode, constraintMode);
            EnsureSampleData();
        }
    }

    public KimodoConstraintEffectors GetEffectors()
    {
        EnsureSampleData();
        return sampleData.effectors.Clone();
    }

    public void SetEffectors(KimodoConstraintEffectors value)
    {
        EnsureSampleData();
        sampleData.effectors = value?.Clone() ?? new KimodoConstraintEffectors();
    }

    public void CommitSampleData() => EnsureSampleData();
    private void OnEnable() => EnsureSampleData();
    private void OnValidate() => EnsureSampleData();

    private void EnsureSampleData()
    {
        sampleData ??= new KimodoMarkerSampleResult();
        bool initializeDefaults = string.IsNullOrWhiteSpace(sampleData.constraintMode) ||
            string.Equals(sampleData.constraintMode, "constraint", StringComparison.OrdinalIgnoreCase) &&
            (sampleData.enableMask == null || sampleData.enableMask.IsEmpty) &&
            (sampleData.validMask == null || sampleData.validMask.IsEmpty);
        sampleData.sampleData ??= new KimodoBridge.MuscleSample();
        if (!KimodoSampleDataLayout.IsValid(sampleData.sampleData))
        {
            sampleData.sampleData = new KimodoBridge.MuscleSample();
        }
        sampleData.enableMask ??= new KimodoConstraintMask();
        sampleData.validMask ??= new KimodoConstraintMask();
        sampleData.effectors ??= new KimodoConstraintEffectors();
        sampleData.effectors.leftHand ??= KimodoRigidTransform.Identity;
        sampleData.effectors.rightHand ??= KimodoRigidTransform.Identity;
        sampleData.effectors.leftFoot ??= KimodoRigidTransform.Identity;
        sampleData.effectors.rightFoot ??= KimodoRigidTransform.Identity;
        sampleData.rootOverride ??= KimodoRigidTransform.Identity;
        sampleData.constraintMode = ModeProtocolName(constraintMode);
        sampleData.sampleTime = Math.Max(0.0, time);
        if (initializeDefaults)
        {
            sampleData.enableMask = KimodoConstraintMask.ForType(ModeProtocolName(constraintMode));
            if (constraintMode == KimodoConstraintMode.Root2D)
            {
                sampleData.validMask.rootPosition = true;
                sampleData.validMask.rootHeading = true;
            }
            else
            {
                sampleData.validMask.muscle = constraintMode == KimodoConstraintMode.FullBody;
                sampleData.validMask.rootTQ = true;
                sampleData.validMask.leftFootTQ = true;
                sampleData.validMask.rightFootTQ = true;
            }
        }
    }

    private static KimodoConstraintMode ResolveMode(string value, KimodoConstraintMode fallback)
    {
        if (string.Equals(value, "root2d", StringComparison.OrdinalIgnoreCase)) return KimodoConstraintMode.Root2D;
        if (string.Equals(value, "effector", StringComparison.OrdinalIgnoreCase)) return KimodoConstraintMode.Effector;
        if (string.Equals(value, "mix", StringComparison.OrdinalIgnoreCase)) return KimodoConstraintMode.Mix;
        if (string.Equals(value, "fullbody", StringComparison.OrdinalIgnoreCase)) return KimodoConstraintMode.FullBody;
        return fallback;
    }

    private static string ModeProtocolName(KimodoConstraintMode mode) => mode switch
    {
        KimodoConstraintMode.Root2D => "root2d",
        KimodoConstraintMode.Effector => "effector",
        KimodoConstraintMode.Mix => "mix",
        _ => "fullbody"
    };

    private static string ModeLabel(KimodoConstraintMode mode) => mode switch
    {
        KimodoConstraintMode.Root2D => "Root2D Constraint",
        KimodoConstraintMode.Effector => "Effector Constraint",
        KimodoConstraintMode.Mix => "Mixed Constraint",
        _ => "FullBody Constraint"
    };
}
