using System;
using CharacterAnimationCli.Unity;
using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

[Serializable]
public sealed class KimodoConstraintMarker : Marker, IKimodoConstraintPreviewSelectable
{
    [Tooltip("If disabled, this marker is ignored by preview, sampling, and generation.")]
    public bool constraintEnabled = true;

    [Tooltip("When enabled, the active constraint mode follows the Timeline pose at this marker time.")]
    public bool autoSample = true;

    [SerializeField] private KimodoConstraintMode constraintMode = KimodoConstraintMode.FullBody;
    [SerializeField] private KimodoRoot2DConstraintData root2DData = new KimodoRoot2DConstraintData();
    [SerializeField] private KimodoFullBodyConstraintData fullBodyData = new KimodoFullBodyConstraintData();
    [SerializeField] private KimodoIkConstraintData ikData = new KimodoIkConstraintData();

    [NonSerialized] private KimodoMarkerSampleResult activeSampleCache;
    [NonSerialized] private KimodoConstraintMode activeSampleCacheMode;

    public string ConstraintType => "constraint";
    public bool ConstraintPreviewEnabled => constraintEnabled;
    public int ConstraintPreviewPriority => 0;
    public string ConstraintPreviewName => ModeLabel(constraintMode);

    public KimodoConstraintMode ConstraintMode
    {
        get => constraintMode;
        set
        {
            CommitActiveSample();
            constraintMode = value;
            activeSampleCache = null;
        }
    }

    public KimodoRoot2DConstraintData Root2DData
    {
        get { EnsurePayloads(); return root2DData; }
    }

    public KimodoFullBodyConstraintData FullBodyData
    {
        get { EnsurePayloads(); return fullBodyData; }
    }

    public KimodoIkConstraintData IkData
    {
        get { EnsurePayloads(); return ikData; }
    }

    // Existing runtime/editor callers receive a live adapter over the
    // selected payload. The serialized source of truth is the mode payload,
    // never a shared CharacterPose plus mask.
    public KimodoMarkerSampleResult SampleData
    {
        get
        {
            EnsurePayloads();
            EnsureActiveSample();
            return activeSampleCache;
        }
        set
        {
            EnsurePayloads();
            ApplyActiveSample(value);
        }
    }

    // Source-level aliases used by older editor helpers. They are not
    // serialized and all modes use the same Auto Sample switch now.
    public bool autoSampleFullBody
    {
        get => autoSample;
        set => autoSample = value;
    }

    [HideInInspector]
    public bool autoSampleRoot2D
    {
        get => autoSample;
        set => autoSample = value;
    }

    public void CommitSampleData() => CommitActiveSample();

    private void OnEnable()
    {
        EnsurePayloads();
        activeSampleCache = null;
    }

    private void OnValidate()
    {
        EnsurePayloads();
        activeSampleCache = null;
    }

    private void EnsurePayloads()
    {
        root2DData ??= new KimodoRoot2DConstraintData();
        root2DData.root ??= new CharacterPoseTransform();

        fullBodyData ??= new KimodoFullBodyConstraintData();
        fullBodyData.pose ??= new CharacterPose();
        fullBodyData.pose.root ??= new CharacterPoseTransform();
        fullBodyData.pose.hands ??= new CharacterPoseSides();
        fullBodyData.pose.feet ??= new CharacterPoseSides();
        fullBodyData.ikTargets ??= new KimodoConstraintIkTargets();
        fullBodyData.ikTargets.hands ??= new CharacterPoseSides();
        fullBodyData.ikTargets.feet ??= new CharacterPoseSides();

        ikData ??= new KimodoIkConstraintData();
        ikData.referencePose ??= new CharacterPose();
        ikData.referencePose.root ??= new CharacterPoseTransform();
        ikData.referencePose.hands ??= new CharacterPoseSides();
        ikData.referencePose.feet ??= new CharacterPoseSides();
        ikData.ikTargets ??= new KimodoConstraintIkTargets();
        ikData.ikTargets.hands ??= new CharacterPoseSides();
        ikData.ikTargets.feet ??= new CharacterPoseSides();
    }

    private void EnsureActiveSample()
    {
        if (activeSampleCache != null && activeSampleCacheMode == constraintMode) return;

        CommitActiveSample();
        activeSampleCacheMode = constraintMode;
        activeSampleCache = new KimodoMarkerSampleResult
        {
            constraintType = "constraint",
            constraintMode = ModeProtocolName(constraintMode),
            sampleTime = Math.Max(0.0, time),
            hasRootHeading = true
        };

        switch (constraintMode)
        {
            case KimodoConstraintMode.Root2D:
                activeSampleCache.characterPose = new CharacterPose
                {
                    root = CloneTransform(root2DData.root),
                    hands = new CharacterPoseSides(),
                    feet = new CharacterPoseSides()
                };
                activeSampleCache.hasRoot2DOverride = true;
                activeSampleCache.root2DOverride = CloneTransform(root2DData.root);
                activeSampleCache.hasRootHeading = root2DData.allowHeading;
                activeSampleCache.mask = KimodoConstraintMask.ForType("root2d");
                break;
            case KimodoConstraintMode.IK:
                activeSampleCache.characterPose = PoseWithTargets(ikData.referencePose, ikData.ikTargets);
                activeSampleCache.mask = BuildIkMask(ikData);
                break;
            default:
                activeSampleCache.characterPose = PoseWithTargets(fullBodyData.pose, fullBodyData.ikTargets);
                activeSampleCache.mask = BuildFullBodyMask();
                break;
        }
    }

