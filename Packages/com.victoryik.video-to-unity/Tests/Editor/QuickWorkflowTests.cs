using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using VideoToUnity.Editor;

namespace VideoToUnity.Tests
{
    public sealed class QuickWorkflowTests
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Assembly EditorAssembly = typeof(BackgroundMatte).Assembly;
        private static readonly Type Policy = EditorAssembly.GetType("VideoToUnity.Editor.QuickWorkflowPolicy", true);
        private static readonly Type InfoType = EditorAssembly.GetType("VideoToUnity.Editor.QuickVideoInfo", true);
        private static readonly Type ModeType = EditorAssembly.GetType("VideoToUnity.Editor.QuickEffectMode", true);
        private EditorWindow window;
        private readonly List<string> folders = new List<string>();
        private readonly List<string> temporaryVideos = new List<string>();
        private readonly HashSet<int> existingPlayers = new HashSet<int>();
        private static object Mode(string value) => Enum.Parse(ModeType, value);
        private static object PolicyCall(string name, params object[] args) => Policy.GetMethod(name, Static).Invoke(null, args);
        private object Field(string name) => window.GetType().GetField(name, Instance).GetValue(window);
        private void Set(string name, object value) => window.GetType().GetField(name, Instance).SetValue(window, value);
        private object Call(string name, params object[] args) => window.GetType().GetMethod(name, Instance).Invoke(window, args);

        [Test] public void RecommendationKeepsPotentialAlphaEvenWithUniformBorders()
        {
            var info = Activator.CreateInstance(InfoType, true);
            InfoType.GetField("AlphaCapable").SetValue(info, true);
            InfoType.GetField("Analysis").SetValue(info, new BackgroundAnalysis { Confidence = 1 });
            Assert.AreEqual("PreserveAlpha", PolicyCall("Recommend", info).ToString());
        }

        [Test] public void UncertainBackgroundIsNeverAutomaticallyRemoved()
        {
            var info = Activator.CreateInstance(InfoType, true);
            InfoType.GetField("Analysis").SetValue(info, new BackgroundAnalysis { Confidence = .59 });
            Assert.AreEqual("Opaque", PolicyCall("Recommend", info).ToString());
            InfoType.GetField("Analysis").SetValue(info, new BackgroundAnalysis { Confidence = .9 });
            Assert.AreEqual("RemoveBackground", PolicyCall("Recommend", info).ToString());
        }

        [TestCase("movie.MP4", true)] [TestCase("clip.mov", true)] [TestCase("picture.png", false)] [TestCase("script.exe", false)]
        public void DropFilterAcceptsOnlyVideoFiles(string path, bool expected)
        { Assert.AreEqual(expected, PolicyCall("IsVideo", path)); }

        [Test] public void PackedPreviewReadsRightHalfAlphaAndPreservesBlackArtwork()
        {
            var frame = new BackgroundPreview { Width = 4, Height = 1, OriginalRgba = new byte[]
                { 0,0,0,255, 255,0,0,255, 255,255,255,255, 128,128,128,255 } };
            var result = (BackgroundPreview)PolicyCall("Compose", frame, Mode("PackedAlpha"), new BackgroundOptions(), false, CancellationToken.None);
            Assert.AreEqual(2, result.Width);
            CollectionAssert.AreEqual(new byte[] { 0,0,0,255, 255,0,0,128 }, result.ResultRgba);
        }

        [Test] public void OpaquePreviewDoesNotModifySourceAlpha()
        {
            var frame = new BackgroundPreview { Width = 1, Height = 1, OriginalRgba = new byte[] { 3,4,5,90 } };
            var result = (BackgroundPreview)PolicyCall("Compose", frame, Mode("Opaque"), new BackgroundOptions(), false, CancellationToken.None);
            Assert.AreEqual(255, result.ResultRgba[3]); Assert.AreEqual(90, frame.OriginalRgba[3]);
        }

