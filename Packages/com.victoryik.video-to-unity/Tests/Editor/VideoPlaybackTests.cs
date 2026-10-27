using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.TestTools;
using UnityEngine.Timeline;
using VideoToUnity.Timeline;

namespace VideoToUnity.Tests
{
    // Native decoder/GPU tests. These cannot be replaced by managed-reference compilation.
    public sealed class VideoPlaybackTests
    {
        private GameObject root;
        private VideoEffectAsset effect;
        private VideoEffectPlayer player;
        private TimelineAsset sequence;

        [SetUp] public void SetUp()
        {
            string package = PackageInfo.FindForAssembly(typeof(VideoEffectAsset).Assembly).resolvedPath;
            string media = Path.Combine(package, "Samples~/Basic/Media/packed-alpha.mp4");
            Assert.IsTrue(File.Exists(media), "The package's diagnostic fixture is required.");
            effect = ScriptableObject.CreateInstance<VideoEffectAsset>();
            effect.source = VideoSourceKind.Url;
            effect.location = new Uri(media).AbsoluteUri;
            effect.alphaMode = VideoAlphaMode.PackedAlpha;
            effect.contentSize = new Vector2Int(128, 64);
            root = new GameObject("Video playback test");
            player = root.AddComponent<VideoEffectPlayer>();
            player.effect = effect;
        }

        [TearDown] public void TearDown()
        {
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            if (sequence != null) UnityEngine.Object.DestroyImmediate(sequence);
            if (effect != null) UnityEngine.Object.DestroyImmediate(effect);
        }

        [UnityTest] public IEnumerator PreparePlayPauseResumeAndStop()
        {
            int prepared = 0, started = 0;
            player.onPrepared.AddListener(() => prepared++);
            player.onStarted.AddListener(() => started++);
            player.Prepare(effect);
            yield return WaitFor(() => player.IsReady);
            Assert.IsNull(player.Texture, "Prewarming must not display a frame.");
            player.Play();
            yield return WaitFor(() => player.Texture != null && player.CurrentTime > .15);
            Assert.AreEqual(1, prepared);
            Assert.AreEqual(1, started);
            player.Pause();
            double pausedAt = player.CurrentTime;
            double until = EditorApplication.timeSinceStartup + .25;
            while (EditorApplication.timeSinceStartup < until) { EditorApplication.QueuePlayerLoopUpdate(); yield return null; }
            Assert.That(player.CurrentTime, Is.EqualTo(pausedAt).Within(.09));
            player.Resume();
            yield return WaitFor(() => player.CurrentTime > pausedAt + .12);
            player.Stop();
            Assert.IsNull(player.Texture);
            Assert.IsFalse(player.IsReady);
            Assert.IsFalse(player.IsPlaying);
        }

        [UnityTest] public IEnumerator StopDuringPreparationCanReplay()
        {
            player.Play();
            player.Stop();
            yield return null;
            Assert.IsNull(player.Texture);
            player.Play();
            yield return WaitFor(() => player.Texture != null);
            Assert.IsNull(player.LastError);
        }

        [UnityTest] public IEnumerator FinalScrubIsNotLostOrRepeatedForever()
        {
            player.SampleTimeline(effect, 0, 1, false);
            yield return WaitFor(() => player.Texture != null);
            int seeks = 0;
            player.Decoder.seekCompleted += _ => seeks++;
            player.SampleTimeline(effect, .4, 1, false);
            player.SampleTimeline(effect, 1.31, 1, false); // Deliberately not aligned to 12 fps.
            yield return WaitFor(() => Math.Abs(player.CurrentTime - 1.31) <= .09 && seeks >= 1);
            double until = EditorApplication.timeSinceStartup + .5;
            while (EditorApplication.timeSinceStartup < until) { EditorApplication.QueuePlayerLoopUpdate(); yield return null; }
            Assert.That(seeks, Is.LessThanOrEqualTo(2), "One active seek plus at most one queued latest target.");
            Assert.IsFalse(player.Decoder.isPlaying);
            player.ReleaseTimeline();
            Assert.IsNull(player.Texture);
        }

        [UnityTest] public IEnumerator TimelineAppliesClipInAndSpeedOnceAndRestoresBinding()
        {
            var original = ScriptableObject.CreateInstance<VideoEffectAsset>();
            try
            {
                player.effect = original;
                sequence = ScriptableObject.CreateInstance<TimelineAsset>();
                var track = sequence.CreateTrack<VideoEffectTrack>(null, "Video");
                var clip = track.CreateClip<VideoEffectClip>();
                ((VideoEffectClip)clip.asset).effect = effect;
                clip.start = .2; clip.clipIn = .2; clip.timeScale = 2; clip.duration = .7;
                var director = root.AddComponent<PlayableDirector>();
                director.playableAsset = sequence;
                director.SetGenericBinding(track, player);
                director.time = .7;
                director.Evaluate();
                yield return WaitFor(() => player.Texture != null && Math.Abs(player.CurrentTime - 1.2) <= .09);
                Assert.AreSame(effect, player.effect);
                director.Stop();
                Assert.AreSame(original, player.effect);
                Assert.IsNull(player.Texture);
            }
            finally { UnityEngine.Object.DestroyImmediate(original); }
        }

        [UnityTest] public IEnumerator PackedNativeDecoderPreservesCoverage()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("Requires a graphics device; a null-device run is not GPU verification.");
            player.Play();
            yield return WaitFor(() => player.Texture != null);
            player.Pause();
            var material = new Material(Resources.Load<Shader>("VideoToUnity/VideoComposite"));
            var target = new RenderTexture(128, 64, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(128, 64, TextureFormat.RGBA32, false, true);
            RenderTexture previous = RenderTexture.active;
            try
            {
                target.Create();
                RenderTexture.active = target;
                GL.Clear(false, true, Color.blue);
                VideoEffectView.ApplyMaterial(material, effect, 1, Vector2.one);
                Graphics.Blit(player.Texture, target, material);
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, 128, 64), 0, 0); readback.Apply();
                Color transparent = readback.GetPixel(16, 32), black = readback.GetPixel(48, 32);
                Color red = readback.GetPixel(80, 32), green = readback.GetPixel(112, 32);
                Assert.That(transparent.b, Is.EqualTo(1).Within(.05));
                Assert.That(black.maxColorComponent, Is.LessThan(.05));
                Assert.That(red.r, Is.EqualTo(.5).Within(.06));
                Assert.That(red.b, Is.EqualTo(.5).Within(.06), "128/255 is coverage, including in Linear projects.");
                Assert.That(green.g, Is.EqualTo(1).Within(.06));
                Assert.That(green.b, Is.LessThan(.05));
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(readback);
                target.Release(); UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private IEnumerator WaitFor(Func<bool> condition)
        {
            double until = EditorApplication.timeSinceStartup + 25;
            while (!condition() && EditorApplication.timeSinceStartup < until)
            {
                Assert.IsNull(player.LastError, player.LastError);
                EditorApplication.QueuePlayerLoopUpdate();
                yield return null;
            }
            Assert.IsTrue(condition(), "Native playback condition timed out: " + player.LastError);
        }
    }
}
