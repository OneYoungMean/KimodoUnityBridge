using System;
using CharacterAnimationCli.Unity;
using KimodoBridge;
using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

[Serializable]
public sealed class KimodoConstraintMarker : Marker, IKimodoConstraintPreviewSelectable
{
    [Tooltip("If disabled, this marker is ignored by preview, sampling, and generation.")]
    public bool constraintEnabled = true;

    [Tooltip("When enabled, the active constraint follows the Timeline pose at this marker time.")]
    public bool autoSample = true;

    [SerializeField] private KimodoConstraintMode constraintMode = KimodoConstraintMode.FullBody;
    [SerializeField] private KimodoMarkerSampleResult sampleData = new KimodoMarkerSampleResult();

    public string ConstraintType => "constraint";
    public bool ConstraintPreviewEnabled => constraintEnabled;
    public int ConstraintPreviewPriority => 0;
    public string ConstraintPreviewName => ModeLabel(constraintMode);

    public KimodoConstraintMode ConstraintMode
    {
        get => constraintMode;
        set
        {
            EnsureSampleData();
            constraintMode = value;
            sampleData.constraintMode = ModeProtocolName(value);
            sampleData.constraintType = "constraint";
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

    public void CommitSampleData() => EnsureSampleData();
    private void OnEnable() => EnsureSampleData();
    private void OnValidate() => EnsureSampleData();

    private void EnsureSampleData()
    {
        sampleData ??= new KimodoMarkerSampleResult();
        bool initializeDefaults = string.IsNullOrWhiteSpace(sampleData.constraintMode);
        sampleData.sampleData ??= new KimodoBridge.MuscleSample();
        if (!KimodoSampleDataLayout.IsValid(sampleData.sampleData))
        {
            sampleData.sampleData = new KimodoBridge.MuscleSample();
        }
        sampleData.enableMask ??= new KimodoSampleChannelMask();
        sampleData.effectors ??= new KimodoConstraintEffectors();
        sampleData.effectors.leftHand ??= KimodoRigidTransform.Identity;
        sampleData.effectors.rightHand ??= KimodoRigidTransform.Identity;
        sampleData.effectors.leftFoot ??= KimodoRigidTransform.Identity;
        sampleData.effectors.rightFoot ??= KimodoRigidTransform.Identity;
        sampleData.root2DOverride ??= KimodoRigidTransform.Identity;
        sampleData.constraintType = "constraint";
        sampleData.constraintMode = ModeProtocolName(constraintMode);
        sampleData.sampleTime = Math.Max(0.0, time);
        if (initializeDefaults)
        {
            if (constraintMode == KimodoConstraintMode.Root2D)
            {
                sampleData.enableMask.root2DPosition = true;
                sampleData.enableMask.root2DHeading = true;
            }
            else
            {
                sampleData.enableMask.muscle49 = constraintMode == KimodoConstraintMode.FullBody;
                sampleData.enableMask.rootTQ = true;
                sampleData.enableMask.leftFootTQ = true;
                sampleData.enableMask.rightFootTQ = true;
            }
        }
        sampleData.enableMask.NormalizeDependencies();
    }

    private static KimodoConstraintMode ResolveMode(string value, KimodoConstraintMode fallback)
    {
        if (string.Equals(value, "root2d", StringComparison.OrdinalIgnoreCase)) return KimodoConstraintMode.Root2D;
        if (string.Equals(value, "effector", StringComparison.OrdinalIgnoreCase)) return KimodoConstraintMode.Effector;
        if (string.Equals(value, "fullbody", StringComparison.OrdinalIgnoreCase)) return KimodoConstraintMode.FullBody;
        return fallback;
    }

    private static string ModeProtocolName(KimodoConstraintMode mode) => mode switch
    {
        KimodoConstraintMode.Root2D => "root2d",
        KimodoConstraintMode.Effector => "effector",
        _ => "fullbody"
    };

    private static string ModeLabel(KimodoConstraintMode mode) => mode switch
    {
        KimodoConstraintMode.Root2D => "Root2D Constraint",
        KimodoConstraintMode.Effector => "Effector Constraint",
        _ => "FullBody Constraint"
    };
}
