using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.UIElements;
using UnityEngine.Video;
using VideoToUnity.Editor;
using UO = UnityEngine.Object;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace VideoToUnity.Tests
{
    public sealed class EditorIntegrationTests
    {
        private string folder, package;
        private Scene scene;
        private VideoEffectAsset effect;

        [SetUp] public void SetUp()
        {
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), ".video-to-unity-validation")))
                Assert.Ignore("Scene lifecycle checks require a dedicated project with a .video-to-unity-validation marker.");
            folder = "Assets/VideoToUnityCheck_" + Guid.NewGuid().ToString("N");
            Assert.IsNotEmpty(AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder)));
            package = PackageInfo.FindForAssembly(typeof(VideoEffectAsset).Assembly).resolvedPath;
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SceneManager.SetActiveScene(scene);
            effect = ScriptableObject.CreateInstance<VideoEffectAsset>();
            effect.source = VideoSourceKind.Url;
            effect.location = new Uri(Path.Combine(package, "Samples~/Basic/Media/packed-alpha.mp4")).AbsoluteUri;
            effect.alphaMode = VideoAlphaMode.PackedAlpha;
            effect.contentSize = new Vector2Int(128, 64);
            AssetDatabase.CreateAsset(effect, folder + "/effect.asset");
        }

        [TearDown] public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Undo.ClearAll(); // Dedicated validation project only.
            Assert.IsTrue(AssetDatabase.DeleteAsset(folder));
        }

        [Test] public void CreationUndoRedoKeepsTheImageEnabled()
        {
            var player = VideoEffectObjectFactory.Create(effect, VideoEffectPlacement.ScreenOverlay, playOnEnable: false);
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, player.GetComponent<Canvas>().renderMode);
            Assert.IsFalse(player.GetComponentInChildren<RawImage>().raycastTarget);
            Assert.IsFalse(player.GetComponent<CanvasGroup>().blocksRaycasts);
            Undo.PerformUndo();
            Assert.AreEqual(0, scene.rootCount);
            Undo.PerformRedo();
            Assert.AreEqual(1, scene.rootCount);
            var restored = scene.GetRootGameObjects()[0];
            Assert.IsTrue(restored.GetComponentInChildren<RawImage>().enabled);
            Assert.AreSame(effect, restored.GetComponent<VideoEffectPlayer>().effect);
        }

        [Test] public void SceneReloadPreservesTheOriginalImageReferences()
        {
            var player = VideoEffectObjectFactory.Create(effect, VideoEffectPlacement.ScreenOverlay, playOnEnable: false);
            var image = player.GetComponentInChildren<RawImage>();
            var material = new Material(Shader.Find("UI/Default"));
            var texture = new Texture2D(2, 2);
            AssetDatabase.CreateAsset(material, folder + "/base.mat");
            AssetDatabase.CreateAsset(texture, folder + "/base.asset");
            image.material = material; image.texture = texture;
            image.GetComponent<VideoEffectView>().Refresh();
            string path = folder + "/saved.unity";
            Assert.IsTrue(EditorSceneManager.SaveScene(scene, path));
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            image = scene.GetRootGameObjects()[0].GetComponentInChildren<RawImage>();
            Assert.IsTrue(image.enabled);
            Assert.AreEqual(folder + "/base.mat", AssetDatabase.GetAssetPath(image.material));
            Assert.AreEqual(folder + "/base.asset", AssetDatabase.GetAssetPath(image.texture));
            Assert.AreSame(effect, image.GetComponent<VideoEffectView>().player.effect);
            Assert.AreNotSame(image.material, image.materialForRendering);
        }

        [Test] public void ExistingCanvasAndWorldPlacementUseTheRequestedParentAndWidth()
        {
            var canvas = new GameObject("Existing Canvas", typeof(RectTransform), typeof(Canvas)).GetComponent<Canvas>();
            var child = VideoEffectObjectFactory.Create(effect, VideoEffectPlacement.ExistingCanvas, canvas, playOnEnable: false);
            Assert.AreSame(canvas.transform, child.transform.parent);
            var world = VideoEffectObjectFactory.Create(effect, VideoEffectPlacement.WorldSpaceCanvas, playOnEnable: false, worldWidth: 2);
            Assert.AreEqual(RenderMode.WorldSpace, world.GetComponent<Canvas>().renderMode);
            var corners = new Vector3[4];
            world.GetComponent<RectTransform>().GetWorldCorners(corners);
            Assert.That(Vector3.Distance(corners[1], corners[2]), Is.EqualTo(2).Within(.001));
        }

        [Test] public void EditorWindowBuildsItsToolkitTreeWithoutOpeningAWindow()
        {
            var window = ScriptableObject.CreateInstance<VideoEffectWindow>();
            try
            {
                window.CreateGUI();
                Assert.That(window.rootVisualElement.Query<UnityEngine.UIElements.Button>().ToList().Count, Is.GreaterThan(5));
                Assert.That(window.rootVisualElement.styleSheets.count, Is.GreaterThan(0));
                Assert.That(window.rootVisualElement.Query<UnityEditor.UIElements.ObjectField>().ToList().Count, Is.GreaterThan(2));
            }
            finally { UO.DestroyImmediate(window); }
        }

        [Test] public void PackedImportDisablesDefaultAndExistingStandaloneTranscoding()
        {
            string path = ImportMedia("packed-alpha.mp4");
            var importer = (VideoClipImporter)AssetImporter.GetAtPath(path);
            var settings = importer.defaultTargetSettings;
            settings.enableTranscoding = true;
            importer.SetTargetSettings("Standalone", settings);
            importer.SaveAndReimport();
            VideoEffectImportSettings.Apply(AssetDatabase.LoadAssetAtPath<VideoClip>(path), VideoAlphaMode.PackedAlpha);
            importer = (VideoClipImporter)AssetImporter.GetAtPath(path);
            Assert.IsFalse(importer.defaultTargetSettings.enableTranscoding);
            Assert.IsFalse(importer.GetTargetSettings("Standalone").enableTranscoding);
            Assert.IsFalse(importer.keepAlpha);
        }

        [UnityTest] public IEnumerator NativeAlphaImportAndPlaybackPreserveCoverage()
        {
            string path = ImportMedia("native-alpha.webm");
            VideoEffectImportSettings.Apply(AssetDatabase.LoadAssetAtPath<VideoClip>(path), VideoAlphaMode.NativeAlpha);
            var importer = (VideoClipImporter)AssetImporter.GetAtPath(path);
            Assert.IsTrue(importer.sourceHasAlpha);
            Assert.IsTrue(importer.keepAlpha);
            Assert.IsTrue(importer.defaultTargetSettings.enableTranscoding);
            effect.source = VideoSourceKind.VideoClip;
            effect.clip = AssetDatabase.LoadAssetAtPath<VideoClip>(path);
            effect.alphaMode = VideoAlphaMode.NativeAlpha;
            var player = VideoEffectObjectFactory.Create(effect, VideoEffectPlacement.ScreenOverlay, playOnEnable: false);
            player.Play();
            double until = EditorApplication.timeSinceStartup + 25;
            while (player.Texture == null && EditorApplication.timeSinceStartup < until)
            {
                Assert.IsNull(player.LastError, player.LastError);
                EditorApplication.QueuePlayerLoopUpdate();
                yield return null;
            }
            Assert.IsNotNull(player.Texture, player.LastError);
            player.Pause();
            var material = new Material(Resources.Load<Shader>("VideoToUnity/VideoComposite"));
            var rt = new RenderTexture(128, 64, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var read = new Texture2D(128, 64, TextureFormat.RGBA32, false, true);
            var prior = RenderTexture.active;
            try
            {
                rt.Create(); RenderTexture.active = rt; GL.Clear(false, true, Color.blue);
                VideoEffectView.ApplyMaterial(material, effect, 1, Vector2.one);
                Graphics.Blit(player.Texture, rt, material);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, 128, 64), 0, 0); read.Apply();
                Assert.That(read.GetPixel(16, 32).b, Is.EqualTo(1).Within(.06));
                Assert.That(read.GetPixel(48, 32).maxColorComponent, Is.LessThan(.06));
                Assert.That(read.GetPixel(80, 32).r, Is.EqualTo(.5).Within(.06));
                Assert.That(read.GetPixel(80, 32).b, Is.EqualTo(.5).Within(.06));
                Assert.That(read.GetPixel(112, 32).g, Is.EqualTo(1).Within(.06));
            }
            finally
            {
                RenderTexture.active = prior;
                UO.DestroyImmediate(material); UO.DestroyImmediate(read); rt.Release(); UO.DestroyImmediate(rt);
            }
        }

        private string ImportMedia(string name)
        {
            string path = folder + "/" + name;
            File.Copy(Path.Combine(package, "Samples~/Basic/Media/" + name), path, false);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return path;
        }
    }
}
