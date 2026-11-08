using UnityEngine;

namespace KimodoUnityBridge.Command
{
    /// <summary>
    /// Legacy serialized component retained so older scenes can deserialize.
    /// Session 3.0 does not add or consult this component; current state comes
    /// directly from the active Timeline and History header.
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
