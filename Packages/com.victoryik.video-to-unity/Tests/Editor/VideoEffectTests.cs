using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace VideoToUnity.Tests
{
    public sealed class VideoEffectTests
    {
        [TestCase("../escape.mp4")]
        [TestCase("folder/../../escape.mp4")]
        [TestCase("/absolute.mp4")]
        [TestCase("C:/absolute.mp4")]
        public void StreamingPathsCannotEscapeRoot(string path)
        {
            var asset = ScriptableObject.CreateInstance<VideoEffectAsset>();
            try { asset.source = VideoSourceKind.StreamingAssets; asset.location = path; Assert.IsFalse(asset.Validate(out _)); }
            finally { UnityEngine.Object.DestroyImmediate(asset); }
        }

        [Test]
        public void StreamingPathPreservesUnicodeAndReservedCharacters()
        {
            var asset = ScriptableObject.CreateInstance<VideoEffectAsset>();
            try
            {
                asset.source = VideoSourceKind.StreamingAssets; asset.location = "Video/한글 & #effect.mp4";
                Assert.IsTrue(asset.Validate(out _));
                string resolved = asset.ResolveUrl();
                Assert.That(resolved, Does.Contain("%23"));
                Assert.That(Uri.UnescapeDataString(resolved), Does.EndWith("/Video/한글 & #effect.mp4"));
            }
            finally { UnityEngine.Object.DestroyImmediate(asset); }
        }

        [Test]
        public void MissingVideoIsActionable()
        {
            var asset = ScriptableObject.CreateInstance<VideoEffectAsset>();
            try { Assert.IsFalse(asset.Validate(out string reason)); Assert.That(reason, Does.Contain("VideoClip")); }
            finally { UnityEngine.Object.DestroyImmediate(asset); }
        }

        [Test]
        public void AspectFitKeepsGeometry()
        {
            Assert.That(VideoEffectView.AspectScale(1, 2, VideoFit.Contain), Is.EqualTo(new Vector2(2, 1)));
            Assert.That(VideoEffectView.AspectScale(1, 2, VideoFit.Cover), Is.EqualTo(new Vector2(1, 0.5f)));
            Assert.That(VideoEffectView.AspectScale(2, 1, VideoFit.Stretch), Is.EqualTo(Vector2.one));
        }

        [Test]
        public void ViewDoesNotPersistDisabledImageAndRestoresOriginalMaterial()
        {
            var go = new GameObject("View test", typeof(RectTransform), typeof(RawImage));
            var raw = go.GetComponent<RawImage>();
            raw.texture = Texture2D.whiteTexture;
            Material original = raw.material;
            try
            {
                var view = go.AddComponent<VideoEffectView>();
                Assert.IsTrue(raw.enabled, "Transient hiding must not serialize RawImage.enabled=false.");
                Assert.AreSame(original, raw.material, "The serialized material must stay untouched.");
                Assert.AreSame(Texture2D.whiteTexture, raw.texture);
                Assert.AreNotSame(original, raw.materialForRendering);
                view.enabled = false;
                Assert.AreSame(original, raw.material);
                Assert.AreSame(Texture2D.whiteTexture, raw.texture);
                Assert.IsTrue(raw.enabled);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void NativeAlphaPreservesOpaqueBlackAndHalfAlpha()
        {
            var samples = new[] { new Color(1,0,0,0), new Color(0,0,0,1), new Color(1,0,0,.5f), Color.green };
            Color[] output = Render(samples, VideoAlphaMode.NativeAlpha, Color.blue, 4);
            Near(output[0], Color.blue); Near(output[1], Color.black);
            Near(output[2], new Color(.5f,0,.5f,1)); Near(output[3], Color.green);
        }

        [Test]
        public void PackedAlphaRestoresBlackAndTransparency()
        {
            var samples = new[] { Color.white, Color.black, Color.red, Color.green,
                Color.black, Color.white, new Color(.5f,.5f,.5f,1), Color.white };
            Color[] output = Render(samples, VideoAlphaMode.PackedAlpha, Color.blue, 4);
            Near(output[0], Color.blue); Near(output[1], Color.black);
            Near(output[2], new Color(.5f,0,.5f,1)); Near(output[3], Color.green);
        }

        [Test]
        public void AdditiveKeepsBackgroundAndBlackContributesNothing()
        {
            Color background = new Color(0,.25f,.5f,1);
            Color[] output = Render(new[] { Color.black, new Color(.25f,0,0,1) }, VideoAlphaMode.Additive, background, 2);
            Near(output[0], background); Near(output[1], new Color(.25f,.25f,.5f,1));
        }

        [Test]
        public void KeyModesRemoveTheirBackground()
        {
            Color[] black = Render(new[] { Color.black, Color.red }, VideoAlphaMode.BlackKey, Color.blue, 2);
            Near(black[0], Color.blue); Near(black[1], Color.red);
            Color[] chroma = Render(new[] { Color.green, Color.red }, VideoAlphaMode.ChromaKey, Color.blue, 2);
            Near(chroma[0], Color.blue); Near(chroma[1], Color.red);
        }

        [Test]
        public void OpaqueIgnoresSourceAlpha()
        {
            Near(Render(new[] { new Color(1,0,0,0) }, VideoAlphaMode.Opaque, Color.blue, 1)[0], Color.red);
        }

        private static Color[] Render(Color[] samples, VideoAlphaMode mode, Color background, int width)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires a real graphics device.");
            var shader = Resources.Load<Shader>("VideoToUnity/VideoComposite");
            Assert.NotNull(shader);
            Assert.IsTrue(shader.isSupported);
            var source = new Texture2D(samples.Length, 1, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
            source.SetPixels(samples); source.Apply();
            var target = new RenderTexture(width, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var material = new Material(shader);
            var asset = ScriptableObject.CreateInstance<VideoEffectAsset>();
            var read = new Texture2D(width, 1, TextureFormat.RGBA32, false, true);
            var previous = RenderTexture.active;
            bool previousSRGB = GL.sRGBWrite;
            try
            {
                asset.alphaMode = mode; asset.correctPackedAlphaGamma = false;
                VideoEffectView.ApplyMaterial(material, asset, 1, Vector2.one);
                target.Create(); RenderTexture.active = target; GL.sRGBWrite = false; GL.Clear(false, true, background);
                Graphics.Blit(source, target, material);
                RenderTexture.active = target; read.ReadPixels(new Rect(0,0,width,1),0,0); read.Apply();
                return read.GetPixels();
            }
            finally
            {
                GL.sRGBWrite = previousSRGB; RenderTexture.active = previous;
                target.Release();
                UnityEngine.Object.DestroyImmediate(source); UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(asset); UnityEngine.Object.DestroyImmediate(read);
            }
        }

        private static void Near(Color actual, Color expected)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(.035f));
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(.035f));
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(.035f));
            Assert.That(actual.a, Is.EqualTo(expected.a).Within(.035f));
        }
    }
}