        private void OpenWindow()
        {
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), ".video-to-unity-validation")))
                Assert.Ignore("Requires a marked isolated validation project.");
            foreach (var player in Resources.FindObjectsOfTypeAll<VideoEffectPlayer>()) existingPlayers.Add(player.GetInstanceID());
            window = ScriptableObject.CreateInstance<BackgroundRemovalWindow>();
            Set("easyMode", true); window.Show();
            Call("CreateGUI");
            EditorAssembly.GetType("VideoToUnity.Editor.VideoToolchainSetup", true).GetMethod("Discover", Static).Invoke(null, null);
        }

        private string Fixture(string file)
        {
            string package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(VideoEffectAsset).Assembly).resolvedPath;
            return Path.Combine(package, "Samples~/BackgroundRemoval", file);
        }

        private static IEnumerator Wait(Func<bool> ready)
        {
            double deadline = EditorApplication.timeSinceStartup + 120;
            while (!ready()) { Assert.Less(EditorApplication.timeSinceStartup, deadline, "Workflow timed out."); yield return null; }
        }

        [UnityTest] public IEnumerator DragAnalyzePreviewAddAndUndoInOneWindow()
        {
            OpenWindow();
            yield return null; // Let the Editor attach and finish CreateGUI before dispatching a native drop.
            DragAndDrop.PrepareStartDrag(); DragAndDrop.paths = new[] { Fixture("flat-black.mp4") };
            using (var drop = DragPerformEvent.GetPooled())
            { var zone = window.rootVisualElement.Q("video-drop-zone"); drop.target = zone; zone.SendEvent(drop); }
            Assert.AreEqual(Path.GetFullPath(Fixture("flat-black.mp4")), (string)Field("sourcePath"), "The drop must reach its target.");
            yield return Wait(() => window.rootVisualElement.Q<Button>("add-to-scene").enabledSelf);
            Assert.AreEqual("RemoveBackground", Field("quickMode").ToString());
            Assert.NotNull(Field("previewResult"));
            Call("AddEasyToScene");
            yield return Wait(() => !(bool)Field("quickAdding"));
            Assert.IsFalse((bool)Field("quickError"), (string)Field("quickMessage"));
            var effect = (VideoEffectAsset)Field("quickSavedEffect"); Assert.NotNull(effect);
            folders.Add(Path.GetDirectoryName(AssetDatabase.GetAssetPath(effect)).Replace('\\', '/'));
            AssertSavedNameMatchesFile(effect);
            var created = Selection.activeGameObject; Assert.NotNull(created.GetComponent<VideoEffectPlayer>());
            Assert.AreSame(effect, created.GetComponent<VideoEffectPlayer>().effect);
            Assert.AreEqual("flat-black · Video Effect", created.name);
            Undo.PerformUndo(); Assert.IsTrue(created == null, "One Undo removes the generated scene object.");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<VideoEffectAsset>(AssetDatabase.GetAssetPath(effect)), "Undo preserves reusable imported assets.");
        }

        [UnityTest] public IEnumerator ChangingSourceDropsPreviousMasksAndRejectsStaleAdd()
        {
            OpenWindow(); Call("AcceptEasySource", Fixture("flat-black.mp4"));
            yield return Wait(() => window.rootVisualElement.Q<Button>("add-to-scene").enabledSelf);
            Call("AddRegion", .1f, .1f, .2f, .2f);
            Call("AcceptEasySource", Fixture("flat-green.mp4"));
            Assert.AreEqual(0, ((IList)Field("regions")).Count);
            Assert.IsNull(Field("previewResult"));
            Assert.Throws<TargetInvocationException>(() => Call("AddEasyToScene"));
            yield return Wait(() => window.rootVisualElement.Q<Button>("add-to-scene").enabledSelf);
            Assert.AreEqual(Fixture("flat-green.mp4").Replace('\\', '/'), ((string)Field("sourcePath")).Replace('\\', '/'));
            Call("CancelEasyWork");
        }

        [UnityTest] public IEnumerator AlphaAndDirectModesSaveReusableEffectsInOneWindow()
        {
            OpenWindow(); yield return null;
            string package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(VideoEffectAsset).Assembly).resolvedPath;
            string mov = Path.Combine(Path.GetTempPath(), "VideoToUnityQuick-" + Guid.NewGuid().ToString("N") + ".mov");
            temporaryVideos.Add(mov);
            var setup = EditorAssembly.GetType("VideoToUnity.Editor.VideoToolchainSetup", true);
            yield return Wait(() => !(bool)setup.GetProperty("Busy", Static).GetValue(null));
            Assert.IsTrue((bool)setup.GetProperty("Ready", Static).GetValue(null));
            string encoder = (string)setup.GetProperty("Ffmpeg", Static).GetValue(null);
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var process = EditorAssembly.GetType("VideoToUnity.Editor.BackgroundProcess", true).GetMethod("Run", Static);
                var args = new object[] { encoder, new[] { "-v", "error", "-nostdin", "-n", "-i", Fixture("flat-black.mp4"), "-map", "0:v:0", "-c:v", "copy", "-an", "-f", "mov", mov }, (Action<Stream>)(_ => { }), cancellation.Token };
                var task = System.Threading.Tasks.Task.Run(() => process.Invoke(null, args));
                yield return Wait(() => task.IsCompleted); task.GetAwaiter().GetResult();
            }
            foreach (string mode in new[] { "Opaque", "Additive", "PackedAlpha", "PreserveAlpha", "RemoveBackground" })
            {
                string source = mode == "PreserveAlpha" ? Path.Combine(package, "Samples~/Basic/Media/native-alpha.webm") :
                    mode == "PackedAlpha" ? Path.Combine(package, "Samples~/Basic/Media/packed-alpha.mp4") : mode == "RemoveBackground" ? mov : Fixture("flat-black.mp4");
                Call("AcceptEasySource", source);
                yield return Wait(() => window.rootVisualElement.Q<Button>("add-to-scene").enabledSelf);
                if (mode == "PreserveAlpha") Assert.AreEqual("PreserveAlpha", Field("quickMode").ToString(), "Native alpha must be detected automatically.");
                Set("quickMode", Mode(mode)); Call("QueueEasyPreview");
                Assert.IsFalse(window.rootVisualElement.Q<Button>("add-to-scene").enabledSelf);
                yield return Wait(() => window.rootVisualElement.Q<Button>("add-to-scene").enabledSelf);
                Call("AddEasyToScene"); yield return Wait(() => !(bool)Field("quickAdding"));
                Assert.IsFalse((bool)Field("quickError"), mode + ": " + Field("quickMessage"));
                var effect = (VideoEffectAsset)Field("quickSavedEffect"); Assert.NotNull(effect, mode);
                folders.Add(Path.GetDirectoryName(AssetDatabase.GetAssetPath(effect)).Replace('\\', '/'));
                AssertSavedNameMatchesFile(effect);
                Assert.AreEqual(Path.GetFileNameWithoutExtension(source) + " · Video Effect", Selection.activeGameObject.name, mode);
                Assert.NotNull(effect.clip); Assert.AreEqual(VideoSourceKind.VideoClip, effect.source);
                Assert.AreEqual(mode == "PreserveAlpha" || mode == "RemoveBackground" ? VideoAlphaMode.PackedAlpha : (VideoAlphaMode)Enum.Parse(typeof(VideoAlphaMode), mode), effect.alphaMode);
                Assert.IsFalse(effect.audioEnabled);
                Undo.PerformUndo();
            }
        }

        private static void AssertSavedNameMatchesFile(VideoEffectAsset effect)
        {
            string path = AssetDatabase.GetAssetPath(effect);
            string expected = Path.GetFileNameWithoutExtension(path);
            Assert.AreEqual(expected, effect.name, "The Inspector must not report a main-object name mismatch.");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            Assert.AreEqual(expected, AssetDatabase.LoadAssetAtPath<VideoEffectAsset>(path).name, "The matching name must survive reimport.");
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (window == null) yield break;
            if (window != null) { Call("CancelEasyWork"); window.Close(); }
            window = null;
            foreach (var player in Resources.FindObjectsOfTypeAll<VideoEffectPlayer>())
                if (!existingPlayers.Contains(player.GetInstanceID()) && player.gameObject.scene.IsValid()) UnityEngine.Object.DestroyImmediate(player.gameObject);
            foreach (string folder in folders) AssetDatabase.DeleteAsset(folder);
            foreach (string video in temporaryVideos) if (File.Exists(video)) File.Delete(video);
            temporaryVideos.Clear();
            folders.Clear(); existingPlayers.Clear();
            yield return null;
        }
    }
}