    private void CommitActiveSample()
    {
        if (activeSampleCache == null) return;
        EnsurePayloads();
        CharacterPose pose = activeSampleCache.characterPose;
        if (pose == null) return;

        switch (activeSampleCacheMode)
        {
            case KimodoConstraintMode.Root2D:
                root2DData.root = CloneTransform(pose.root);
                root2DData.allowHeading = activeSampleCache.hasRootHeading;
                break;
            case KimodoConstraintMode.IK:
                ikData.referencePose = pose.Clone();
                ikData.ikTargets = TargetsFromPose(pose);
                ApplyIkMask(ikData, activeSampleCache.mask);
                break;
            default:
                fullBodyData.pose = pose.Clone();
                fullBodyData.ikTargets = TargetsFromPose(pose);
                break;
        }
    }

    private void ApplyActiveSample(KimodoMarkerSampleResult value)
    {
        CommitActiveSample();
        activeSampleCache = value?.Clone();
        if (activeSampleCache == null)
        {
            activeSampleCacheMode = constraintMode;
            return;
        }

        activeSampleCacheMode = ResolveMode(
            string.IsNullOrWhiteSpace(value.constraintMode) ? value.constraintType : value.constraintMode,
            constraintMode);
        constraintMode = activeSampleCacheMode;
        CommitActiveSample();
        activeSampleCache = null;
    }

    private static KimodoConstraintMode ResolveMode(string value, KimodoConstraintMode fallback)
    {
        if (string.Equals(value, "root2d", StringComparison.OrdinalIgnoreCase)) return KimodoConstraintMode.Root2D;
        if (string.Equals(value, "ik", StringComparison.OrdinalIgnoreCase)) return KimodoConstraintMode.IK;
        if (string.Equals(value, "fullbody", StringComparison.OrdinalIgnoreCase)) return KimodoConstraintMode.FullBody;
        return fallback;
    }

    private static string ModeProtocolName(KimodoConstraintMode mode) => mode switch
    {
        KimodoConstraintMode.Root2D => "root2d",
        KimodoConstraintMode.IK => "ik",
        _ => "fullbody"
    };

    private static string ModeLabel(KimodoConstraintMode mode) => mode switch
    {
        KimodoConstraintMode.Root2D => "Root2D Constraint",
        KimodoConstraintMode.IK => "IK Constraint+",
        _ => "FullBody Constraint"
    };

    private static KimodoConstraintMask BuildFullBodyMask()
    {
        return new KimodoConstraintMask
        {
            muscle = true,
            rootPosition = true,
            rootHeading = true,
            leftHand = true,
            rightHand = true,
            leftFoot = true,
            rightFoot = true
        };
    }

    private static KimodoConstraintMask BuildIkMask(KimodoIkConstraintData data)
    {
        return new KimodoConstraintMask
        {
            rootPosition = true,
            rootHeading = true,
            leftHand = data.leftHand,
            rightHand = data.rightHand,
            leftFoot = data.leftFoot,
            rightFoot = data.rightFoot
        };
    }

    private static void ApplyIkMask(KimodoIkConstraintData data, KimodoConstraintMask mask)
    {
        if (mask == null) return;
        data.leftHand = mask.leftHand;
        data.rightHand = mask.rightHand;
        data.leftFoot = mask.leftFoot;
        data.rightFoot = mask.rightFoot;
    }

    private static KimodoConstraintIkTargets TargetsFromPose(CharacterPose pose) => new KimodoConstraintIkTargets
    {
        hands = pose?.hands?.Clone() ?? new CharacterPoseSides(),
        feet = pose?.feet?.Clone() ?? new CharacterPoseSides()
    };

    private static CharacterPose PoseWithTargets(
        CharacterPose source,
        KimodoConstraintIkTargets targets)
    {
        CharacterPose pose = source?.Clone() ?? new CharacterPose();
        targets?.CopyTo(pose);
        return pose;
    }

    private static CharacterPoseTransform CloneTransform(CharacterPoseTransform value) =>
        value != null
            ? new CharacterPoseTransform { t = value.t, q = value.q }
            : new CharacterPoseTransform();
}
