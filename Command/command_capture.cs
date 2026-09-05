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

        private const string AnalysisPictureRenderVersion = "21-humanbodybones-mesh";
        private const string TestAnalysisPictureRenderVersion = "36-align-analysis-timebase";
        private const int PictureSupersample = 2;
        private const int TestPoseSupersampleHeight = 2048;
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
        private static readonly Color TestStartFrameTint = new Color(.35f, .65f, .62f, 1f);
        private static readonly Color TestEndFrameTint = new Color(.78f, .35f, .40f, 1f);
        private static readonly Color TestKeyframeTint = new Color(.82f, .70f, .22f, 1f);
        private static GameObject captureSessionRoot;

        private static bool IsBuiltInCapturePipeline()
        {
            return GraphicsSettings.currentRenderPipeline == null;
        }
    }
}
