using UnityEngine;

namespace KimodoBridge
{
    [AddComponentMenu("Kimodo/Runtime Motion Driver Test")]
    public sealed class MotionDriverTest : MonoBehaviour
    {
        [SerializeField] private KimodoRuntimeMotionDriver driver;

        [Header("Prompt")]
        [SerializeField] private string prompt = "idle";

        [Header("Duration")]
        [SerializeField] private float durationSeconds = 5f;

        [Header("Root2D")]
        [SerializeField] private float rootLocalX;
        [SerializeField] private float rootLocalZ = 1f;
        [SerializeField] private float rootWorldX;
        [SerializeField] private float rootWorldZ = 1f;
        [SerializeField] private float rootHeadingX;
        [SerializeField] private float rootHeadingZ = 1f;
        [SerializeField] private float constraintTimeSeconds = 1f;

        [Header("End Effector")]
        [SerializeField] private float targetX;
        [SerializeField] private float targetY = 1f;
        [SerializeField] private float targetZ = 1f;
        [SerializeField] private float effectorTimeSeconds = 1f;

        private Rect windowRect = new Rect(16f, 16f, 440f, 760f);
        private Vector2 scroll;
        private string lastResult = "Ready.";

        private void Reset()
        {
            if (driver == null)
            {
                driver = GetComponent<KimodoRuntimeMotionDriver>();
            }
        }

        private void OnGUI()
        {
            windowRect = GUI.Window(GetInstanceID(), windowRect, DrawWindow, "Motion Driver Test");
        }

