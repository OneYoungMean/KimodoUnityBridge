using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoAnalysisPreviewStyle
    {
        internal static readonly Color ConstraintColor = new Color(0.48f, 0.76f, 1f);

        internal static Color ResolveColor(string eventKind, string sourceRole)
        {
            switch ((eventKind ?? string.Empty).ToLowerInvariant())
            {
                case "start": return new Color(0.25f, 0.85f, 0.35f);
                case "end": return new Color(0.95f, 0.3f, 0.25f);
                case "left-foot": return new Color(0.2f, 0.5f, 0.95f);
                case "right-foot": return new Color(0.95f, 0.25f, 0.25f);
                default: return Color.yellow;
            }
        }
    }
}
