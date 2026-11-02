using UnityEngine;

namespace KimodoUnityBridge.Command
{
    /// <summary>
    /// Scene-serialized identity and output binding for the Timeline context.
    /// The Command assembly rebuilds its runtime view from the Timeline and
    /// writes snapshots to the explicitly bound JSON path.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class KimodoSessionContextBinding : MonoBehaviour
    {
        public string sessionId;
        public string sessionName;
        public string sessionJsonPath;
        public string timelineAssetPath;
        public bool isAutomatic;
        public int sessionRevision;
    }
}
