using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace VideoToUnity.Editor
{
    public sealed partial class BackgroundRemovalWindow : EditorWindow
    {
        private const string FfmpegPreference = "VideoToUnity.FFmpeg";
        private const string FfprobePreference = "VideoToUnity.FFprobe";
        private const string UxmlGuid = "e8ce74e6e9474a2a9d2277ebf6b412e9";
        private const string UssGuid = "23fa98092aa844ccaff93024f3938b4c";
        private static readonly List<string> FormatLabels = new List<string>
            { "Packed H.264 (.mp4)", "AlphaWebM (.webm) · Native Alpha" };

        [SerializeField] private string sourcePath = "";
        [SerializeField] private string ffmpeg = "ffmpeg";
        [SerializeField] private string ffprobe = "ffprobe";
        [SerializeField] private bool autoDetect = true;
        [SerializeField] private Color manualColor = Color.black;
        [SerializeField] private float threshold = .06f;
        [SerializeField] private float softness = .08f;
        [SerializeField] private bool borderConnected = true;
        [SerializeField] private float temporalSmoothing = .15f;
        [SerializeField] private List<RegionState> regions = new List<RegionState>();
        [SerializeField] private int selectedRegion = -1;
        [SerializeField] private float previewTime;
        [SerializeField] private int formatIndex;
        [SerializeField] private string videoPath = "Assets/BackgroundRemoved.mp4";
        [SerializeField] private string effectPath = "Assets/BackgroundRemoved.asset";

        private VisualElement sourceSection, analysisSection, regionSection, outputSection, statusSection, resultSection;
        private VisualElement originalStage, resultStage, outlineLayer, originalImage, resultImage, checkerImage;
        private TextField sourceField, ffmpegField, ffprobeField, videoField, effectField;
        private Toggle autoDetectField, borderField;
        private Toggle pickColorField;
        private ColorField manualColorField;
        private Slider thresholdField, softnessField, temporalField, timeField;
        private DropdownField formatField;
        private Button analyzeButton, previewButton, convertButton, cancelButton, openEffectButton;
        private HelpBox statusBox, analysisBox;
        private Label metrics, confidence, effectResultLabel;
        private Label previewNotice;
        private Texture2D originalTexture, resultTexture, checkerTexture;
        private UiRect imageBounds;
        private int seenRevision;
        private double durationSeconds;
        private bool dragging;
        private bool drawingRegion;
        private int dragRegion = -1;
        private bool movingRegion;
        private UiPoint dragStart;
        private NormalizedRegion dragOriginal;
        private BackgroundResult previewResult;
        private BackgroundAnalysis shownAnalysis;
        private string seenOperationMessage;
        private bool previewStale;

        [Serializable]
        private sealed class RegionState
        {
            public float x = .1f, y = .1f, width = .2f, height = .2f;
            public RegionState() { }
            public RegionState(float x, float y, float width, float height)
            { this.x = x; this.y = y; this.width = width; this.height = height; }
        }

        [MenuItem("Tools/Video to Unity/고급/MP4 배경 분리")]
        public static void Open()
        {
            var window = GetWindow<BackgroundRemovalWindow>("MP4 배경 분리");
            window.CancelEasyWork();
            window.easyMode = false;
            window.CreateGUI();
            window.Show();
        }

        private void OnEnable()
        {
            ffmpeg = EditorPrefs.GetString(FfmpegPreference, "ffmpeg");
            ffprobe = EditorPrefs.GetString(FfprobePreference, "ffprobe");
            BackgroundRemovalOperation.Changed += OperationChanged;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            EditorApplication.update += PollOperation;
            EnableEasyWorkflow();
        }

        private void OnDisable()
        {
            DisableEasyWorkflow();
            BackgroundRemovalOperation.Changed -= OperationChanged;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            EditorApplication.update -= PollOperation;
            if (BackgroundRemovalOperation.Busy) BackgroundRemovalOperation.Cancel();
            DestroyPreviewTextures();
        }

        public void CreateGUI()
        {
            if (easyMode) { CreateEasyGUI(); return; }
            minSize = new Vector2(620, 620);
            rootVisualElement.Clear();
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(AssetDatabase.GUIDToAssetPath(UxmlGuid));
            if (visualTree == null)
            {
                var fallback = new Label("배경 분리 UI 리소스를 찾을 수 없습니다. 패키지를 다시 가져오세요.");
                rootVisualElement.Add(fallback);
                return;
            }
            visualTree.CloneTree(rootVisualElement);
            // The window bounds constrain the viewport; only its content may grow vertically.
            var scroll = rootVisualElement.Q<ScrollView>("background-window");
            if (scroll != null)
            {
                scroll.mode = ScrollViewMode.Vertical;
                scroll.style.flexGrow = 1;
                scroll.style.flexShrink = 1;
                scroll.style.minHeight = 0;
                scroll.contentContainer.style.flexGrow = 0;
                scroll.contentContainer.style.flexShrink = 0;
            }
            var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(UssGuid));
            if (style != null) rootVisualElement.styleSheets.Add(style);
            Query();
            BuildSource();
            BuildAnalysis();
            BuildRegions();
            BuildPreview();
            BuildOutput();
            BuildStatus();
            BuildResult();
            OperationChanged();
            RefreshState();
        }

        private void Query()
        {
            sourceSection = rootVisualElement.Q("source-section");
            analysisSection = rootVisualElement.Q("analysis-section");
            regionSection = rootVisualElement.Q("region-section");
            outputSection = rootVisualElement.Q("output-section");
            statusSection = rootVisualElement.Q("status-section");
            resultSection = rootVisualElement.Q("result-section");
            originalStage = rootVisualElement.Q("original-stage");
            resultStage = rootVisualElement.Q("result-stage");
            outlineLayer = rootVisualElement.Q("outline-layer");
            originalImage = rootVisualElement.Q("original-image");
            resultImage = rootVisualElement.Q("result-image");
            checkerImage = rootVisualElement.Q("checker-image");
            if (originalImage != null) originalImage.style.display = DisplayStyle.None;
            if (resultImage != null) resultImage.style.display = DisplayStyle.None;
        }

        private void BuildSource()
        {
            Heading(sourceSection, "1. 원본 MP4와 도구");
            sourceField = TextFieldInput(sourceSection, "원본 MP4", sourcePath, ChangeSource);
            sourceSection.Add(new Button(ChooseSource) { text = "원본 파일 선택…" });
            Description(sourceSection, "원본 파일은 그대로 보존됩니다. 결과는 Assets 아래의 새 파일로만 저장합니다. 오디오는 제거됩니다.");
            var tools = new Foldout { text = "FFmpeg / ffprobe 실행 파일 설정", value = false };
            sourceSection.Add(tools);
            ffmpegField = TextFieldInput(tools, "FFmpeg", ffmpeg, value =>
            { ffmpeg = value; EditorPrefs.SetString(FfmpegPreference, value); });
            tools.Add(new Button(() => ChooseTool(ffmpegField, "FFmpeg")) { text = "FFmpeg 실행 파일 선택…" });
            ffprobeField = TextFieldInput(tools, "ffprobe", ffprobe, value =>
            { ffprobe = value; EditorPrefs.SetString(FfprobePreference, value); });
            tools.Add(new Button(() => ChooseTool(ffprobeField, "ffprobe")) { text = "ffprobe 실행 파일 선택…" });
            Description(tools, "PATH에 있다면 명령 이름만 입력해도 됩니다. 입력값은 이 컴퓨터의 EditorPrefs에 저장됩니다.");
        }

        private void BuildAnalysis()
        {
            Heading(analysisSection, easyMode ? "배경색과 가장자리" : "2. 색상 추출 설정");
            analyzeButton = new Button(() => Run(() =>
            {
                CommitFields();
                BackgroundRemovalOperation.StartAnalyze(sourcePath, ffmpeg, ffprobe);
            })) { text = "원본 배경색 분석" };
            analyzeButton.AddToClassList("background-primary");
            analysisSection.Add(analyzeButton);
            confidence = new Label("아직 분석하지 않았습니다.");
            confidence.AddToClassList("background-confidence");
            analysisSection.Add(confidence);
            analysisBox = new HelpBox("배경색을 먼저 분석하거나 AutoDetect를 끄고 직접 색을 지정하세요.", HelpBoxMessageType.Info);
            analysisSection.Add(analysisBox);
            autoDetectField = ToggleInput(analysisSection, "AutoDetect 사용", autoDetect, value =>
            { autoDetect = value; MarkPreviewStale(); RefreshState(); });
            manualColorField = new ColorField(easyMode ? "제거할 배경색" : "수동 배경색")
            {
                value = manualColor,
                tooltip = easyMode ? "자동으로 찾은 색이 기본값입니다. 다른 배경색을 직접 지정할 수 있습니다." : "AutoDetect 사용 중에는 이 색을 사용하지 않습니다. 자동 분석 색은 위 신뢰도 옆의 제안 RGB 값으로 확인하세요."
            };
            manualColorField.RegisterValueChangedCallback(e => { manualColor = e.newValue; MarkPreviewStale(); });
            analysisSection.Add(manualColorField);
            if (!easyMode) Description(analysisSection, "자동 모드에서는 아래 수동 색을 사용하지 않습니다. 분석 제안 색은 위의 RGB 값으로 확인하세요.");
            var presets = new VisualElement();
            presets.AddToClassList("background-row");
            presets.Add(new Button(() => UseManualColor(Color.black))
            { name = "use-black-background", text = easyMode ? "검정 배경" : "검정 배경 사용 (수동)" });
            presets.Add(new Button(() => UseManualColor(Color.green))
            { name = "use-green-background", text = easyMode ? "초록 배경" : "초록 배경 사용 (수동)" });
            analysisSection.Add(presets);
            analysisSection.Add(new Button(UseSuggestedColor) { text = easyMode ? "추천 색으로 되돌리기" : "제안 색을 수동 색으로 복사" });
            thresholdField = SliderInput(analysisSection, "색상 임계값", threshold, 0, 1, value => { threshold = value; MarkPreviewStale(); });
            softnessField = SliderInput(analysisSection, "경계 부드러움", softness, .001f, 1, value => { softness = value; MarkPreviewStale(); });
            borderField = ToggleInput(analysisSection, "가장자리와 연결된 배경만 제거", borderConnected, value => { borderConnected = value; MarkPreviewStale(); });
            temporalField = SliderInput(analysisSection, "시간 안정화", temporalSmoothing, 0, .9f, value => { temporalSmoothing = value; MarkPreviewStale(); });
            if (!easyMode) Description(analysisSection, "색상 기반 추출입니다. 같은 색의 피사체도 제거될 수 있습니다. 신뢰도는 테두리 색의 일관성이며 피사체 판별 정확도가 아닙니다. 60% 미만이면 자동 처리를 거부합니다. 검정·초록 버튼 또는 원본 색 찍기로 수동 색을 정한 뒤 미리보기를 확인하세요. 보호 영역은 유지됩니다.");
            if (!easyMode) Description(analysisSection, "보호 영역은 분석 결과와 무관하게 클립 전체에 같은 위치로 고정됩니다. 움직이는 피사체를 자동으로 따라가지 않습니다.");
        }

        private void BuildRegions()
        {
            Heading(regionSection, easyMode ? "지우지 않을 영역" : "3. 고정 보호 영역");
            var row = new VisualElement();
            row.AddToClassList("background-row");
            regionSection.Add(row);
            row.Add(new Button(() => AddRegion(.1f, .1f, .2f, .2f)) { text = "영역 추가" });
            row.Add(new Button(RemoveSelectedRegion) { text = "선택 영역 삭제" });
            if (!easyMode) Description(regionSection, "값은 원본 왼쪽 위(0,0)에서 오른쪽 아래(1,1)로 가는 정규화 좌표입니다. 숫자 입력은 필수 동작이며, 미리보기에서 빈 곳을 끌어 추가하거나 사각형을 끌어 이동할 수도 있습니다. 보호 영역은 최대 64개입니다.");
            regionSection.Add(new Label("X · Y · 너비 · 높이"));
            RefreshRegionRows();
        }

        private void BuildPreview()
        {
            var previewSection = rootVisualElement.Q("preview-section");
            timeField = SliderInput(previewSection, "시간 (초)", previewTime, 0, 10, value =>
            { previewTime = value; MarkPreviewStale(); RefreshOutlines(); });
            pickColorField = new Toggle("원본에서 배경색 찍기") { name = "pick-background-color" };
            previewSection.Add(pickColorField);
            if (!easyMode) Description(previewSection, "색 찍기를 켠 뒤 왼쪽 원본을 클릭하면 그 색을 수동 배경색으로 사용합니다. 미리보기 새로고침으로 결과를 확인하세요.");
            previewButton = new Button(() => Run(() =>
            {
                if (easyMode) { QueueEasyPreview(); return; }
                CommitFields();
                BackgroundRemovalOperation.StartPreview(sourcePath, ffmpeg, ffprobe, Options(), previewTime);
            })) { text = "이 시간의 미리보기 새로고침" };
            previewButton.AddToClassList("background-primary");
            previewSection.Add(previewButton);
            metrics = new Label("미리보기를 만들면 결과 크기와 영상 길이가 표시됩니다.");
            metrics.AddToClassList("background-metrics");
            previewSection.Add(metrics);
            previewNotice = new Label();
            previewNotice.name = "preview-freshness";
            previewNotice.AddToClassList("background-description");
            previewSection.Add(previewNotice);
            UpdatePreviewNotice();
            RefreshOutlines();
            RegisterPreviewPointer(resultStage, true);
            RegisterPreviewPointer(originalStage, false);
            originalStage?.RegisterCallback<GeometryChangedEvent>(_ => RefreshOutlines());
            resultStage?.RegisterCallback<GeometryChangedEvent>(_ => RefreshOutlines());
        }

        private void BuildOutput()
        {
            Heading(outputSection, "4. 새 영상과 이펙트 저장");
            formatField = new DropdownField("출력 형식", FormatLabels, Mathf.Clamp(formatIndex, 0, 1));
            formatField.RegisterValueChangedCallback(e =>
            {
                formatIndex = Mathf.Max(0, FormatLabels.IndexOf(e.newValue));
                videoPath = Path.ChangeExtension(videoPath, formatIndex == 0 ? ".mp4" : ".webm");
                videoField?.SetValueWithoutNotify(videoPath);
            });
            outputSection.Add(formatField);
            videoField = TextFieldInput(outputSection, "새 영상 경로", videoPath, value => videoPath = value);
            outputSection.Add(new Button(() => ChooseAssetPath(videoField, "새 배경 제거 영상 저장", formatIndex == 0 ? "mp4" : "webm")) { text = "새 영상 경로 선택…" });
            effectField = TextFieldInput(outputSection, "새 이펙트 에셋", effectPath, value => effectPath = value);
            outputSection.Add(new Button(() => ChooseAssetPath(effectField, "새 VideoEffectAsset 저장", "asset")) { text = "새 이펙트 경로 선택…" });
            convertButton = new Button(() => Run(() =>
            {
                CommitFields();
                BackgroundRemovalOperation.StartConvert(sourcePath, ffmpeg, ffprobe, Options(),
                    formatIndex == 0 ? VideoConversionFormat.PackedH264 : VideoConversionFormat.AlphaWebM,
                    videoPath, effectPath);
            })) { text = "배경 분리 영상과 이펙트 만들기" };
            convertButton.AddToClassList("background-primary");
            outputSection.Add(convertButton);
            Description(outputSection, "Packed H.264는 알파를 오른쪽 절반에 저장하며 Packed Alpha로 임포트합니다. AlphaWebM은 실제 알파를 보존하며 Native Alpha로 임포트합니다. 출력은 Straight Alpha이고, 내용 크기는 원본과 같습니다. 기존 파일과 에셋은 덮어쓰지 않으며 원본을 수정하지 않습니다.");
            Description(outputSection, "오디오는 저장되지 않습니다. CFR, 짝수 가로·높이, SDR Rec.709/sRGB 원본이 필요합니다. 가변 프레임, 회전 메타데이터, 비정방 픽셀, 인터레이스, HDR/와이드 색역은 지원하지 않습니다. 코덱 지원은 대상 기기에서 확인하세요.");
        }

        private void BuildStatus()
        {
            Heading(statusSection, "처리 상태");
            statusBox = new HelpBox(BackgroundRemovalOperation.Message, HelpBoxMessageType.Info);
            statusSection.Add(statusBox);
            cancelButton = new Button(() => BackgroundRemovalOperation.Cancel()) { text = "취소 및 임시 파일 정리" };
            statusSection.Add(cancelButton);
            Description(statusSection, "창을 닫거나 Play Mode로 들어가거나 스크립트가 다시 로드되면 진행 중 작업을 취소합니다. 자식 프로세스와 임시 파일은 작업이 끝난 뒤 정리됩니다.");
        }

        private void BuildResult()
        {
            Heading(resultSection, "완료된 새 에셋");
            effectResultLabel = new Label();
            effectResultLabel.AddToClassList("background-result-path");
            resultSection.Add(effectResultLabel);
            openEffectButton = new Button(() => Run(() =>
            {
                var output = BackgroundRemovalOperation.LastOutput;
                if (output == null || output.Effect == null) throw new InvalidOperationException("완료된 새 이펙트가 없습니다.");
                VideoEffectWindow.OpenWithEffect(output.Effect);
            })) { text = "새 이펙트로 영상 이펙트 창 열기" };
            openEffectButton.AddToClassList("background-primary");
            resultSection.Add(openEffectButton);
            Description(resultSection, "새로 저장한 이펙트를 기존 배치 화면에 연결해 바로 씬 오브젝트를 만들 수 있습니다. 저장된 기존 에셋은 수정하지 않습니다.");
        }

        private void ChooseSource()
        {
            string path = EditorUtility.OpenFilePanel("배경을 분리할 MP4 선택", "", "mp4");
            if (string.IsNullOrEmpty(path)) return;
            ChangeSource(Path.GetFullPath(path));
            sourceField?.SetValueWithoutNotify(sourcePath);
        }

        private void ChangeSource(string path)
        {
            if (sourcePath == path) return;
            sourcePath = path;
            durationSeconds = 0;
            previewTime = 0;
            timeField?.SetValueWithoutNotify(0);
            previewResult = null;
            previewStale = false;
            pickColorField?.SetValueWithoutNotify(false);
            shownAnalysis = null;
            seenRevision = BackgroundRemovalOperation.Revision;
            DestroyTexture(ref originalTexture);
            DestroyTexture(ref resultTexture);
            if (originalImage != null) originalImage.style.display = DisplayStyle.None;
            if (resultImage != null) resultImage.style.display = DisplayStyle.None;
            outlineLayer?.Clear();
            if (confidence != null) confidence.text = "새 원본을 분석하세요.";
            if (analysisBox != null) analysisBox.text = "배경색 분석 결과가 아직 없습니다.";
            if (metrics != null) metrics.text = "";
            UpdatePreviewNotice();
            RefreshState();
        }

        private bool MatchesSource(string analyzedSource)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(analyzedSource)) return false;
            try { return string.Equals(Path.GetFullPath(sourcePath.Trim()), analyzedSource, StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }

        private static void ChooseTool(TextField field, string label)
        {
            string path = EditorUtility.OpenFilePanel(label + " 실행 파일 선택", "",
                Application.platform == RuntimePlatform.WindowsEditor ? "exe" : "");
            if (!string.IsNullOrEmpty(path)) field.value = path;
        }

        private void ChooseAssetPath(TextField field, string title, string extension)
        {
            string chosen = EditorUtility.SaveFilePanelInProject(title,
                Path.GetFileNameWithoutExtension(field.value), extension,
                "Assets 안의 존재하지 않는 새 경로를 선택하세요.", "Assets");
            if (!string.IsNullOrEmpty(chosen)) field.value = chosen;
        }

        private void CommitFields()
        {
            sourcePath = sourceField?.value?.Trim() ?? sourcePath;
            ffmpeg = ffmpegField?.value?.Trim() ?? ffmpeg;
            ffprobe = ffprobeField?.value?.Trim() ?? ffprobe;
            videoPath = videoField?.value?.Trim() ?? videoPath;
            effectPath = effectField?.value?.Trim() ?? effectPath;
            EditorPrefs.SetString(FfmpegPreference, ffmpeg);
            EditorPrefs.SetString(FfprobePreference, ffprobe);
        }

        private BackgroundOptions Options()
        {
            var result = new BackgroundOptions
            {
                AutoDetect = autoDetect,
                Red = ToByte(manualColor.r),
                Green = ToByte(manualColor.g),
                Blue = ToByte(manualColor.b),
                Threshold = threshold,
                Softness = softness,
                BorderConnected = borderConnected,
                TemporalSmoothing = temporalSmoothing,
                ProtectedRegions = new BackgroundKeepRegion[regions.Count]
            };
            for (int i = 0; i < regions.Count; i++)
            {
                var region = regions[i];
                var normalized = BackgroundRemovalUiModel.ClampRegion(region.x, region.y, region.width, region.height);
                result.ProtectedRegions[i] = new BackgroundKeepRegion(normalized.X, normalized.Y, normalized.Width, normalized.Height);
            }
            return result;
        }

        private void UseSuggestedColor()
        {
            if (shownAnalysis == null) return;
            UseManualColor(new Color32(shownAnalysis.Red, shownAnalysis.Green, shownAnalysis.Blue, 255));
        }

        private void UseManualColor(Color color)
        {
            manualColor = color;
            manualColorField?.SetValueWithoutNotify(manualColor);
            autoDetect = false;
            autoDetectField?.SetValueWithoutNotify(false);
            MarkPreviewStale();
            SetStatus($"수동 배경색 RGB {ToByte(color.r)}, {ToByte(color.g)}, {ToByte(color.b)} · 보호 영역을 유지했습니다. 미리보기를 새로고침하세요.", HelpBoxMessageType.Info);
            RefreshState();
        }

        private void OperationChanged()
        {
            if (easyMode) { RefreshEasyState(); return; }
            if (statusBox == null) return;
            seenOperationMessage = BackgroundRemovalOperation.Message;
            statusBox.text = BackgroundRemovalOperation.Message;
            statusBox.messageType = BackgroundRemovalOperation.Failed ? HelpBoxMessageType.Error : HelpBoxMessageType.Info;
            if (seenRevision == BackgroundRemovalOperation.Revision) { RefreshState(); return; }
            seenRevision = BackgroundRemovalOperation.Revision;
            var analysis = BackgroundRemovalOperation.LastAnalysis;
            if (analysis != null && MatchesSource(BackgroundRemovalOperation.LastAnalysisSource))
            {
                shownAnalysis = analysis;
                confidence.text = $"신뢰도 {analysis.Confidence:P0} · 제안 색 RGB {analysis.Red}, {analysis.Green}, {analysis.Blue}" +
                    (string.IsNullOrWhiteSpace(analysis.Message) ? "" : "\n" + analysis.Message);
                analysisBox.text = analysis.Reliable ? "분석 결과를 사용할 수 있습니다. 제안 색을 수동 색으로 복사해 확인할 수 있습니다." :
                    "신뢰도가 낮습니다. 자동 미리보기와 변환은 거부됩니다. 아래 검정·초록 버튼이나 제안 색을 수동 색으로 선택하세요. 만든 미리보기에서는 원본 색을 찍을 수도 있습니다.";
                analysisBox.messageType = analysis.Reliable ? HelpBoxMessageType.Info : HelpBoxMessageType.Warning;
            }
            var result = BackgroundRemovalOperation.LastResult;
            if (!MatchesSource(BackgroundRemovalOperation.LastResultSource)) result = null;
            if (result != null) durationSeconds = result.DurationSeconds;
            if (result != null && result.Preview != null)
            {
                if (!ReferenceEquals(previewResult, result)) previewStale = false;
                previewResult = result;
                durationSeconds = result.DurationSeconds;
                timeField.highValue = Mathf.Max(.1f, (float)Math.Max(result.DurationSeconds, result.Preview.TimeSeconds));
                previewTime = (float)result.Preview.TimeSeconds;
                timeField.SetValueWithoutNotify((float)result.Preview.TimeSeconds);
                ShowPreview(result.Preview);
                UpdatePreviewNotice();
                metrics.text = $"결과 {result.Width} × {result.Height} · 길이 {result.DurationSeconds:F2}초 · {result.FramesPerSecond:F2} fps";
            }
            var output = BackgroundRemovalOperation.LastOutput;
            if (output != null)
            {
                effectResultLabel.text = "영상: " + output.VideoPath + "\n이펙트: " + output.EffectPath +
                    "\n크기: " + output.Size.x + " × " + output.Size.y + " · " + output.AlphaMode;
                Show(resultSection, true);
                EditorGUIUtility.PingObject(output.Effect);
            }
            else
            {
                Show(resultSection, false);
            }
            RefreshState();
        }

        private void ShowPreview(BackgroundPreview preview)
        {
            DestroyTexture(ref originalTexture);
            DestroyTexture(ref resultTexture);
            originalTexture = ToTexture(preview.OriginalRgba, preview.Width, preview.Height);
            resultTexture = ToTexture(preview.ResultRgba, preview.Width, preview.Height);
            SetImageTexture(originalImage, originalTexture);
            SetImageTexture(resultImage, resultTexture);
            originalImage.style.display = DisplayStyle.Flex;
            resultImage.style.display = DisplayStyle.Flex;
            EnsureChecker(preview.Width, preview.Height);
            RefreshOutlines();
        }

        private static Texture2D ToTexture(byte[] rgba, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
            { name = "BackgroundRemoval Preview", hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            var colors = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                int sourceRow = y * width * 4;
                int targetRow = (height - 1 - y) * width;
                for (int x = 0; x < width; x++)
                {
                    int s = sourceRow + x * 4;
                    colors[targetRow + x] = new Color32(rgba[s], rgba[s + 1], rgba[s + 2], rgba[s + 3]);
                }
            }
            texture.SetPixels32(colors);
            texture.Apply(false, false);
            return texture;
        }

        private void EnsureChecker(int width, int height)
        {
            if (checkerTexture != null && checkerTexture.width == width && checkerTexture.height == height) return;
            DestroyTexture(ref checkerTexture);
            checkerTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
            { name = "BackgroundRemoval Checker", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var colors = new Color32[width * height];
            int block = Mathf.Max(4, Mathf.RoundToInt(width / 40f));
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    colors[y * width + x] = ((x / block + y / block) & 1) == 0
                        ? new Color32(92, 92, 92, 255) : new Color32(154, 154, 154, 255);
            checkerTexture.SetPixels32(colors);
            checkerTexture.Apply(false, false);
            SetImageTexture(checkerImage, checkerTexture);
        }

        private void RefreshOutlines()
        {
            if (outlineLayer == null) return;
            outlineLayer.Clear();
            if (resultStage == null) return;
            // Each Image fills its own stage and lets ScaleToFit handle the letterbox.
            // Keep computed bounds only for stage-local pointer and overlay geometry.
            imageBounds = PreviewBounds(resultStage);
            for (int i = 0; i < regions.Count; i++)
            {
                var region = regions[i];
                var rect = BackgroundRemovalUiModel.RegionPixels(
                    new NormalizedRegion(region.x, region.y, region.width, region.height), imageBounds);
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                var outline = new VisualElement();
                outline.AddToClassList("background-region-outline");
                if (i == selectedRegion) outline.AddToClassList("selected");
                outline.style.left = rect.X;
                outline.style.top = rect.Y;
                outline.style.width = rect.Width;
                outline.style.height = rect.Height;
                outlineLayer.Add(outline);
            }
        }

        private void RegisterPreviewPointer(VisualElement stage, bool editable)
        {
            if (stage == null) return;
            stage.RegisterCallback<PointerDownEvent>(e =>
            {
                if (!CanEditPreview() || (easyMode && !CanEditEasyMask) || previewResult == null || e.button != 0) return;
                if (!editable)
                {
                    if (pickColorField != null && pickColorField.value)
                        PickBackgroundColor(e.localPosition.x, e.localPosition.y);
                    return;
                }
                if (imageBounds.Width <= 0) RefreshOutlines();
                if (!imageBounds.Contains(e.localPosition.x, e.localPosition.y)) return;
                int hit = HitRegion(e.localPosition.x, e.localPosition.y);
                selectedRegion = hit;
                if (hit >= 0)
                {
                    movingRegion = true;
                    dragOriginal = new NormalizedRegion(regions[hit].x, regions[hit].y, regions[hit].width, regions[hit].height);
                    dragStart = BackgroundRemovalUiModel.ClampSourcePoint(imageBounds, e.localPosition.x, e.localPosition.y);
                }
                else
                {
                    if (!BackgroundRemovalUiModel.CanAddRegion(regions.Count))
                    {
                        SetStatus("보호 영역은 최대 64개입니다. 기존 영역을 삭제한 뒤 추가하세요.", HelpBoxMessageType.Warning);
                        return;
                    }
                    movingRegion = false;
                    drawingRegion = true;
                    dragStart = BackgroundRemovalUiModel.ClampSourcePoint(imageBounds, e.localPosition.x, e.localPosition.y);
                }
                dragging = true;
                stage.CapturePointer(e.pointerId);
                e.StopPropagation();
                RefreshRegionRows();
            });
            stage.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!editable || !dragging || !CanEditPreview() || (easyMode && !CanEditEasyMask)) return;
                var point = BackgroundRemovalUiModel.ClampSourcePoint(imageBounds, e.localPosition.x, e.localPosition.y);
                if (movingRegion && selectedRegion >= 0)
                {
                    var moved = BackgroundRemovalUiModel.MoveRegion(dragOriginal, point.X - dragStart.X, point.Y - dragStart.Y);
                    SetRegion(selectedRegion, moved);
                }
                else if (drawingRegion)
                {
                    if (dragRegion < 0)
                    {
                        var initial = BackgroundRemovalUiModel.ClampRegion(dragStart.X, dragStart.Y, .01f, .01f);
                        if (!AddRegion(initial.X, initial.Y, initial.Width, initial.Height)) return;
                        dragRegion = regions.Count - 1;
                    }
                    var drawn = BackgroundRemovalUiModel.DragRegion(dragStart, point);
                    SetRegion(dragRegion, drawn);
                }
                RefreshOutlines();
            });
            stage.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!dragging) return;
                dragging = false;
                if (dragRegion >= 0 && dragRegion < regions.Count && BackgroundRemovalUiModel.IsSmallDrag(
                    new NormalizedRegion(regions[dragRegion].x, regions[dragRegion].y, regions[dragRegion].width, regions[dragRegion].height)))
                    regions.RemoveAt(dragRegion);
                if (dragRegion >= 0) selectedRegion = Math.Min(dragRegion, regions.Count - 1);
                dragRegion = -1;
                drawingRegion = false;
                movingRegion = false;
                stage.ReleasePointer(e.pointerId);
                RefreshRegionRows();
                RefreshOutlines();
            });
            stage.RegisterCallback<PointerCaptureOutEvent>(_ =>
            {
                dragging = drawingRegion = movingRegion = false;
                dragRegion = -1;
                RefreshRegionRows();
                RefreshOutlines();
            });
        }

        private static bool CanEditPreview() => !BackgroundRemovalOperation.Busy &&
            !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling;

        private void PickBackgroundColor(float localX, float localY)
        {
            var preview = previewResult?.Preview;
            if (preview == null || !CanEditPreview()) return;
            var bounds = PreviewBounds(originalStage);
            if (!BackgroundRemovalUiModel.TrySampleOriginal(preview.OriginalRgba, preview.Width, preview.Height,
                bounds, localX, localY, out byte red, out byte green, out byte blue)) return;
            UseManualColor(new Color32(red, green, blue, 255));
            pickColorField?.SetValueWithoutNotify(false);
        }

        private void MarkPreviewStale()
        {
            if (easyMode) { QueueEasyPreview(); return; }
            previewStale = previewResult != null;
            UpdatePreviewNotice();
        }

        private void UpdatePreviewNotice()
        {
            if (previewNotice == null) return;
            previewNotice.text = previewResult == null ? "먼저 미리보기를 만드세요." : previewStale
                ? "설정 또는 시간이 바뀌었습니다. 표시된 결과는 이전 미리보기입니다. 새로고침하세요."
                : "표시된 시간의 결과입니다. 시간 안정화 효과는 전체 변환에서 확인하세요.";
        }

        private int HitRegion(float x, float y)
        {
            for (int i = regions.Count - 1; i >= 0; i--)
            {
                var region = regions[i];
                var rect = BackgroundRemovalUiModel.RegionPixels(
                    new NormalizedRegion(region.x, region.y, region.width, region.height), imageBounds);
                if (rect.Contains(x, y)) return i;
            }
            return -1;
        }

        private void SetRegion(int index, NormalizedRegion region)
        {
            if (index < 0 || index >= regions.Count) return;
            regions[index] = new RegionState(region.X, region.Y, region.Width, region.Height);
            MarkPreviewStale();
        }

        private bool AddRegion(float x, float y, float width, float height)
        {
            if (!BackgroundRemovalUiModel.CanAddRegion(regions.Count))
            { SetStatus("보호 영역은 최대 64개입니다.", HelpBoxMessageType.Warning); return false; }
            var region = BackgroundRemovalUiModel.ClampRegion(x, y, width, height);
            regions.Add(new RegionState(region.X, region.Y, region.Width, region.Height));
            selectedRegion = regions.Count - 1;
            MarkPreviewStale();
            RefreshRegionRows();
            RefreshOutlines();
            return true;
        }

        private void RemoveSelectedRegion()
        {
            if (selectedRegion < 0 || selectedRegion >= regions.Count) return;
            regions.RemoveAt(selectedRegion);
            selectedRegion = Math.Min(selectedRegion, regions.Count - 1);
            MarkPreviewStale();
            RefreshRegionRows();
            RefreshOutlines();
        }

        private void RefreshRegionRows()
        {
            if (regionSection == null) return;
            var old = regionSection.Q("region-rows");
            if (old != null) old.RemoveFromHierarchy();
            var list = new VisualElement();
            list.name = "region-rows";
            list.AddToClassList("background-region-list");
            regionSection.Add(list);
            for (int i = 0; i < regions.Count; i++)
            {
                int index = i;
                var region = regions[i];
                var row = new VisualElement();
                row.AddToClassList("background-region-row");
                row.Add(new Button(() => { selectedRegion = index; RefreshRegionRows(); RefreshOutlines(); })
                { text = "영역 " + (i + 1) });
                row.Add(FloatInput("X", region.x, value => regions[index].x = BackgroundRemovalUiModel.Clamp(value, 0, 1 - regions[index].width)));
                row.Add(FloatInput("Y", region.y, value => regions[index].y = BackgroundRemovalUiModel.Clamp(value, 0, 1 - regions[index].height)));
                row.Add(FloatInput("W", region.width, value => regions[index].width = BackgroundRemovalUiModel.Clamp(value, .001f, 1 - regions[index].x)));
                row.Add(FloatInput("H", region.height, value => regions[index].height = BackgroundRemovalUiModel.Clamp(value, .001f, 1 - regions[index].y)));
                row.Add(new Button(() => { regions.RemoveAt(index); selectedRegion = Math.Min(index, regions.Count - 1); MarkPreviewStale(); RefreshRegionRows(); RefreshOutlines(); })
                { text = "삭제" });
                list.Add(row);
            }
        }

        private void PlayModeChanged(PlayModeStateChange state)
        {
            if (easyMode && state == PlayModeStateChange.ExitingEditMode) CancelEasyWork();
            if (state == PlayModeStateChange.ExitingEditMode) BackgroundRemovalOperation.Cancel();
            RefreshState();
        }

        private void PollOperation()
        {
            if (easyMode) { PollEasyWorkflow(); return; }
            // A repeated preview can produce the same status text, so revision changes also
            // drive the result update instead of relying on the message alone.
            if (statusBox != null &&
                (seenOperationMessage != BackgroundRemovalOperation.Message || seenRevision != BackgroundRemovalOperation.Revision))
                OperationChanged();
            if (!BackgroundRemovalOperation.Busy && cancelButton != null && cancelButton.enabledSelf)
                RefreshState();
        }

        private void RefreshState()
        {
            if (easyMode) { RefreshEasyState(); return; }
            if (sourceSection == null) return;
            bool busy = BackgroundRemovalOperation.Busy;
            bool editing = !busy && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling;
            sourceSection.SetEnabled(editing);
            analysisSection.SetEnabled(editing);
            regionSection.SetEnabled(editing);
            outputSection.SetEnabled(editing);
            cancelButton?.SetEnabled(busy);
            pickColorField?.SetEnabled(editing && previewResult?.Preview != null);
            timeField?.SetEnabled(editing);
            manualColorField?.SetEnabled(!autoDetect);
            timeField.highValue = Mathf.Max(.1f, (float)Math.Max(durationSeconds, previewTime));
            analyzeButton?.SetEnabled(editing && BackgroundRemovalUiModel.SourceReady(sourcePath));
            previewButton?.SetEnabled(editing && BackgroundRemovalUiModel.SourceReady(sourcePath));
            convertButton?.SetEnabled(editing && BackgroundRemovalUiModel.SourceReady(sourcePath));
            openEffectButton?.SetEnabled(!busy && BackgroundRemovalOperation.LastOutput != null);
            if (previewResult == null)
            {
                originalImage?.AddToClassList("background-hidden");
                resultImage?.AddToClassList("background-hidden");
            }
        }

        private void Run(Action action)
        {
            try { action(); }
            catch (Exception ex) { if (easyMode && !quickBackgroundJob && !quickAlphaJob && quickCopyTask == null) quickAdding = false; SetStatus(ex.Message, HelpBoxMessageType.Error); }
            RefreshState();
        }

        private void SetStatus(string message, HelpBoxMessageType type)
        {
            if (easyMode) { quickMessage = message; quickError = type == HelpBoxMessageType.Error; }
            if (statusBox == null) return;
            statusBox.text = message;
            statusBox.messageType = type;
        }

        private void DestroyPreviewTextures()
        {
            DestroyTexture(ref originalTexture);
            DestroyTexture(ref resultTexture);
            DestroyTexture(ref checkerTexture);
        }

        private static void DestroyTexture(ref Texture2D texture)
        {
            if (texture != null) DestroyImmediate(texture);
            texture = null;
        }

        private static void SetImageTexture(VisualElement element, Texture2D texture)
        {
            var image = element as Image;
            if (image == null) throw new InvalidOperationException("미리보기 이미지 요소를 찾을 수 없습니다.");
            image.scaleMode = ScaleMode.ScaleToFit;
            image.image = texture;
        }

        private UiRect PreviewBounds(VisualElement stage)
        {
            if (stage == null) return new UiRect(0, 0, 0, 0);
            Rect content = stage.contentRect;
            return BackgroundRemovalUiModel.FitInContent(
                previewResult?.Preview?.Width ?? 16, previewResult?.Preview?.Height ?? 9,
                new UiRect(content.x, content.y, content.width, content.height));
        }

        private static byte ToByte(float value) => (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
        private static void Heading(VisualElement parent, string text) { var label = new Label(text); label.AddToClassList("background-heading"); parent.Add(label); }
        private static void Description(VisualElement parent, string text) { var label = new Label(text); label.AddToClassList("background-description"); parent.Add(label); }
        private static void Show(VisualElement element, bool visible) => element?.EnableInClassList("background-hidden", !visible);
        private static TextField TextFieldInput(VisualElement parent, string label, string value, Action<string> changed)
        { var field = new TextField(label) { value = value }; field.RegisterValueChangedCallback(e => changed(e.newValue)); parent.Add(field); return field; }
        private static Toggle ToggleInput(VisualElement parent, string label, bool value, Action<bool> changed)
        { var field = new Toggle(label) { value = value }; field.RegisterValueChangedCallback(e => changed(e.newValue)); parent.Add(field); return field; }
        private static Slider SliderInput(VisualElement parent, string label, float value, float low, float high, Action<float> changed)
        { var field = new Slider(label, low, high) { value = value, showInputField = true }; field.RegisterValueChangedCallback(e => changed(e.newValue)); parent.Add(field); return field; }
        private FloatField FloatInput(string label, float value, Func<float, float> changed)
        {
            var field = new FloatField(label) { value = value, isDelayed = true };
            field.RegisterValueChangedCallback(e =>
            {
                field.SetValueWithoutNotify(changed(e.newValue));
                MarkPreviewStale();
                RefreshOutlines();
            });
            return field;
        }
    }
}
