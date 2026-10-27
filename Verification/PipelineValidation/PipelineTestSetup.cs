using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace VideoToUnity.PipelineValidation
{
    public static class PipelineTestSetup
    {
        public static void Run()
        {
            string output = PackageBootstrap.Argument("-vtuOutput");
            Directory.CreateDirectory(output);
            try
            {
                string expected = PackageBootstrap.Argument("-vtuPipeline");
                PlayerSettings.colorSpace = ColorSpace.Linear;
                PlayerSettings.runInBackground = true;
                QualitySettings.antiAliasing = 0;
                QualitySettings.vSyncCount = 0;
                var asset = QualitySettings.renderPipeline != null ? QualitySettings.renderPipeline : GraphicsSettings.defaultRenderPipeline;
                if (asset == null || (expected == "urp" && !asset.GetType().Name.Contains("Universal")) ||
                    (expected == "hdrp" && !asset.GetType().Name.Contains("HDRenderPipeline")))
                    throw new InvalidOperationException("Template did not activate expected pipeline: " + (asset == null ? "null" : asset.GetType().FullName));
                string changes = "";
                if (expected == "hdrp")
                {
                    var serialized = new SerializedObject(asset);
                    var format = serialized.FindProperty("m_RenderPipelineSettings.colorBufferFormat");
                    if (format == null) throw new InvalidOperationException("HDRP colorBufferFormat property missing.");
                    int alphaFormat = Array.FindIndex(format.enumNames, name => name.Contains("R16G16B16A16"));
                    if (alphaFormat < 0) throw new InvalidOperationException("HDRP RGBA16 color buffer format unavailable.");
                    format.enumValueIndex = alphaFormat;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(asset);
                    AssetDatabase.SaveAssetIfDirty(asset);
                    changes = "HDRP colorBufferFormat=" + format.enumNames[alphaFormat];
                }
                string packages = string.Join("\n", UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .Where(package => package.name.Contains("render-pipelines") || package.name.Contains("video-to-unity") || package.name == "com.unity.ugui")
                    .Select(package => package.name + "@" + package.version));
                File.WriteAllText(Path.Combine(output, "configuration.txt"),
                    "Unity=" + Application.unityVersion + "\nPipeline=" + asset.GetType().FullName + "\nAsset=" + AssetDatabase.GetAssetPath(asset) +
                    "\nColorSpace=" + PlayerSettings.colorSpace + "\nGraphicsAPI=" + SystemInfo.graphicsDeviceType + "\nGPU=" + SystemInfo.graphicsDeviceName +
                    "\nTarget=256x128 ARGBHalf Linear depth24 AA1\nCamera=orthographic; no scene volumes; postprocessing disabled where supported\n" + changes + "\n" + packages);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var root = new GameObject("Pipeline validation runner");
                root.AddComponent<PipelineCanvasProbe>();
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
                EditorApplication.EnterPlaymode();
            }
            catch (Exception exception)
            {
                File.WriteAllText(Path.Combine(output, "setup-failure.txt"), exception.ToString());
                Debug.LogException(exception);
                EditorApplication.Exit(3);
            }
        }
    }
}
