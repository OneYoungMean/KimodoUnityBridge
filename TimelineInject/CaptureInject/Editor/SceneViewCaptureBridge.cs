using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace KimodoUnityBridge.Command
{
    internal static class SceneViewCaptureBridge
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        internal static Texture2D Render(SceneView view, int width, int height)
        {
            if (view == null || view.camera == null) throw new InvalidOperationException("No active Scene View camera is available.");
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
            Camera camera = view.camera;
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            Rect previousRect = camera.rect;
            RenderTexture target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.Default);
            bool setup = false;
            try
            {
                camera.targetTexture = target;
                camera.rect = new Rect(0, 0, 1, 1);
                RenderTexture.active = target;
                MethodInfo setupCamera = Require(typeof(Handles), "Internal_SetupCamera", StaticFlags);
                MethodInfo clearCamera = Require(typeof(Handles), "Internal_ClearCamera", StaticFlags);
                MethodInfo finishCamera = typeof(Handles).GetMethod("Internal_FinishDrawingCamera", StaticFlags, null,
                    new[] { typeof(Camera), typeof(bool) }, null) ?? throw new MissingMethodException("Handles.Internal_FinishDrawingCamera is unavailable.");
                setupCamera.Invoke(null, new object[] { camera });
                setup = true;
                camera.ResetWorldToCameraMatrix();
                camera.ResetProjectionMatrix();
                clearCamera.Invoke(null, new object[] { camera });
                PropertyInfo gridsProperty = typeof(SceneView).GetProperty("sceneViewGrids", InstanceFlags);
                object grids = gridsProperty?.GetValue(view);
                MethodInfo prepareGrid = grids?.GetType().GetMethod("PrepareGridRender", InstanceFlags);
                MethodInfo drawWithGrid = typeof(Handles).GetMethod("Internal_DrawCameraWithGrid", StaticFlags);
                if (grids != null && prepareGrid != null && drawWithGrid != null)
                {
                    object grid = prepareGrid.Invoke(grids, new object[] { camera, camera.transform.position, camera.transform.rotation,
                        camera.orthographic ? Mathf.Max(camera.orthographicSize * 2f, .0001f) : view.size, camera.orthographic });
                    drawWithGrid.Invoke(null, new object[] { camera, view.cameraMode.drawMode, grid, view.drawGizmos, view.showGrid });
                }
                else
                {
                    MethodInfo drawCamera = Require(typeof(Handles), "Internal_DrawCameraWithFilter", StaticFlags);
                    drawCamera.Invoke(null, new object[] { camera, view.cameraMode.drawMode, view.drawGizmos, true, null });
                }
                finishCamera.Invoke(null, new object[] { camera, view.drawGizmos });
                setup = false;
                GL.Flush();
                RenderTexture.active = target;
                var image = new Texture2D(width, height, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply(false, false);
                return image;
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                throw new InvalidOperationException("Synchronous SceneView rendering failed: " + exception.InnerException.Message, exception.InnerException);
            }
            finally
            {
                if (setup)
                {
                    try { typeof(Handles).GetMethod("Internal_FinishDrawingCamera", StaticFlags, null,
                        new[] { typeof(Camera), typeof(bool) }, null)?.Invoke(null, new object[] { camera, view.drawGizmos }); }
                    catch { }
                }
                camera.targetTexture = previousTarget;
                camera.rect = previousRect;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        private static MethodInfo Require(Type type, string name, BindingFlags flags) =>
            type.GetMethod(name, flags) ?? throw new MissingMethodException(type.FullName, name);
    }
}
