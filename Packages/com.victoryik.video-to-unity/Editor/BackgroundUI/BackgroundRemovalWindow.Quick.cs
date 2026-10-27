using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UnityEngine.Video;

namespace VideoToUnity.Editor
{
    public sealed partial class BackgroundRemovalWindow
    {
        [SerializeField] private bool easyMode;
        [SerializeField] private QuickEffectMode quickMode;
        [SerializeField] private VideoEffectPlacement quickPlacement;
        [SerializeField] private Canvas quickCanvas;
        [SerializeField] private bool quickLoop;
        [SerializeField] private float quickWorldWidth = 2f;
        private QuickVideoInfo quickInfo;
        private Task<QuickVideoInfo> inspectionTask;
        private Task<BackgroundPreview> quickPreviewTask;
        private Task<string> quickCopyTask;
        private CancellationTokenSource quickCancellation;
        private bool inspectQueued, previewQueued, quickAdding, quickBackgroundJob, quickAlphaJob;
        private bool quickStreaming;
        private double previewDue;
        private int quickGeneration, taskGeneration, quickTargetScene;
        private string quickVideoPath, quickEffectPath, quickMessage = "영상 하나를 끌어 넣으면 사용 방식을 추천합니다.";
        private VideoEffectAsset quickSavedEffect;
        private HelpBox quickRecommendation, quickTools;
        private Label quickSourceName, quickOutputLabel;
        private VisualElement quickDrop, quickControls, quickPlacementPanel;
        private Foldout quickFineTune;
        private DropdownField quickModeField, quickPlacementField;
        private ObjectField quickCanvasField;
        private FloatField quickWidthField;
        private Button quickAdd, quickInstall, quickDiscover, quickCancel;
        private bool quickError;
        private static readonly List<string> QuickModes = new List<string>
        { "단색 배경 제거", "원본 투명도 유지", "빛·글로우 밝게 합성", "원본 그대로", "좌우 RGB + 알파 영상" };
        private static readonly List<string> QuickPlacements = new List<string>
        { "화면 전체", "선택한 UI 안에", "게임 공간의 평면" };

        [MenuItem("Tools/Video to Unity/영상으로 이펙트 만들기", false, 0)]
        public static void OpenEasy()
        {
            var window = GetWindow<BackgroundRemovalWindow>();
            window.easyMode = true;
            window.titleContent = new GUIContent("Video to Unity");
            window.CreateGUI();
            window.Show();
            VideoToolchainSetup.Discover();
        }

        private void EnableEasyWorkflow()
        {
            VideoToolchainSetup.Changed += EasyToolsChanged;
            if (easyMode) EditorApplication.delayCall += RestoreEasyTools;
        }

        private void DisableEasyWorkflow()
        {
            VideoToolchainSetup.Changed -= EasyToolsChanged;
            EditorApplication.delayCall -= RestoreEasyTools;
            CancelEasyWork();
        }

        private void RestoreEasyTools()
        { if (this != null && easyMode && !VideoToolchainSetup.Ready && !VideoToolchainSetup.Busy) VideoToolchainSetup.Discover(); }

        private void CreateEasyGUI()
        {
            minSize = new Vector2(640, 650);
            rootVisualElement.Clear();
            var oldStyle = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(UssGuid));
            if (oldStyle != null) rootVisualElement.styleSheets.Add(oldStyle);
            var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath("638e03740167479c99a6ffb62cb39a42"));
            if (style != null) rootVisualElement.styleSheets.Add(style);
            var scroll = new ScrollView { name = "quick-window" };
            scroll.AddToClassList("quick-window");
            rootVisualElement.Add(scroll);
            var title = new Label("영상을 게임 이펙트로");
            title.AddToClassList("background-title"); scroll.Add(title);
            Description(scroll, "영상 넣기 → 추천 확인 → 미리보기 → 씬에 추가");

            quickDrop = new VisualElement { name = "video-drop-zone" };
            quickDrop.AddToClassList("quick-drop"); scroll.Add(quickDrop);
            quickSourceName = new Label(string.IsNullOrEmpty(sourcePath) ? "여기에 영상 파일을 끌어 놓으세요" : Path.GetFileName(sourcePath));
            quickSourceName.AddToClassList("quick-source-name"); quickDrop.Add(quickSourceName);
            Description(quickDrop, "탐색기 또는 Unity Project 창에서 · MP4 / MOV / WebM 등");
            quickDrop.Add(new Button(() =>
            {
                string path = EditorUtility.OpenFilePanelWithFilters("영상 선택", "", new[] { "영상", "mp4,mov,webm,m4v,avi,wmv,mkv" });
                if (!string.IsNullOrEmpty(path)) Run(() => AcceptEasySource(path));
            }) { name = "choose-video", text = "파일 선택" });
            quickDrop.AddManipulator(new QuickVideoDropManipulator(path => Run(() => AcceptEasySource(path))));

