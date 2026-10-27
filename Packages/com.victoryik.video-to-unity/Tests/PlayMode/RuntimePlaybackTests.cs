using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.Video;

namespace VideoToUnity.Tests
{
    public sealed class RuntimePlaybackTests
    {
        private GameObject root;
        private VideoEffectAsset effect;
        private VideoEffectPlayer player;

        [SetUp] public void SetUp()
        {
            Assert.IsTrue(Application.isPlaying, "These tests must run in PlayMode.");
            effect = ScriptableObject.CreateInstance<VideoEffectAsset>();
            effect.source = VideoSourceKind.StreamingAssets;
            effect.location = "VideoToUnity/packed-alpha.mp4";
            effect.alphaMode = VideoAlphaMode.PackedAlpha;
            effect.contentSize = new Vector2Int(128, 64);
            root = new GameObject("Video runtime check");
            root.SetActive(false);
            player = root.AddComponent<VideoEffectPlayer>();
            player.effect = effect;
            player.playOnEnable = true;
        }

        [UnityTearDown] public IEnumerator TearDown()
        {
            Time.timeScale = 1;
            if (root != null) UnityEngine.Object.Destroy(root);
            if (effect != null) UnityEngine.Object.Destroy(effect);
            yield return null;
        }

        [UnityTest] public IEnumerator AutoPlayUsesUnscaledTimeAndDisablingReleasesTheDecoder()
        {
            Time.timeScale = 0;
            root.SetActive(true);
            yield return WaitFor(() => player.Texture != null && player.CurrentTime > .25);
            Assert.IsTrue(player.IsPlaying);
            player.enabled = false;
            Assert.IsNull(player.Texture);
            Assert.IsFalse(player.IsReady);
            Assert.IsFalse(player.Decoder.isPrepared);
        }

        [UnityTest] public IEnumerator NaturalCompletionOccursOnceAndCanHoldTheFinalFrame()
        {
            effect.holdLastFrame = true; effect.speed = 2;
            int completed = 0, stopped = 0;
            player.onCompleted.AddListener(() => completed++);
            player.onStopped.AddListener(() => stopped++);
            root.SetActive(true);
            yield return WaitFor(() => completed == 1);
            yield return new WaitForSecondsRealtime(.2f);
            Assert.AreEqual(1, completed);
            Assert.IsNotNull(player.Texture);
            Assert.IsFalse(player.IsPlaying);
            player.Stop();
            Assert.AreEqual(1, stopped);
            Assert.AreEqual(1, completed);
            Assert.IsNull(player.Texture);
        }

        [UnityTest] public IEnumerator LoopingRaisesLoopEventsWithoutCompletion()
        {
            effect.loop = true; effect.speed = 4;
            int loops = 0, completed = 0;
            player.onLooped.AddListener(() => loops++);
            player.onCompleted.AddListener(() => completed++);
            root.SetActive(true);
            yield return WaitFor(() => loops >= 2);
            Assert.AreEqual(0, completed);
            Assert.IsTrue(player.IsPlaying);
        }

        [UnityTest] public IEnumerator MultipleViewsShareTheSameDecoderTexture()
        {
            var canvas = Canvas();
            var first = View(canvas.transform);
            var second = View(canvas.transform);
            root.SetActive(true);
            yield return WaitFor(() => player.Texture != null);
            first.Refresh(); second.Refresh();
            Assert.AreSame(player.Texture, first.DisplayMaterial.GetTexture("_VideoTex"));
            Assert.AreSame(player.Texture, second.DisplayMaterial.GetTexture("_VideoTex"));
            Assert.AreEqual(1, root.GetComponentsInChildren<VideoPlayer>(true).Length);
            Assert.IsNull(first.GetComponent<RawImage>().texture, "No decoder texture is serialized on the RawImage.");
        }

