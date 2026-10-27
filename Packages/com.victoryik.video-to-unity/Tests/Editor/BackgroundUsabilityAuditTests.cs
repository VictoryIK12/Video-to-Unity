using System;
using System.Reflection;
using NUnit.Framework;
#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using VideoToUnity.Editor;
#endif

namespace VideoToUnity.Tests
{
    // Also executed outside Unity by Verification/PublicRelease/RunUsabilityChecks.ps1.
    public sealed class BackgroundUsabilityGeometryTests
    {
        private static readonly Type Model = Type.GetType("VideoToUnity.Editor.BackgroundRemovalUiModel, VideoToUnity.Editor", true);
        private static readonly Type Rect = Model.Assembly.GetType("VideoToUnity.Editor.UiRect", true);
        private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
        private static object Call(string name, params object[] args) => Model.GetMethod(name, Static).Invoke(null, args);
        private static object Bounds(float x, float y, float width, float height) => Activator.CreateInstance(Rect,
            BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { x, y, width, height }, null);
        private static byte[] Pixels => new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 12, 34, 56, 255 };

        private static byte[] Sample(float x, float y, object bounds = null)
        {
            object[] args = { Pixels, 2, 2, bounds ?? Bounds(10, 20, 100, 100), x, y, (byte)0, (byte)0, (byte)0 };
            Assert.IsTrue((bool)Call("TrySampleOriginal", args));
            return new[] { (byte)args[6], (byte)args[7], (byte)args[8] };
        }

        [Test] public void ColorPickerUsesTopLeftBufferOrigin()
        {
            CollectionAssert.AreEqual(new byte[] { 255, 0, 0 }, Sample(10, 20));
            CollectionAssert.AreEqual(new byte[] { 0, 0, 255 }, Sample(10, 119));
        }

        [Test] public void ColorPickerClampsInclusiveRightAndBottomEdges()
        { CollectionAssert.AreEqual(new byte[] { 12, 34, 56 }, Sample(110, 120)); }

        [Test] public void ColorPickerRejectsLetterboxAndInvalidCoordinates()
        {
            foreach (float x in new[] { -1f, 9f, 111f, float.NaN, float.PositiveInfinity })
            {
                object[] args = { Pixels, 2, 2, Bounds(10, 20, 100, 100), x, 30f, (byte)0, (byte)0, (byte)0 };
                Assert.IsFalse((bool)Call("TrySampleOriginal", args));
            }
        }

        [Test] public void ColorPickerRejectsMalformedBuffersWithoutReading()
        {
            foreach (byte[] pixels in new[] { null, new byte[15], new byte[17] })
            {
                object[] args = { pixels, 2, 2, Bounds(0, 0, 100, 100), 1f, 1f, (byte)0, (byte)0, (byte)0 };
                Assert.IsFalse((bool)Call("TrySampleOriginal", args));
            }
            object[] huge = { Pixels, int.MaxValue, int.MaxValue, Bounds(0, 0, 1, 1), 0f, 0f, (byte)0, (byte)0, (byte)0 };
            Assert.IsFalse((bool)Call("TrySampleOriginal", huge));
        }

        [Test] public void PortraitPreviewSampleAccountsForHorizontalLetterbox()
        {
            object fitted = Call("Fit", 50f, 100f, 200f, 100f);
            CollectionAssert.AreEqual(new byte[] { 255, 0, 0 }, Sample(75, 0, fitted));
            CollectionAssert.AreEqual(new byte[] { 12, 34, 56 }, Sample(125, 100, fitted));
        }

        [Test] public void FullRegionListCannotAllocateAnotherRegion()
        {
            Assert.IsTrue((bool)Call("CanAddRegion", 63));
            Assert.IsFalse((bool)Call("CanAddRegion", 64));
            Assert.IsFalse((bool)Call("CanAddRegion", 65));
            Assert.IsFalse((bool)Call("CanAddRegion", -1));
        }

        [Test] public void RegionNormalizationRemainsFiniteAndInsideImage()
        {
            object region = Call("ClampRegion", float.NaN, float.PositiveInfinity, float.NegativeInfinity, 3f);
            var type = region.GetType();
            Assert.IsTrue((bool)type.GetProperty("IsValid", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(region));
        }

        [Test] public void InvalidPreviewGeometryCannotBeSampled()
        {
            object[] args = { Pixels, 2, 2, Bounds(0, 0, 0, 100), 0f, 1f, (byte)0, (byte)0, (byte)0 };
            Assert.IsFalse((bool)Call("TrySampleOriginal", args));
        }

        [Test] public void UnequalStagesKeepPreviewBoundsInsideTheirOwnContent()
        {
            object original = Call("FitInContent", 160f, 96f, Bounds(1, 1, 900, 250));
            object result = Call("FitInContent", 160f, 96f, Bounds(1, 1, 300, 250));
            float Read(object rect, string name) => (float)Rect.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rect);
            Assert.That(Read(original, "X"), Is.GreaterThan(200));
            Assert.AreEqual(1f, Read(result, "X"));
            Assert.That(Read(result, "X") + Read(result, "Width"), Is.LessThanOrEqualTo(301f));
            Assert.That(Read(result, "Y") + Read(result, "Height"), Is.LessThanOrEqualTo(251f));
            CollectionAssert.AreEqual(new byte[] { 255, 0, 0 }, Sample(Read(result, "X"), Read(result, "Y"), result));
        }
    }