            var toolRow = new VisualElement(); scroll.Add(toolRow);
            quickTools = new HelpBox("영상 도구를 찾는 중…", HelpBoxMessageType.Info); toolRow.Add(quickTools);
            var setupButtons = new VisualElement(); setupButtons.AddToClassList("background-row"); toolRow.Add(setupButtons);
            quickDiscover = new Button(VideoToolchainSetup.Discover) { text = "다시 찾기" }; setupButtons.Add(quickDiscover);
            quickInstall = new Button(VideoToolchainSetup.Install) { name = "prepare-tools", text = "영상 도구 자동 준비" }; setupButtons.Add(quickInstall);
            var toolDetails = new Foldout { text = "도구 경로 직접 지정", value = false }; toolRow.Add(toolDetails);
            ffmpegField = TextFieldInput(toolDetails, "FFmpeg", ffmpeg, value => ffmpeg = value);
            toolDetails.Add(new Button(() => ChooseTool(ffmpegField, "FFmpeg")) { text = "FFmpeg 선택…" });
            ffprobeField = TextFieldInput(toolDetails, "ffprobe", ffprobe, value => ffprobe = value);
            toolDetails.Add(new Button(() => ChooseTool(ffprobeField, "ffprobe")) { text = "ffprobe 선택…" });
            toolDetails.Add(new Button(() => Run(() => VideoToolchainSetup.UsePaths(ffmpegField.value, ffprobeField.value))) { text = "이 경로 확인하고 사용" });

            quickControls = new VisualElement(); scroll.Add(quickControls);
            quickRecommendation = new HelpBox("영상을 넣으면 배경색과 투명도를 확인합니다.", HelpBoxMessageType.Info);
            quickControls.Add(quickRecommendation);
            quickModeField = new DropdownField("사용 방식", QuickModes, (int)quickMode);
            quickModeField.RegisterValueChangedCallback(e =>
            {
                quickMode = (QuickEffectMode)QuickModes.IndexOf(e.newValue);
                QueueEasyPreview(); RefreshEasyState();
            });
            quickControls.Add(quickModeField);

            var previewSection = new VisualElement { name = "preview-section" };
            previewSection.AddToClassList("background-section"); quickControls.Add(previewSection);
            Heading(previewSection, "미리보기");
            var previews = new VisualElement(); previews.AddToClassList("background-preview-row"); previewSection.Add(previews);
            originalStage = QuickPreviewColumn(previews, "원본", "original-stage", out originalImage);
            resultStage = QuickPreviewColumn(previews, "적용 결과", "result-stage", out resultImage);
            checkerImage = new Image { name = "checker-image", scaleMode = ScaleMode.ScaleToFit };
            checkerImage.AddToClassList("background-checker"); resultStage.Insert(0, checkerImage);
            outlineLayer = new VisualElement { name = "outline-layer", pickingMode = PickingMode.Ignore };
            outlineLayer.AddToClassList("background-outline-layer"); resultStage.Add(outlineLayer);
            BuildPreview();
            previewButton.text = "미리보기 다시 확인";
            timeField.label = "다른 시간 보기";
            Description(previewSection, "시간을 바꾸면 해당 장면을 자동으로 갱신합니다. 배경 제거 결과에서 지워지면 안 되는 그림도 확인하세요.");

            quickFineTune = new Foldout { text = "배경이 잘못 지워졌나요? · 색과 보호 영역 조절", value = false };
            quickControls.Add(quickFineTune);
            analysisSection = new VisualElement(); quickFineTune.Add(analysisSection);
            regionSection = new VisualElement(); quickFineTune.Add(regionSection);
            BuildAnalysis(); BuildRegions();
            Show(analyzeButton, false); Show(confidence, false); Show(analysisBox, false); Show(autoDetectField, false);
            Show(analysisSection.Q<Button>("use-black-background"), true);
            Description(quickFineTune, "오른쪽 미리보기에서 드래그하면 남길 영역을 지정합니다. 영역은 영상 전체에서 같은 위치에 고정됩니다.");