        [UnityTest] public IEnumerator WorldCanvasRendersVideoAndRespectsRectAndStencilMasks()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Requires a real GPU.");
            if (GraphicsSettings.currentRenderPipeline != null) Assert.Ignore("This direct Camera.Render check targets Built-in.");
            var camera = new GameObject("Validation Camera").AddComponent<Camera>();
            camera.transform.SetParent(root.transform, false);
            camera.transform.localPosition = new Vector3(0, 0, -10);
            camera.orthographic = true; camera.orthographicSize = .5f; camera.aspect = 2;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.blue;
            camera.cullingMask = 1 << 8;
            var canvas = Canvas(); canvas.worldCamera = camera;
            var holder = new GameObject("Mask area", typeof(RectTransform));
            holder.layer = 8; holder.transform.SetParent(canvas.transform, false);
            var holderRect = holder.GetComponent<RectTransform>(); holderRect.sizeDelta = new Vector2(1, 1);
            var view = View(holder.transform);
            var rect = view.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f); rect.sizeDelta = new Vector2(2, 1);
            var target = new RenderTexture(128, 64, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var read = new Texture2D(128, 64, TextureFormat.RGBA32, false, true);
            target.Create(); camera.targetTexture = target;
            try
            {
                root.SetActive(true);
                yield return WaitFor(() => player.Texture != null);
                player.Pause();
                Render(camera, view, target, read);
                Assert.That(read.GetPixel(48, 32).maxColorComponent, Is.LessThan(.06));
                Assert.That(read.GetPixel(80, 32).b, Is.EqualTo(.5).Within(.06));
                Assert.That(read.GetPixel(112, 32).g, Is.EqualTo(1).Within(.06));
                var rectangle = holder.AddComponent<RectMask2D>();
                yield return null;
                Render(camera, view, target, read);
                Assert.That(read.GetPixel(48, 32).maxColorComponent, Is.LessThan(.06));
                Assert.That(read.GetPixel(112, 32).b, Is.EqualTo(1).Within(.06));
                UnityEngine.Object.Destroy(rectangle);
                yield return null;
                holder.AddComponent<Image>();
                holder.AddComponent<Mask>().showMaskGraphic = false;
                yield return null;
                Render(camera, view, target, read);
                Assert.That(read.GetPixel(48, 32).maxColorComponent, Is.LessThan(.06));
                Assert.That(read.GetPixel(112, 32).b, Is.EqualTo(1).Within(.06));
            }
            finally
            {
                camera.targetTexture = null;
                target.Release(); UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(read);
            }
        }

        private Canvas Canvas()
        {
            var go = new GameObject("Video Canvas", typeof(RectTransform), typeof(Canvas));
            go.layer = 8; go.transform.SetParent(root.transform, false);
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(2, 1);
            var canvas = go.GetComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace;
            return canvas;
        }

        private VideoEffectView View(Transform parent)
        {
            var go = new GameObject("View", typeof(RectTransform), typeof(RawImage));
            go.layer = 8; go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var view = go.AddComponent<VideoEffectView>(); view.player = player; view.fit = VideoFit.Stretch;
            return view;
        }

        private static void Render(Camera camera, VideoEffectView view, RenderTexture target, Texture2D read)
        {
            UnityEngine.Canvas.ForceUpdateCanvases(); view.Refresh(); camera.Render();
            var previous = RenderTexture.active;
            try { RenderTexture.active = target; read.ReadPixels(new Rect(0, 0, 128, 64), 0, 0); read.Apply(); }
            finally { RenderTexture.active = previous; }
        }

        private IEnumerator WaitFor(Func<bool> condition)
        {
            double until = Time.realtimeSinceStartupAsDouble + 25;
            while (!condition() && Time.realtimeSinceStartupAsDouble < until)
            { Assert.IsNull(player.LastError, player.LastError); yield return null; }
            Assert.IsTrue(condition(), "Native playback timed out: " + player.LastError);
        }
    }
}
