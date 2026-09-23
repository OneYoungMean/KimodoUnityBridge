using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KimodoUnityBridge;
using KimodoBridge;
using TimelineInject;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private static readonly Dictionary<string, AnalysisCacheRecord> AnalysisCache =
            new Dictionary<string, AnalysisCacheRecord>(StringComparer.OrdinalIgnoreCase);
        // Pose samples are expensive (they evaluate a Timeline/retarget
        // sampler). Keep one canonical sample array for the lifetime of an
        // analysis request so trajectory, endpoint checks and rendering all
        // consume the same data.
        private static readonly Dictionary<string, KimodoMarkerSampleResult[]> AnalysisPoseSamples =
            new Dictionary<string, KimodoMarkerSampleResult[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, PrecomputedAnalysis> PrecomputedAnalyses =
            new Dictionary<string, PrecomputedAnalysis>(StringComparer.OrdinalIgnoreCase);

        private sealed class PrecomputedAnalysis
        {
            public JObject Analysis;
            public KimodoMarkerSampleResult[] Samples;
        }

        private const string AnalysisPictureRenderVersion = "21-humanbodybones-mesh";
        private const string UnifiedAnalysisPictureRenderVersion = "64-test-analysis-picture-default-material-hdrp-lights-2x";
        private const string TestAnalysisPicture20TileRenderVersion = "57-pose-spacing-event-ghosts-phase-v2";
        private const int TestAnalysisCanvasWidth = 1920;
        private const int TestAnalysisCanvasHeight = 1080;
        private const int PictureSupersample = 2;
        private const float TestPoseJointCameraOffsetMeters = .2f;
        private const float TestPoseFootForwardCameraOffsetMeters = .3f;
        private const float TestPoseHeadCameraOffsetMeters = .3f;
        private const float TestCameraMarginMeters = .5f;
        private const float TestCameraFitScale = 1f;
        private const float TestGhostAlphaMin = .1f;
        private const float TestGhostAlphaMax = .5f;
        private const float StationaryTrajectoryRange = .25f;
        private const int StationaryTrajectoryMinFrames = 10;
        private const float StationaryTrajectoryAlphaBoost = .1f;
        private const float MaxPromotedGhostAlpha = .75f;
        private const float HeightTimePoseGapMeters = .06f;
        private static readonly Color TestStartFrameTint = new Color(.35f, .65f, .62f, 1f);
        private static readonly Color TestEndFrameTint = new Color(.78f, .35f, .40f, 1f);
        private static readonly Color TestKeyframeTint = new Color(.82f, .70f, .22f, 1f);
        private static GameObject captureSessionRoot;

        private enum CapturePipeline
        {
            BuiltIn,
            Urp,
            Hdrp,
            OtherSrp
        }

        private static CapturePipeline GetCapturePipeline()
        {
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline == null) return CapturePipeline.BuiltIn;
            string name = pipeline.GetType().FullName ?? string.Empty;
            if (name.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("HDRP", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return CapturePipeline.Hdrp;
            }
            if (name.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("URP", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return CapturePipeline.Urp;
            }
            return CapturePipeline.OtherSrp;
        }

        private static Shader FindAnalysisShader(string hdrpName, string urpName, string builtInName)
        {
            string[] orderedNames;
            switch (GetCapturePipeline())
            {
                case CapturePipeline.Hdrp:
                    orderedNames = new[] { hdrpName, urpName, builtInName };
                    break;
                case CapturePipeline.Urp:
                    orderedNames = new[] { urpName, hdrpName, builtInName };
                    break;
                case CapturePipeline.BuiltIn:
                    orderedNames = new[] { builtInName, urpName, hdrpName };
                    break;
                default:
                    orderedNames = new[] { urpName, hdrpName, builtInName };
                    break;
            }

            foreach (string name in orderedNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                Shader shader = Shader.Find(name);
                if (shader != null) return shader;
            }
            return null;
        }

        private static bool IsBuiltInCapturePipeline()
        {
            return GetCapturePipeline() == CapturePipeline.BuiltIn;
        }
    }
}