#if UNITY_EDITOR
    public sealed class BackgroundUsabilityAuditTests
    {
        private BackgroundRemovalWindow window;
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type Window = typeof(BackgroundRemovalWindow);
        private object Get(string name) => Window.GetField(name, Flags).GetValue(window);
        private void Set(string name, object value) => Window.GetField(name, Flags).SetValue(window, value);
        private object Call(string name, params object[] args) => Window.GetMethod(name, Flags).Invoke(window, args);

        [SetUp] public void SetUp()
        {
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), ".video-to-unity-validation")))
                Assert.Ignore("Use an isolated marked validation project.");
            var operation = Window.Assembly.GetType("VideoToUnity.Editor.BackgroundRemovalOperation", true);
            Assert.IsFalse((bool)operation.GetProperty("Busy", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
            window = ScriptableObject.CreateInstance<BackgroundRemovalWindow>();
            window.CreateGUI();
        }

        [TearDown] public void TearDown()
        { if (window != null) UnityEngine.Object.DestroyImmediate(window); }

        [Test] public void LocalValidationErrorSurvivesIdlePolling()
        {
            Call("OperationChanged");
            Call("SetStatus", "Keep this actionable error", HelpBoxMessageType.Error);
            Call("PollOperation");
            Assert.AreEqual("Keep this actionable error", ((HelpBox)Get("statusBox")).text);
        }

        [Test] public void FullRegionListPreservesEveryRegionOnRejectedAdd()
        {
            for (int i = 0; i < 64; i++) Assert.IsTrue((bool)Call("AddRegion", .1f, .2f, .3f, .4f));
            var list = (IList)Get("regions");
            object last = list[63];
            Assert.IsFalse((bool)Call("AddRegion", .5f, .6f, .1f, .1f));
            Assert.AreEqual(64, list.Count);
            Assert.AreSame(last, list[63]);
        }

        [UnityTest] public IEnumerator NumericRegionFieldShowsClampedStoredValue()
        {
            window.Show();
            yield return null;
            Assert.NotNull(window.rootVisualElement.panel, "Value changes require a native UI Toolkit panel.");
            Call("AddRegion", .1f, .1f, .2f, .2f);
            var field = window.rootVisualElement.Q("region-rows").Query<FloatField>().ToList()[0];
            field.value = -3f;
            var region = ((IList)Get("regions"))[0];
            Assert.AreEqual(0f, (float)region.GetType().GetField("x").GetValue(region));
            Assert.AreEqual(0f, field.value);
        }

        [Test] public void ChangedSettingsMarkPreviewAsStale()
        {
            Set("previewResult", new BackgroundResult { Preview = new BackgroundPreview() });
            Call("MarkPreviewStale");
            Assert.IsTrue((bool)Get("previewStale"));
            StringAssert.Contains("이전 미리보기", window.rootVisualElement.Q<Label>("preview-freshness").text);
        }

        [Test] public void SourceChangeClearsColorPickerAndPreview()
        {
            var picker = window.rootVisualElement.Q<Toggle>("pick-background-color");
            picker.SetValueWithoutNotify(true);
            Set("previewResult", new BackgroundResult { Preview = new BackgroundPreview() });
            Call("ChangeSource", "different-source.mp4");
            Assert.IsFalse(picker.value);
            Assert.IsNull(Get("previewResult"));
            Assert.IsFalse((bool)Get("previewStale"));
        }

        [Test] public void ManualBlackAndGreenPresetsRetainProtectedRegions()
        {
            Call("AddRegion", .2f, .3f, .25f, .4f);
            var list = (IList)Get("regions");
            object original = list[0];
            Assert.NotNull(window.rootVisualElement.Q<Button>("use-black-background"));
            Assert.NotNull(window.rootVisualElement.Q<Button>("use-green-background"));
            foreach (Color color in new[] { Color.black, Color.green })
            {
                Set("autoDetect", true);
                Call("UseManualColor", color);
                Assert.IsFalse((bool)Get("autoDetect"));
                Assert.AreEqual(color, (Color)Get("manualColor"));
                Assert.AreEqual(1, list.Count);
                Assert.AreSame(original, list[0]);
            }
        }
    }
#endif
}
