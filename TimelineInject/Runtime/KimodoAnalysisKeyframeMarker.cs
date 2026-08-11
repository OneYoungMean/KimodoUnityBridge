using System;
using UnityEngine.Serialization;
using UnityEngine.Timeline;

[Serializable]
public sealed class KimodoAnalysisKeyframeMarker : Marker
{
    public int frame;
    [FormerlySerializedAs("score")]
    public float saliency;
    public string reasons = string.Empty;
}