        private void DrawWindow(int windowId)
        {
            if (driver == null)
            {
                GUILayout.Label("Driver is not assigned.");
                GUI.DragWindow(new Rect(0f, 0f, windowRect.width, 24f));
                return;
            }

            scroll = GUILayout.BeginScrollView(scroll, false, true);

            DrawStatusSection();
            DrawPromptSection();
            DrawDurationSection();
            DrawRootSection();
            DrawEffectorSection();

            GUILayout.Space(8f);
            GUILayout.Label("Last Result");
            GUILayout.TextArea(lastResult, GUILayout.MinHeight(80f));

            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, windowRect.width, 24f));
        }

        private void DrawStatusSection()
        {
            GUILayout.Label("Status");
            bool isIdle;
            string currentPrompt = driver.GetCurrentPrompt(out isIdle);
            Vector3 position = driver.GetPosition();

            GUILayout.Label($"Running: {driver.IsRunning}");
            GUILayout.Label($"Prompt: {currentPrompt}");
            GUILayout.Label($"IsIdle: {isIdle}");
            GUILayout.Label($"Position: ({position.x:0.###}, {position.y:0.###}, {position.z:0.###})");
            GUILayout.Label($"Duration: {driver.GetAnimationDurationSeconds():0.###}s");
            GUILayout.Label($"Status: {driver.StatusMessage}");

            bool nextDrawDebugSkeleton = GUILayout.Toggle(driver.DrawDebugSkeleton, "Draw Debug Skeleton");
            if (nextDrawDebugSkeleton != driver.DrawDebugSkeleton)
            {
                driver.DrawDebugSkeleton = nextDrawDebugSkeleton;
                lastResult = $"DrawDebugSkeleton = {nextDrawDebugSkeleton}";
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Read Prompt"))
            {
                lastResult = $"Prompt={currentPrompt}, IsIdle={isIdle}";
            }

            if (GUILayout.Button("Read Position"))
            {
                lastResult = $"Position=({position.x:0.###}, {position.y:0.###}, {position.z:0.###})";
            }

            GUILayout.EndHorizontal();

            if (GUILayout.Button("Reset Motion"))
            {
                _ = driver.ResetMotionAsync();
                lastResult = "ResetMotionAsync called.";
            }
        }

        private void DrawPromptSection()
        {
            GUILayout.Space(8f);
            GUILayout.Label("Prompt");
            prompt = DrawTextField("Text", prompt);

            if (GUILayout.Button("Set Prompt"))
            {
                driver.SetAnimationPrompt(prompt);
                lastResult = $"SetAnimationPrompt(\"{prompt}\")";
            }
        }

        private void DrawDurationSection()
        {
            GUILayout.Space(8f);
            GUILayout.Label("Duration");
            durationSeconds = DrawFloatField("Seconds", durationSeconds);

            if (GUILayout.Button("Set Duration"))
            {
                driver.SetAnimationDurationSeconds(durationSeconds);
                lastResult = $"SetAnimationDurationSeconds({durationSeconds:0.###})";
            }
        }

        private void DrawRootSection()
        {
            GUILayout.Space(8f);
            GUILayout.Label("Root2D");
            constraintTimeSeconds = DrawFloatField("Constraint Time", constraintTimeSeconds);
            rootLocalX = DrawFloatField("Local X", rootLocalX);
            rootLocalZ = DrawFloatField("Local Z", rootLocalZ);
            rootWorldX = DrawFloatField("World X", rootWorldX);
            rootWorldZ = DrawFloatField("World Z", rootWorldZ);
            rootHeadingX = DrawFloatField("Heading X", rootHeadingX);
            rootHeadingZ = DrawFloatField("Heading Z", rootHeadingZ);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Queue Local Root2D"))
            {
                driver.SetRoot2DLocal(rootLocalX, rootLocalZ, constraintTimeSeconds);
                lastResult = $"SetRoot2DLocal({rootLocalX:0.###}, {rootLocalZ:0.###}, {constraintTimeSeconds:0.###})";
            }

            if (GUILayout.Button("Queue World Root2D"))
            {
                driver.SetRoot2D(rootWorldX, rootWorldZ, constraintTimeSeconds);
                lastResult = $"SetRoot2D({rootWorldX:0.###}, {rootWorldZ:0.###}, {constraintTimeSeconds:0.###})";
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Queue Local Root+Heading"))
            {
                driver.SetRoot2DLocal(rootLocalX, rootLocalZ, rootHeadingX, rootHeadingZ, constraintTimeSeconds);
                lastResult =
                    $"SetRoot2DLocal({rootLocalX:0.###}, {rootLocalZ:0.###}, {rootHeadingX:0.###}, {rootHeadingZ:0.###}, {constraintTimeSeconds:0.###})";
            }

            if (GUILayout.Button("Queue World Root+Heading"))
            {
                driver.SetRoot2D(rootWorldX, rootWorldZ, rootHeadingX, rootHeadingZ, constraintTimeSeconds);
                lastResult =
                    $"SetRoot2D({rootWorldX:0.###}, {rootWorldZ:0.###}, {rootHeadingX:0.###}, {rootHeadingZ:0.###}, {constraintTimeSeconds:0.###})";
            }

            GUILayout.EndHorizontal();
        }

        private void DrawEffectorSection()
        {
            GUILayout.Space(8f);
            GUILayout.Label("End Effector");
            effectorTimeSeconds = DrawFloatField("Effector Time", effectorTimeSeconds);
            targetX = DrawFloatField("Target X", targetX);
            targetY = DrawFloatField("Target Y", targetY);
            targetZ = DrawFloatField("Target Z", targetZ);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Left Hand"))
            {
                driver.SetLeftHandConstraint(targetX, targetY, targetZ, effectorTimeSeconds);
                lastResult = $"SetLeftHandConstraint({targetX:0.###}, {targetY:0.###}, {targetZ:0.###}, {effectorTimeSeconds:0.###})";
            }

            if (GUILayout.Button("Right Hand"))
            {
                driver.SetRightHandConstraint(targetX, targetY, targetZ, effectorTimeSeconds);
                lastResult = $"SetRightHandConstraint({targetX:0.###}, {targetY:0.###}, {targetZ:0.###}, {effectorTimeSeconds:0.###})";
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Left Foot"))
            {
                driver.SetLeftFootConstraint(targetX, targetY, targetZ, effectorTimeSeconds);
                lastResult = $"SetLeftFootConstraint({targetX:0.###}, {targetY:0.###}, {targetZ:0.###}, {effectorTimeSeconds:0.###})";
            }

            if (GUILayout.Button("Right Foot"))
            {
                driver.SetRightFootConstraint(targetX, targetY, targetZ, effectorTimeSeconds);
                lastResult = $"SetRightFootConstraint({targetX:0.###}, {targetY:0.###}, {targetZ:0.###}, {effectorTimeSeconds:0.###})";
            }

            GUILayout.EndHorizontal();
        }

        private static string DrawTextField(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(120f));
            string result = GUILayout.TextField(value ?? string.Empty);
            GUILayout.EndHorizontal();
            return result;
        }

        private static float DrawFloatField(string label, float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(120f));
            string next = GUILayout.TextField(value.ToString("0.###"));
            GUILayout.EndHorizontal();

            if (float.TryParse(next, out float parsed))
            {
                return parsed;
            }

            return value;
        }
    }
}
