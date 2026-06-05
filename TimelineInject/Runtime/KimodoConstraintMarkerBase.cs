
using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

[Serializable]
public abstract class KimodoConstraintMarkerBase : Marker
{
    [Tooltip("If enabled, use manually edited marker values. If disabled, values are sampled from timeline pose at this marker time.")]
    public bool useOverride;
    [SerializeField]
    private KimodoMarkerSampleResult sampleData = new KimodoMarkerSampleResult();

    public abstract string ConstraintType { get; }

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
        }
    }

    public Vector3 rootPosition
    {
        get => SampleData.rootPosition;
        set => SampleData.rootPosition = value;
    }

    public Vector2 smoothRoot2D
    {
        get => new Vector2(rootPosition.x, rootPosition.z);
        set => rootPosition = new Vector3(value.x, rootPosition.y, value.y);
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

    protected virtual void OnDisable()
    {
        // no-op: marker lifecycle event hub has been removed.
    }
}
