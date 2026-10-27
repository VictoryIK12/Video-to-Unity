using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace VideoToUnity.PipelineValidation
{
    public sealed class PipelineCanvasProbe : MonoBehaviour
    {
        [Serializable] public sealed class Sample
        {
            public string name;
            public int x, y;
            public Color actual, expected;
            public float maxError;
            public bool passed;
        }
        [Serializable] public sealed class Case
        {
            public string name, renderMode, mask, capture;
            public bool passed;
            public long decoderFrame;
            public double decoderTime;
            public int cameraFrames;
            public List<Sample> samples = new List<Sample>();
        }
        [Serializable] public sealed class Report
        {
            public string pipeline, unity, gpu, graphicsApi, colorSpace, pipelineAsset, renderPipelineInstance;
            public string fixture, fixtureSha256;
            public string method = "Native VideoPlayer -> VideoEffectPlayer/View -> actual uGUI Canvas -> SRP camera.targetTexture; CPU ReadPixels after rendered frames. No Graphics.Blit.";
            public string error = "";
            public int width = 256, height = 128;
            public float tolerance = .07f;
            public bool passed;
            public List<string> settings = new List<string>();
            public List<Case> cases = new List<Case>();
        }

        private Camera camera;
        private RenderTexture target;
        private VideoEffectPlayer player;
        private VideoEffectAsset effect;
        private GameObject canvasObject;
        private Report report;
        private string output;
        private int cameraFrames;
        private double deadline;
        private bool finished;

        private void Start()
        {
            output = Argument("-vtuOutput");
            report = new Report
            {
                pipeline = Argument("-vtuPipeline"), unity = Application.unityVersion, gpu = SystemInfo.graphicsDeviceName,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(), colorSpace = QualitySettings.activeColorSpace.ToString(), fixture = Argument("-vtuFixtureName"),
                pipelineAsset = GraphicsSettings.currentRenderPipeline == null ? "null" : GraphicsSettings.currentRenderPipeline.GetType().FullName
            };
            using (var hash = SHA256.Create())
            using (var stream = File.OpenRead(Path.Combine(Application.streamingAssetsPath, report.fixture)))
                report.fixtureSha256 = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
            deadline = Time.realtimeSinceStartupAsDouble + 180;
            RenderPipelineManager.endCameraRendering += Rendered;
            try { Configure(); StartCoroutine(RunCases()); }
            catch (Exception exception) { Finish(exception.ToString()); }
        }

        private void Update()
        {
            if (!finished && Time.realtimeSinceStartupAsDouble > deadline)
                Finish("Native render/video timeout. Camera frames=" + cameraFrames + "; decoder error=" + (player == null ? "none" : player.LastError));
        }

        private void Configure()
        {
            Application.runInBackground = true;
            Application.targetFrameRate = 60;
            target = new RenderTexture(256, 128, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            { antiAliasing = 1, useMipMap = false, name = "PipelineCanvasOutput" };
            target.Create();
            camera = new GameObject("Native pipeline camera").AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = .5f;
            camera.aspect = 2;
            camera.transform.position = new Vector3(0, 0, -10);
            camera.nearClipPlane = .1f;
            camera.farClipPlane = 100;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0, 0, 1, 0);
            camera.allowHDR = true;
            camera.allowMSAA = false;
            camera.targetTexture = target;
            ConfigurePipelineCamera();
            effect = ScriptableObject.CreateInstance<VideoEffectAsset>();
            effect.source = VideoSourceKind.StreamingAssets;
            effect.location = report.fixture;
            effect.alphaMode = VideoAlphaMode.PackedAlpha;
            effect.contentSize = new Vector2Int(128, 64);
            effect.loop = true;
            effect.audioEnabled = false;
            player = new GameObject("Native video player").AddComponent<VideoEffectPlayer>();
            player.effect = effect;
            player.Play();
        }

        private void ConfigurePipelineCamera()
        {
            string typeName = report.pipeline == "urp" ? "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData" : "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData";
            Type type = FindType(typeName);
            if (type == null) throw new InvalidOperationException("Missing pipeline camera data type " + typeName);
            var data = camera.gameObject.AddComponent(type);
            if (report.pipeline == "urp")
            {
                Set(data, "renderPostProcessing", false);
                Set(data, "allowHDROutput", false);
                SetEnum(data, "antialiasing", "None");
            }
            else
            {
                SetEnum(data, "clearColorMode", "Color");
                Set(data, "backgroundColorHDR", new Color(0, 0, 1, 0));
                SetEnum(data, "antialiasing", "None");
                Set(data, "volumeLayerMask", (LayerMask)0);
                // The own project receives a minimal camera override; the plugin is untouched.
                ConfigureHdrpFrameSettings(data);
            }
        }

        private void ConfigureHdrpFrameSettings(Component data)
        {
            Set(data, "customRenderingSettings", true);
            var settingsField = data.GetType().GetField("m_RenderingPathCustomFrameSettings", BindingFlags.Instance | BindingFlags.NonPublic);
            var overrideField = data.GetType().GetField("renderingPathCustomFrameSettingsOverrideMask");
            object settings = settingsField.GetValue(data);
            object overrides = overrideField.GetValue(data);
            var maskField = overrides.GetType().GetField("mask");
            object bits = maskField.GetValue(overrides);
            var bitIndexer = bits.GetType().GetProperty("Item");
            Type optionType = FindType("UnityEngine.Rendering.HighDefinition.FrameSettingsField");
            foreach (string option in new[] { "Postprocess", "ExposureControl" })
            {
                object optionValue = Enum.Parse(optionType, option);
                settings.GetType().GetMethod("SetEnabled").Invoke(settings, new[] { optionValue, (object)false });
                bitIndexer.SetValue(bits, true, new object[] { Convert.ToUInt32(optionValue) });
                report.settings.Add("HDRP FrameSettings override " + option + "=false");
            }
            maskField.SetValue(overrides, bits);
            overrideField.SetValue(data, overrides);
            settingsField.SetValue(data, settings);
        }

        private IEnumerator RunCases()
        {
            while (player.Texture == null || player.Decoder.frame < 3 || cameraFrames < 4) yield return null;
            player.Pause();
            report.renderPipelineInstance = RenderPipelineManager.currentPipeline == null ? "null" : RenderPipelineManager.currentPipeline.GetType().FullName;
            yield return Capture("background", "none", "none", false);
            foreach (RenderMode mode in new[] { RenderMode.WorldSpace, RenderMode.ScreenSpaceCamera })
            {
                foreach (string mask in new[] { "none", "Mask", "RectMask2D" })
                {
                    CreateCanvas(mode, mask);
                    int frame = cameraFrames;
                    while (cameraFrames < frame + 8) yield return null;
                    yield return Capture(mode + "-" + mask, mode.ToString(), mask, true);
                    canvasObject.SetActive(false);
                    Destroy(canvasObject);
                    yield return null;
                }
            }
            Finish("");
        }

        private void CreateCanvas(RenderMode mode, string mask)
        {
            canvasObject = new GameObject("Canvas " + mode + " " + mask, typeof(RectTransform));
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = mode;
            canvas.worldCamera = camera;
            canvas.planeDistance = 1;
            var rect = canvasObject.GetComponent<RectTransform>();
            if (mode == RenderMode.WorldSpace)
            {
                rect.sizeDelta = new Vector2(256, 128);
                rect.localScale = Vector3.one / 128f;
                rect.position = Vector3.zero;
            }
            Transform parent = rect;
            if (mask != "none")
            {
                var maskObject = new GameObject(mask, typeof(RectTransform));
                var maskRect = maskObject.GetComponent<RectTransform>();
                maskRect.SetParent(rect, false);
                maskRect.sizeDelta = new Vector2(128, 128);
                if (mask == "Mask")
                {
                    var graphic = maskObject.AddComponent<Image>();
                    graphic.raycastTarget = false;
                    maskObject.AddComponent<Mask>().showMaskGraphic = false;
                }
                else maskObject.AddComponent<RectMask2D>();
                parent = maskRect;
            }
            var viewObject = new GameObject("Native RawImage View", typeof(RectTransform));
            var viewRect = viewObject.GetComponent<RectTransform>();
            viewRect.SetParent(parent, false);
            viewRect.sizeDelta = new Vector2(256, 128);
            var image = viewObject.AddComponent<RawImage>();
            image.raycastTarget = false;
            var view = viewObject.AddComponent<VideoEffectView>();
            view.player = player;
            view.fit = VideoFit.Stretch;
            view.Refresh();
            Canvas.ForceUpdateCanvases();
        }

        private IEnumerator Capture(string name, string mode, string mask, bool hasVideo)
        {
            int frame = cameraFrames;
            while (cameraFrames < frame + 3) yield return null;
            var texture = new Texture2D(256, 128, TextureFormat.RGBAFloat, false, true);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, 256, 128), 0, 0);
            texture.Apply();
            RenderTexture.active = previous;
            var item = new Case { name = name, renderMode = mode, mask = mask, capture = name + ".png", decoderFrame = player.Decoder.frame, decoderTime = player.CurrentTime, cameraFrames = cameraFrames };
            Color blue = new Color(0, 0, 1, 0);
            SamplePixel(item, texture, "transparent", 32, blue);
            SamplePixel(item, texture, "opaqueBlack", 96, hasVideo ? new Color(0, 0, 0, 1) : blue);
            SamplePixel(item, texture, "halfRed", 160, hasVideo ? new Color(.5f, 0, .5f, .5f) : blue);
            SamplePixel(item, texture, "opaqueGreenOrClipped", 224, hasVideo && mask == "none" ? new Color(0, 1, 0, 1) : blue);
            item.passed = item.samples.TrueForAll(sample => sample.passed);
            report.cases.Add(item);
            // PNG is a viewable linear-value diagnostic; floating point numeric samples are authoritative.
            var png = new Texture2D(256, 128, TextureFormat.RGBA32, false, true);
            png.SetPixels(texture.GetPixels()); png.Apply();
            File.WriteAllBytes(Path.Combine(output, item.capture), png.EncodeToPNG());
            Destroy(png); Destroy(texture);
            File.WriteAllText(Path.Combine(output, "results.json"), JsonUtility.ToJson(report, true));
            Debug.Log("VTU case " + name + " passed=" + item.passed);
        }

        private void SamplePixel(Case item, Texture2D image, string name, int x, Color expected)
        {
            Color actual = image.GetPixel(x, 96);
            float error = Mathf.Max(Mathf.Abs(actual.r - expected.r), Mathf.Abs(actual.g - expected.g), Mathf.Abs(actual.b - expected.b), Mathf.Abs(actual.a - expected.a));
            item.samples.Add(new Sample { name = name, x = x, y = 96, actual = actual, expected = expected, maxError = error, passed = error <= report.tolerance });
        }

        private void Rendered(ScriptableRenderContext context, Camera rendered) { if (rendered == camera) cameraFrames++; }

        private void Finish(string error)
        {
            if (finished) return;
            finished = true;
            RenderPipelineManager.endCameraRendering -= Rendered;
            report.error = error;
            report.passed = error.Length == 0 && report.cases.Count == 7 && report.cases.TrueForAll(item => item.passed);
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "results.json"), JsonUtility.ToJson(report, true));
            if (player != null) player.Stop();
#if UNITY_EDITOR
            EditorApplication.Exit(report.passed ? 0 : 4);
#endif
        }

        private static string Argument(string key)
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, key);
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing " + key);
            return args[index + 1];
        }

        private static Type FindType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            { var type = assembly.GetType(name); if (type != null) return type; }
            return null;
        }

        private void Set(object targetObject, string name, object value)
        {
            Type type = targetObject.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) field.SetValue(targetObject, value);
            else if (property != null && property.CanWrite) property.SetValue(targetObject, value);
            else throw new MissingMemberException(type.FullName, name);
            report.settings.Add(type.Name + "." + name + "=" + value);
        }

        private void SetEnum(object targetObject, string name, string value)
        {
            var field = targetObject.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            var property = targetObject.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Type type = field != null ? field.FieldType : property.PropertyType;
            Set(targetObject, name, Enum.Parse(type, value));
        }
    }
}
