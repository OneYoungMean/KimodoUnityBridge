using UnityEngine;

namespace KimodoBridge
{
    /// <summary>
    /// Disables an editor-only Session root from Awake during Play Mode.
    /// No Play Mode exit callback changes the root's state.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Kimodo/Session Runtime Guard")]
    internal sealed class KimodoDisableOnRuntime : MonoBehaviour
    {
        internal static KimodoDisableOnRuntime Ensure(GameObject root)
        {
            if (root == null) return null;
            return root.GetComponent<KimodoDisableOnRuntime>() ?? root.AddComponent<KimodoDisableOnRuntime>();
        }

        internal void DisableForPlay()
        {
            if (gameObject.activeSelf)
            {
                gameObject.SetActive(false);
            }
        }

        private void Awake()
        {
            if (Application.isPlaying) DisableForPlay();
        }

    }
}
