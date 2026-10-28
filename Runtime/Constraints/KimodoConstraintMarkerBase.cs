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
    [SerializeField] private KimodoMarkerSampleResult sampleData;
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
            string protocolName = ModeProtocolName(value);
            constraintMode = value;
            sampleData ??= new KimodoMarkerSampleResult();
            sampleData.constraintMode = protocolName;
            EnsureSampleData();
        }
    }

    /// <summary>Constraint payload for Inspector, AutoSample and generation.
    /// ConstraintMode is canonical for the marker; the payload string is kept
    /// synchronized for protocol consumers.</summary>
    public KimodoMarkerSampleResult SampleData
    {
        get { EnsureSampleData(); return sampleData; }
        set
        {
            KimodoMarkerSampleResult incoming = value?.Clone() ?? new KimodoMarkerSampleResult();
            KimodoConstraintMode mode = ResolveMode(incoming.constraintMode);
            sampleData = incoming;
            constraintMode = mode;
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
        bool initializeDefaults = sampleData == null;
        sampleData ??= new KimodoMarkerSampleResult();
        sampleData.sampleData ??= new KimodoBridge.MuscleSample();
        if (!KimodoSampleDataLayout.IsValid(sampleData.sampleData))
        {
            sampleData.sampleData = new KimodoBridge.MuscleSample();
        }
        sampleData.enableMask ??= new KimodoConstraintMask();
        sampleData.effectors ??= new KimodoConstraintEffectors();
        sampleData.effectors.leftHand ??= KimodoRigidTransform.Identity;
        sampleData.effectors.rightHand ??= KimodoRigidTransform.Identity;
        sampleData.effectors.leftFoot ??= KimodoRigidTransform.Identity;
        sampleData.effectors.rightFoot ??= KimodoRigidTransform.Identity;
        sampleData.rootOverride ??= KimodoRigidTransform.Identity;
        // Keep the protocol-facing string as a projection of the marker's
        // serialized enum, rather than treating it as a second configuration.
        sampleData.constraintMode = ModeProtocolName(constraintMode);
        sampleData.sampleTime = Math.Max(0.0, time);
        if (initializeDefaults)
        {
            sampleData.enableMask = KimodoConstraintMask.ForType(ModeProtocolName(constraintMode));
            // Inspector-authored effectors start in local space. Command
            // construction assigns World explicitly at its boundary.
            sampleData.effectors.leftHand.space = KimodoConstraintSpace.Local;
            sampleData.effectors.rightHand.space = KimodoConstraintSpace.Local;
            sampleData.effectors.leftFoot.space = KimodoConstraintSpace.Local;
            sampleData.effectors.rightFoot.space = KimodoConstraintSpace.Local;
            if (constraintMode == KimodoConstraintMode.Root2D)
            {
                sampleData.enableMask.rootHeading = true;
            }
            else
            {
                sampleData.sampleValid = true;
            }
        }
    }

    private static KimodoConstraintMode ResolveMode(string value)
    {
        return KimodoConstraintInternal.NormalizeMode(value) switch
        {
            "root2d" => KimodoConstraintMode.Root2D,
            "effector" => KimodoConstraintMode.Effector,
            "fullbody" => KimodoConstraintMode.FullBody,
            _ => throw new InvalidOperationException($"Unsupported constraint mode '{value}'.")
        };
    }

    private static string ModeProtocolName(KimodoConstraintMode mode) => mode switch
    {
        KimodoConstraintMode.Root2D => "root2d",
        KimodoConstraintMode.Effector => "effector",
        KimodoConstraintMode.FullBody => "fullbody",
        _ => throw new InvalidOperationException("Unsupported constraint mode. Select Root2D, FullBody, or Effector.")
    };

    private static string ModeLabel(KimodoConstraintMode mode) => mode switch
    {
        KimodoConstraintMode.Root2D => "Root2D Constraint",
        KimodoConstraintMode.Effector => "Effector Constraint",
        KimodoConstraintMode.FullBody => "FullBody Constraint",
        _ => "Invalid Constraint Mode"
    };
}
