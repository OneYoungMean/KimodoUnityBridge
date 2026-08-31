using NUnit.Framework;
using UnityEngine;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class PoseDepthCompositeTests
    {
        [Test]
        public void PoseDepthComposite_IsOrderIndependent_AndBlendsGhostAndPath()
        {
            ComputeShader shader = Resources.Load<ComputeShader>("KimodoPoseDepthComposite");
            Assert.That(shader, Is.Not.Null, "Missing GPU pose composite compute shader.");
            const int width = 2, height = 1;
            RenderTexture accum = Make(width, height, RenderTextureFormat.ARGB32, true);
            RenderTexture depth = Make(width, height, RenderTextureFormat.RFloat, true);
            RenderTexture baseColor = Make(width, height, RenderTextureFormat.ARGB32, false);
            Texture2D farColor = MakeImage(new Color(1f, 0f, 0f, .5f), Color.clear);
            Texture2D nearColor = MakeImage(new Color(0f, 1f, 0f, .5f), Color.clear);
            Texture2D farDepth = MakeImage(new Color(0f, 0f, 0f, 1f), Color.clear);
            Texture2D nearDepth = MakeImage(new Color(0f, 0f, 0f, 1f), Color.clear);
            float far = SystemInfo.usesReversedZBuffer ? .2f : .8f;
            float near = SystemInfo.usesReversedZBuffer ? .8f : .2f;
            SetDepth(farDepth, far); SetDepth(nearDepth, near);
            try
            {
                Graphics.SetRenderTarget(accum); GL.Clear(false, true, Color.gray);
                Graphics.SetRenderTarget(baseColor); GL.Clear(false, true, Color.gray);
                int init = shader.FindKernel("InitDepth");
                shader.SetInt("_Width", width); shader.SetInt("_Height", height);
                shader.SetInt("_ReversedZ", SystemInfo.usesReversedZBuffer ? 1 : 0);
                shader.SetTexture(init, "_AccumDepth", depth); shader.Dispatch(init, 1, 1, 1);
                DispatchPose(shader, accum, depth, baseColor, farColor, farDepth, 1f);
                DispatchPose(shader, accum, depth, baseColor, nearColor, nearDepth, 1f);
                Color first = ReadPixel(accum);

                Graphics.SetRenderTarget(accum); GL.Clear(false, true, Color.gray);
                shader.SetTexture(init, "_AccumDepth", depth); shader.Dispatch(init, 1, 1, 1);
                DispatchPose(shader, accum, depth, baseColor, nearColor, nearDepth, 1f);
                DispatchPose(shader, accum, depth, baseColor, farColor, farDepth, 1f);
                Color second = ReadPixel(accum);
                Assert.That(Vector3.Distance(new Vector3(first.r, first.g, first.b), new Vector3(second.r, second.g, second.b)), Is.LessThan(.01f));
                Assert.That(first.g, Is.GreaterThan(first.r), "Nearest pose must win regardless of submission order.");
                Assert.That(first.g, Is.GreaterThan(.6f), "Transparent ghost tint must be alpha blended over the base layer.");

                Texture2D path = MakeImage(new Color(0f, 0f, 1f, .75f), Color.clear);
                try
                {
                    int blend = shader.FindKernel("BlendLayer");
                    shader.SetTexture(blend, "_LayerColor", path); shader.SetTexture(blend, "_AccumColor", accum);
                    shader.Dispatch(blend, 1, 1, 1);
                    Color withPath = ReadPixel(accum);
                    Assert.That(withPath.b, Is.GreaterThan(0f), "Trajectory/path layer must remain visible.");
                }
                finally { Object.DestroyImmediate(path); }
            }
            finally
            {
                Object.DestroyImmediate(farColor); Object.DestroyImmediate(nearColor);
                Object.DestroyImmediate(farDepth); Object.DestroyImmediate(nearDepth);
                RenderTexture.ReleaseTemporary(accum); RenderTexture.ReleaseTemporary(depth); RenderTexture.ReleaseTemporary(baseColor);
            }
        }

        private static void DispatchPose(ComputeShader shader, RenderTexture accum, RenderTexture depth, RenderTexture baseColor, Texture2D color, Texture2D poseDepth, float alpha)
        {
            int kernel = shader.FindKernel("CompositePose");
            shader.SetFloat("_PoseAlpha", alpha);
            shader.SetTexture(kernel, "_PoseColor", color); shader.SetTexture(kernel, "_PoseDepth", poseDepth);
            shader.SetTexture(kernel, "_BaseColor", baseColor);
            shader.SetTexture(kernel, "_AccumColor", accum); shader.SetTexture(kernel, "_AccumDepth", depth);
            shader.Dispatch(kernel, 1, 1, 1);
        }

        private static RenderTexture Make(int width, int height, RenderTextureFormat format, bool randomWrite)
        {
            var texture = RenderTexture.GetTemporary(width, height, 0, format);
            texture.Release(); texture.enableRandomWrite = randomWrite; texture.Create();
            return texture;
        }

        private static Texture2D MakeImage(Color first, Color second)
        {
            var image = new Texture2D(2, 1, TextureFormat.RGBA32, false);
            image.SetPixels(new[] { first, second }); image.Apply(false, false); return image;
        }

        private static void SetDepth(Texture2D image, float value)
        {
            image.SetPixels(new[] { new Color(value, 0f, 0f, 1f), new Color(value, 0f, 0f, 1f) }); image.Apply(false, false);
        }

        private static Texture2D Read(RenderTexture source)
        {
            RenderTexture previous = RenderTexture.active; RenderTexture.active = source;
            var image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0); image.Apply(false, false);
            RenderTexture.active = previous; return image;
        }

        private static Color ReadPixel(RenderTexture source)
        {
            Texture2D image = Read(source);
            try { return image.GetPixel(0, 0); }
            finally { Object.DestroyImmediate(image); }
        }
    }
}