            quickPlacementPanel = new VisualElement(); quickPlacementPanel.AddToClassList("quick-footer"); rootVisualElement.Add(quickPlacementPanel);
            Heading(quickPlacementPanel, "어디에 사용할까요?");
            quickPlacementField = new DropdownField("추가할 위치", QuickPlacements, (int)quickPlacement);
            quickPlacementField.RegisterValueChangedCallback(e => { quickPlacement = (VideoEffectPlacement)QuickPlacements.IndexOf(e.newValue); RefreshEasyState(); });
            quickPlacementPanel.Add(quickPlacementField);
            quickCanvasField = new ObjectField("UI Canvas") { objectType = typeof(Canvas), allowSceneObjects = true, value = quickCanvas };
            quickCanvasField.RegisterValueChangedCallback(e => { quickCanvas = e.newValue as Canvas; RefreshEasyState(); });
            quickPlacementPanel.Add(quickCanvasField);
            quickWidthField = new FloatField("월드 너비") { value = quickWorldWidth, isDelayed = true };
            quickWidthField.RegisterValueChangedCallback(e => { quickWorldWidth = e.newValue; RefreshEasyState(); });
            quickPlacementPanel.Add(quickWidthField);
            ToggleInput(quickPlacementPanel, "반복 재생", quickLoop, value => { quickLoop = value; quickSavedEffect = null; });
            quickAdd = new Button(() => Run(AddEasyToScene)) { name = "add-to-scene", text = "씬에 추가" };
            quickAdd.AddToClassList("quick-primary"); quickPlacementPanel.Add(quickAdd);
            quickOutputLabel = new Label("영상과 설정은 프로젝트에 자동으로 저장됩니다. 원본은 그대로 유지됩니다.");
            quickOutputLabel.AddToClassList("background-description"); quickPlacementPanel.Add(quickOutputLabel);
            statusBox = new HelpBox(quickMessage, HelpBoxMessageType.Info); rootVisualElement.Add(statusBox);
            quickCancel = new Button(CancelEasyWork) { text = "진행 중 작업 취소" }; rootVisualElement.Add(quickCancel);
            EasyToolsChanged();
            if (quickInfo == null && !string.IsNullOrEmpty(sourcePath)) inspectQueued = true;
            RefreshEasyState();
        }

        private static VisualElement QuickPreviewColumn(VisualElement row, string caption, string name, out VisualElement image)
        {
            var column = new VisualElement(); column.AddToClassList("background-preview-column"); row.Add(column);
            column.Add(new Label(caption));
            var stage = new VisualElement { name = name }; stage.AddToClassList("background-preview-stage"); column.Add(stage);
            image = new Image { scaleMode = ScaleMode.ScaleToFit }; image.AddToClassList("background-preview-image"); stage.Add(image);
            return stage;
        }

        internal void AcceptEasySource(string path)
        {
            if (quickAdding) throw new InvalidOperationException("현재 추가 작업을 마치거나 취소한 뒤 다른 영상을 넣으세요.");
            path = ResolveEasyPath(path);
            if (!QuickWorkflowPolicy.IsVideo(path) || !File.Exists(path)) throw new ArgumentException("실제로 존재하는 영상 파일 하나를 선택하세요.");
            CancelEasyWorkCore(false);
            sourcePath = path; quickInfo = null; quickSavedEffect = null;
            regions.Clear(); selectedRegion = -1; RefreshRegionRows();
            previewResult = null; previewTime = 0; durationSeconds = 0; autoDetect = false;
            manualColor = Color.black; threshold = .06f; softness = .08f;
            manualColorField?.SetValueWithoutNotify(manualColor);
            thresholdField?.SetValueWithoutNotify(threshold); softnessField?.SetValueWithoutNotify(softness);
            timeField?.SetValueWithoutNotify(0); pickColorField?.SetValueWithoutNotify(false);
            DestroyPreviewTextures(); outlineLayer?.Clear();
            quickSourceName.text = Path.GetFileName(path);
            quickRecommendation.text = "배경색과 투명도를 확인하는 중…";
            inspectQueued = true; quickError = false;
            quickMessage = VideoToolchainSetup.Ready ? "영상 분석을 시작합니다." : "영상 도구가 준비되면 자동으로 분석합니다.";
            if (!VideoToolchainSetup.Ready && !VideoToolchainSetup.Busy) VideoToolchainSetup.Discover();
            RefreshEasyState();
        }

        internal static string ResolveEasyPath(string path)
        {
            if (path.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
                if (package != null) path = Path.Combine(package.resolvedPath, path.Substring(package.assetPath.Length).TrimStart('/'));
            }
            return Path.GetFullPath(path);
        }

        private void EasyToolsChanged()
        {
            if (!easyMode) return;
            if (VideoToolchainSetup.Ready)
            {
                ffmpeg = VideoToolchainSetup.Ffmpeg; ffprobe = VideoToolchainSetup.Ffprobe;
                ffmpegField?.SetValueWithoutNotify(ffmpeg); ffprobeField?.SetValueWithoutNotify(ffprobe);
            }
            RefreshEasyState();
        }

        private bool QuickWorkerBusy => inspectionTask != null || quickPreviewTask != null || quickCopyTask != null;
        private bool CanEditEasyMask => quickMode == QuickEffectMode.RemoveBackground && !QuickWorkerBusy && !quickAdding;

        private void QueueEasyPreview()
        {
            quickSavedEffect = null;
            if (quickInfo == null || quickAdding) return;
            quickGeneration++;
            quickCancellation?.Cancel();
            previewQueued = true; previewDue = EditorApplication.timeSinceStartup + .3;
            previewStale = true;
            RefreshEasyState();
        }

        private void StartEasyTask()
        {
            quickCancellation?.Dispose(); quickCancellation = new CancellationTokenSource();
            quickCancellation.CancelAfter(TimeSpan.FromMinutes(2));
            taskGeneration = quickGeneration;
        }

        private void PollEasyWorkflow()
        {
            if (!easyMode || statusBox == null) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            try
            {
                if (inspectionTask != null && inspectionTask.IsCompleted)
                {
                    var task = inspectionTask; inspectionTask = null;
                    if (taskGeneration == quickGeneration) ApplyEasyInspection(task.GetAwaiter().GetResult());
                    else { var observed = task.Exception; }
                }
                if (quickPreviewTask != null && quickPreviewTask.IsCompleted)
                {
                    var task = quickPreviewTask; quickPreviewTask = null;
                    if (taskGeneration == quickGeneration) ApplyEasyPreview(task.GetAwaiter().GetResult());
                    else { var observed = task.Exception; }
                }
                if (quickCopyTask != null && quickCopyTask.IsCompleted)
                {
                    var task = quickCopyTask; quickCopyTask = null;
                    task.GetAwaiter().GetResult();
                    quickSavedEffect = SaveEasyEffect(quickVideoPath, DirectMode(), new Vector2Int(quickInfo.Width / (quickMode == QuickEffectMode.PackedAlpha ? 2 : 1), quickInfo.Height));
                    FinishEasyAdd();
                }
                if (quickBackgroundJob && !BackgroundRemovalOperation.Busy)
                {
                    quickBackgroundJob = false;
                    var output = BackgroundRemovalOperation.LastOutput;
                    if (BackgroundRemovalOperation.Failed || output == null || output.EffectPath != quickEffectPath)
                        throw new InvalidOperationException(BackgroundRemovalOperation.Message);
                    quickSavedEffect = output.Effect;
                    FinishEasyAdd();
                }
                if (quickAlphaJob && !VideoEffectConversionOperation.Busy)
                {
                    quickAlphaJob = false;
                    var output = VideoEffectConversionOperation.LastOutput;
                    if (VideoEffectConversionOperation.Failed || output == null || output.AssetPath != quickVideoPath)
                        throw new InvalidOperationException(VideoEffectConversionOperation.Message);
                    quickSavedEffect = SaveEasyEffect(output.AssetPath, output.AlphaMode, output.Size);
                    FinishEasyAdd();
                }
                if (QuickWorkerBusy || quickAdding || BackgroundRemovalOperation.Busy || VideoEffectConversionOperation.Busy || !VideoToolchainSetup.Ready) { RefreshEasyState(); return; }
                if (inspectQueued)
                {
                    inspectQueued = false; StartEasyTask();
                    string source = sourcePath, encoder = ffmpeg, probe = ffprobe;
                    var token = quickCancellation.Token;
                    inspectionTask = Task.Run(() => QuickVideoProbe.Inspect(source, encoder, probe, token), token);
                    quickMessage = "배경색과 투명도를 분석하는 중…";
                }
                else if (previewQueued && EditorApplication.timeSinceStartup >= previewDue && quickInfo != null)
                {
                    previewQueued = false; StartEasyTask();
                    var info = quickInfo; var mode = quickMode; var options = Options(); options.AutoDetect = false;
                    double seconds = previewTime; string encoder = ffmpeg;
                    bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear; var token = quickCancellation.Token;
                    quickPreviewTask = Task.Run(() => QuickWorkflowPolicy.Compose(QuickVideoProbe.Preview(info, encoder, seconds, token), mode, options, linear, token), token);
                    quickMessage = "선택한 장면의 적용 결과를 확인하는 중…";
                }
            }
            catch (OperationCanceledException) { quickAdding = false; quickMessage = "작업을 취소했습니다. 다시 선택하거나 미리보기를 갱신할 수 있습니다."; }
            catch (Exception ex) { quickAdding = false; quickError = true; quickMessage = ex.Message; }
            RefreshEasyState();
        }

        private void ApplyEasyInspection(QuickVideoInfo info)
        {
            quickInfo = info; quickMode = QuickWorkflowPolicy.Recommend(info);
            quickModeField.SetValueWithoutNotify(QuickModes[(int)quickMode]);
            shownAnalysis = info.Analysis; autoDetect = false;
            if (info.Analysis != null) manualColor = new Color32(info.Analysis.Red, info.Analysis.Green, info.Analysis.Blue, 255);
            manualColorField.SetValueWithoutNotify(manualColor);
            durationSeconds = info.DurationSeconds;
            previewTime = (float)info.Preview.TimeSeconds;
            timeField.highValue = (float)Math.Max(.01, info.DurationSeconds - 1 / Math.Max(1, info.FramesPerSecond));
            timeField.SetValueWithoutNotify(previewTime);
            quickRecommendation.text = quickMode == QuickEffectMode.PreserveAlpha
                ? "추천 · 원본 투명도 유지. 알파를 담을 수 있는 영상입니다. 전체 변환에서도 투명도를 확인합니다."
                : quickMode == QuickEffectMode.RemoveBackground
                ? "추천 · 단색 배경 제거. 화면 테두리의 색이 일정합니다. 남길 그림까지 지워지지 않았는지 결과를 확인하세요."
                : "추천 · 원본 그대로. 일정한 배경색을 확신할 수 없습니다. 배경을 지우려면 사용 방식과 색을 직접 선택하세요.";
            var options = Options(); options.AutoDetect = false;
            // Composition runs off the UI thread even when the initial decoded frame is cached.
            StartEasyTask(); var token = quickCancellation.Token;
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear; var mode = quickMode;
            quickPreviewTask = Task.Run(() => QuickWorkflowPolicy.Compose(info.Preview, mode, options, linear, token), token);
            previewStale = true;
        }

        private void ApplyEasyPreview(BackgroundPreview preview)
        {
            previewResult = new BackgroundResult { Preview = preview, Width = quickInfo.Width, Height = quickInfo.Height,
                DurationSeconds = quickInfo.DurationSeconds, FramesPerSecond = quickInfo.FramesPerSecond };
            ShowPreview(preview);
            previewStale = false; quickError = false;
            metrics.text = $"{quickInfo.Width} × {quickInfo.Height} · {quickInfo.DurationSeconds:F1}초 · {quickInfo.FramesPerSecond:F1} fps";
            quickMessage = "결과가 맞으면 아래에서 위치를 고르고 ‘씬에 추가’를 누르세요.";
        }

        private void AddEasyToScene()
        {
            if (quickAdding || QuickWorkerBusy || previewQueued || inspectQueued || previewStale || quickInfo == null || previewResult == null)
                throw new InvalidOperationException("현재 설정의 미리보기가 준비된 뒤 추가할 수 있습니다.");
            ValidateEasyPlacement();
            QuickVideoProbe.ValidateSource(quickInfo);
            quickTargetScene = SceneManager.GetActiveScene().handle;
            if (quickSavedEffect != null) { FinishEasyAdd(); return; }
            quickError = false; quickAdding = true;
            PrepareEasyPaths();
            var outputFormat = Application.platform == RuntimePlatform.LinuxEditor && !quickStreaming ? VideoConversionFormat.AlphaWebM : VideoConversionFormat.PackedH264;
            if (quickMode == QuickEffectMode.RemoveBackground)
            {
                var options = Options(); options.AutoDetect = false;
                BackgroundRemovalOperation.StartConvertStreaming(sourcePath, ffmpeg, ffprobe, options, outputFormat, quickVideoPath, quickEffectPath, quickStreaming);
                quickBackgroundJob = true;
            }
            else if (quickMode == QuickEffectMode.PreserveAlpha)
            {
                VideoEffectConversionOperation.Start(sourcePath, quickVideoPath, ffmpeg, ffprobe, outputFormat, quickStreaming);
                quickAlphaJob = true;
            }
            else
            {
                StartEasyTask(); quickCancellation.CancelAfter(TimeSpan.FromMinutes(20));
                var token = quickCancellation.Token;
                string source = sourcePath, destination = VideoEffectEditorPaths.NewAssetPath(quickVideoPath, Path.GetExtension(sourcePath), quickStreaming);
                quickCopyTask = VideoEffectFileTransfer.CopyNewFile(source, destination, token);
                quickMessage = "원본을 프로젝트에 복사하고 이펙트를 준비하는 중…";
            }
        }

        private void PrepareEasyPaths()
        {
            quickStreaming = EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL;
            string stem = QuickWorkflowPolicy.FileStem(sourcePath);
            string folder = EnsureEasyFolder("Assets/VideoToUnity/Generated");
            folder = UnusedEasyPath(folder + "/" + stem);
            EnsureEasyFolder(folder);
            quickEffectPath = UnusedEasyPath(folder + "/effect.asset");
            string videoFolder = quickStreaming ? EnsureEasyFolder("Assets/StreamingAssets/VideoToUnity") : folder;
            bool converted = quickMode == QuickEffectMode.RemoveBackground || quickMode == QuickEffectMode.PreserveAlpha;
            string extension = converted ? (Application.platform == RuntimePlatform.LinuxEditor && !quickStreaming ? ".webm" : ".mp4") : Path.GetExtension(sourcePath);
            quickVideoPath = UnusedEasyPath(videoFolder + "/" + (quickStreaming ? stem : "video") + extension);
        }

        private static string UnusedEasyPath(string preferred)
        {
            string extension = Path.GetExtension(preferred);
            string stem = preferred.Substring(0, preferred.Length - extension.Length);
            for (int i = 0; i < 10000; i++)
            {
                string candidate = i == 0 ? preferred : stem + " " + i + extension;
                // Unity can retain a recently deleted GUID even when GenerateUniqueAssetPath reuses its path.
                if (!File.Exists(candidate) && !Directory.Exists(candidate) && !File.Exists(candidate + ".meta") &&
                    string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(candidate))) return candidate;
            }
            throw new IOException("사용하지 않은 저장 이름을 찾지 못했습니다.");
        }

        internal static string EnsureEasyFolder(string path)
        {
            VideoEffectEditorPaths.AssetPath(path + "/check.asset", VideoEffectEditorPaths.IsStreamingAssets(path + "/check.asset"));
            var parts = path.Split('/'); string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    if (Directory.Exists(Path.GetFullPath(next))) AssetDatabase.Refresh();
                    else if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, parts[i]))) throw new IOException("저장 폴더를 만들 수 없습니다: " + next);
                }
                current = next;
            }
            return current;
        }

        private VideoAlphaMode DirectMode() => quickMode == QuickEffectMode.Additive ? VideoAlphaMode.Additive :
            quickMode == QuickEffectMode.PackedAlpha ? VideoAlphaMode.PackedAlpha : VideoAlphaMode.Opaque;

        private VideoEffectAsset SaveEasyEffect(string video, VideoAlphaMode mode, Vector2Int size)
        {
            VideoClip clip = null;
            if (quickStreaming) AssetDatabase.Refresh();
            else
            {
                AssetDatabase.ImportAsset(video, ImportAssetOptions.ForceSynchronousImport);
                if (mode == VideoAlphaMode.PackedAlpha || mode == VideoAlphaMode.NativeAlpha) VideoEffectImportSettings.Apply(video, mode);
                clip = AssetDatabase.LoadAssetAtPath<VideoClip>(video);
                if (clip == null) throw new InvalidOperationException("Unity가 영상을 읽지 못했습니다. 지원되는 MP4로 내보낸 뒤 다시 넣어 주세요.");
            }
            VideoEffectEditorPaths.NewAssetPath(quickEffectPath, ".asset", false);
            var effect = CreateInstance<VideoEffectAsset>();
            try
            {
                effect.name = Path.GetFileNameWithoutExtension(quickEffectPath);
                effect.source = quickStreaming ? VideoSourceKind.StreamingAssets : VideoSourceKind.VideoClip;
                effect.clip = clip; effect.location = quickStreaming ? video.Substring("Assets/StreamingAssets/".Length) : "";
                effect.alphaMode = mode; effect.contentSize = size; effect.loop = quickLoop; effect.audioEnabled = false;
                if (!effect.Validate(out string reason)) throw new InvalidOperationException(reason);
                AssetDatabase.CreateAsset(effect, quickEffectPath); AssetDatabase.SaveAssetIfDirty(effect);
                return effect;
            }
            finally { if (!AssetDatabase.Contains(effect)) DestroyImmediate(effect); }
        }

        private void FinishEasyAdd()
        {
            quickAdding = false;
            if (quickSavedEffect == null) throw new InvalidOperationException("저장된 이펙트가 없습니다.");
            quickSavedEffect.name = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(quickSavedEffect));
            quickSavedEffect.loop = quickLoop; EditorUtility.SetDirty(quickSavedEffect); AssetDatabase.SaveAssetIfDirty(quickSavedEffect);
            if (SceneManager.GetActiveScene().handle != quickTargetScene)
                throw new InvalidOperationException("씬이 바뀌어 자동 추가를 멈췄습니다. 에셋은 저장되었습니다. 원하는 씬에서 다시 ‘씬에 추가’를 누르세요.");
            ValidateEasyPlacement();
            var position = quickPlacement == VideoEffectPlacement.WorldSpaceCanvas && SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : Vector3.zero;
            var player = VideoEffectObjectFactory.Create(quickSavedEffect, quickPlacement, quickCanvas, VideoFit.Contain, true, quickWorldWidth, position);
            player.gameObject.name = Path.GetFileNameWithoutExtension(sourcePath) + " · Video Effect";
            Selection.activeGameObject = player.gameObject;
            quickMessage = "씬에 추가했습니다. Play를 누르면 재생됩니다. 오브젝트 추가는 Ctrl+Z로 되돌릴 수 있습니다.";
            quickOutputLabel.text = "저장 위치: " + AssetDatabase.GetAssetPath(quickSavedEffect) + "\n이펙트는 소리 없이 재생합니다. 씬을 저장하면 배치도 보존됩니다.";
        }

        private void ValidateEasyPlacement()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || PrefabStageUtility.GetCurrentPrefabStage() != null)
                throw new InvalidOperationException("Play Mode와 프리팹 모드를 닫고 편집 씬에서 추가하세요.");
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene)) throw new InvalidOperationException("일반 편집 씬을 먼저 여세요.");
            if (quickPlacement == VideoEffectPlacement.ExistingCanvas && (quickCanvas == null || EditorUtility.IsPersistent(quickCanvas) || !quickCanvas.gameObject.scene.isLoaded))
                throw new InvalidOperationException("현재 씬의 UI Canvas를 선택하세요.");
            if (float.IsNaN(quickWorldWidth) || float.IsInfinity(quickWorldWidth) || quickWorldWidth <= 0) throw new InvalidOperationException("월드 너비는 0보다 커야 합니다.");
        }

        private void CancelEasyWork() => CancelEasyWorkCore(true);

        private void CancelEasyWorkCore(bool cancelTools)
        {
            quickGeneration++; inspectQueued = false; previewQueued = false;
            if (cancelTools && easyMode && VideoToolchainSetup.Busy) VideoToolchainSetup.Cancel();
            quickCancellation?.Cancel();
            if (inspectionTask != null) inspectionTask.ContinueWith(t => { var observed = t.Exception; }, TaskScheduler.Default);
            if (quickPreviewTask != null) quickPreviewTask.ContinueWith(t => { var observed = t.Exception; }, TaskScheduler.Default);
            if (quickCopyTask != null) quickCopyTask.ContinueWith(t =>
            {
                var observed = t.Exception;
                if (t.Status == TaskStatus.RanToCompletion) { try { VideoEffectFileTransfer.DeleteCopiedFile(t.Result); } catch (IOException) { } }
            }, TaskScheduler.Default);
            inspectionTask = null; quickPreviewTask = null; quickCopyTask = null;
            if (quickBackgroundJob) BackgroundRemovalOperation.Cancel();
            if (quickAlphaJob) VideoEffectConversionOperation.Cancel();
            quickBackgroundJob = false; quickAlphaJob = false; quickAdding = false;
            quickMessage = "작업을 취소했습니다. 원본은 유지됩니다.";
            if (easyMode) RefreshEasyState();
        }

        private void RefreshEasyState()
        {
            if (!easyMode || quickAdd == null) return;
            bool editing = !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling;
            bool converting = quickAdding || BackgroundRemovalOperation.Busy || VideoEffectConversionOperation.Busy;
            quickDrop?.SetEnabled(editing && !converting);
            quickControls?.SetEnabled(editing && !converting && quickInfo != null);
            quickPlacementPanel?.SetEnabled(editing && !converting);
            quickAdd.SetEnabled(editing && !converting && !QuickWorkerBusy && !inspectQueued && !previewQueued && !previewStale && previewResult != null && VideoToolchainSetup.Ready);
            quickAdd.text = quickAdding ? "준비 중…" : "씬에 추가";
            Show(quickCanvasField, quickPlacement == VideoEffectPlacement.ExistingCanvas);
            Show(quickWidthField, quickPlacement == VideoEffectPlacement.WorldSpaceCanvas);
            Show(quickFineTune, quickMode == QuickEffectMode.RemoveBackground);
            Show(outlineLayer, quickMode == QuickEffectMode.RemoveBackground);
            Show(pickColorField, quickMode == QuickEffectMode.RemoveBackground);
            pickColorField?.SetEnabled(CanEditEasyMask && previewResult != null);
            if (previewNotice != null) previewNotice.text = previewStale || previewQueued || QuickWorkerBusy ? "설정이 바뀌었습니다. 새 결과를 준비하는 중…" : "시간별 정지 화면 미리보기 · 배경 분리 결과는 소리 없이 저장됩니다.";
            if (quickTools != null) quickTools.text = VideoToolchainSetup.Ready ? "영상 도구 준비 완료 · 자동으로 연결했습니다." : VideoToolchainSetup.Message;
            Show(quickInstall, !VideoToolchainSetup.Ready && VideoToolchainSetup.CanInstall);
            Show(quickDiscover, !VideoToolchainSetup.Ready);
            quickInstall?.SetEnabled(editing && !VideoToolchainSetup.Busy && !converting);
            quickDiscover?.SetEnabled(editing && !VideoToolchainSetup.Busy && !converting);
            quickCancel?.SetEnabled(QuickWorkerBusy || converting || VideoToolchainSetup.Busy);
            Show(quickCancel, QuickWorkerBusy || converting || VideoToolchainSetup.Busy);
            if (statusBox != null)
            {
                statusBox.text = quickBackgroundJob ? BackgroundRemovalOperation.Message : quickAlphaJob ? VideoEffectConversionOperation.Message : quickMessage;
                statusBox.messageType = quickError ? HelpBoxMessageType.Error : HelpBoxMessageType.Info;
            }
        }
    }

    internal sealed class QuickVideoDropManipulator : Manipulator
    {
        private readonly Action<string> accepted;
        internal QuickVideoDropManipulator(Action<string> accepted) { this.accepted = accepted; }
        protected override void RegisterCallbacksOnTarget()
        { target.RegisterCallback<DragUpdatedEvent>(Update); target.RegisterCallback<DragPerformEvent>(Perform); target.RegisterCallback<DragLeaveEvent>(Leave); }
        protected override void UnregisterCallbacksFromTarget()
        { target.UnregisterCallback<DragUpdatedEvent>(Update); target.UnregisterCallback<DragPerformEvent>(Perform); target.UnregisterCallback<DragLeaveEvent>(Leave); }
        private static string PathFromDrag()
        {
            if (DragAndDrop.paths.Length == 1 && QuickWorkflowPolicy.IsVideo(DragAndDrop.paths[0])) return DragAndDrop.paths[0];
            if (DragAndDrop.paths.Length == 0 && DragAndDrop.objectReferences.Length == 1 && DragAndDrop.objectReferences[0] is VideoClip)
                return AssetDatabase.GetAssetPath(DragAndDrop.objectReferences[0]);
            return null;
        }
        private void Update(DragUpdatedEvent e)
        { bool valid = target.enabledInHierarchy && PathFromDrag() != null; DragAndDrop.visualMode = valid ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected; target.EnableInClassList("quick-drop-active", valid); e.StopPropagation(); }
        private void Perform(DragPerformEvent e)
        { string path = PathFromDrag(); target.RemoveFromClassList("quick-drop-active"); if (target.enabledInHierarchy && path != null) { DragAndDrop.AcceptDrag(); accepted(path); } e.StopPropagation(); }
        private void Leave(DragLeaveEvent e) { target.RemoveFromClassList("quick-drop-active"); }
    }
}
