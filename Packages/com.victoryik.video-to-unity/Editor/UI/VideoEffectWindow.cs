using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.Video;

namespace VideoToUnity.Editor
{
    public sealed class VideoEffectWindow : EditorWindow
    {
        private const string FfmpegPreference = "VideoToUnity.FFmpeg";
        private const string FfprobePreference = "VideoToUnity.FFprobe";
        private static readonly List<string> SourceLabels = new List<string>
            { "기존 VideoClip 사용", "StreamingAssets 파일 사용 (Web 포함)", "알파 원본 변환하기" };
        private static readonly List<string> AlphaLabels = new List<string>
            { "Native Alpha · 영상의 알파", "Additive · 빛 더하기", "Black Key · 검정 제거", "Chroma Key · 지정색 제거", "Packed Alpha · 좌 RGB / 우 알파", "Opaque · 불투명" };
        private static readonly List<string> FormatLabels = new List<string>
            { "Packed H.264 (.mp4) · 기본", "Native Alpha VP8 (.webm) · Linux용" };
        private static readonly List<string> PlacementLabels = new List<string>
            { "화면 전체 · 새 Overlay Canvas", "선택한 Canvas 안에 UI 추가", "월드 공간 · 새 World Space Canvas" };
        private static readonly List<string> FitLabels = new List<string>
            { "Contain · 비율 유지, 전체 표시", "Cover · 비율 유지, 가장자리 자름", "Stretch · 영역에 맞춰 늘림" };

        [SerializeField] private int sourceInput;
        [SerializeField] private VideoClip clip;
        [SerializeField] private string location = "";
        [SerializeField] private string alphaSource = "";
        [SerializeField] private VideoConversionFormat format;
        [SerializeField] private bool conversionStreaming;
        [SerializeField] private string conversionPath = "Assets/VideoEffect.mp4";
        [SerializeField] private VideoAlphaMode alphaMode = VideoAlphaMode.Opaque;
        [SerializeField] private bool premultiplied;
        [SerializeField] private Color tint = Color.white;
        [SerializeField] private float opacity = 1f;
        [SerializeField] private Color keyColor = Color.green;
        [SerializeField] private float keyThreshold = .05f;
        [SerializeField] private float keySoftness = .08f;
        [SerializeField] private bool loop;
        [SerializeField] private bool flipY;
        [SerializeField] private float speed = 1f;
        [SerializeField] private bool audioEnabled;
        [SerializeField] private Vector2Int contentSize;
        [SerializeField] private string effectPath = "Assets/VideoEffect.asset";
        [SerializeField] private VideoEffectAsset savedAsset;
        [SerializeField] private VideoEffectPlacement placement;
        [SerializeField] private Canvas existingCanvas;
        [SerializeField] private VideoFit fit;
        [SerializeField] private bool playOnEnable = true;
        [SerializeField] private float worldWidth = 2f;
        [SerializeField] private Vector3 worldPosition;
        [SerializeField] private VideoEffectPlayer previewPlayer;
        private int seenRevision;

        private VisualElement editControls;
        private VisualElement clipPanel;
        private VisualElement streamingPanel;
        private VisualElement conversionPanel;
        private VisualElement keyPanel;
        private VisualElement canvasPanel;
        private VisualElement worldPanel;
        private VisualElement previewButtons;
        private DropdownField sourceField;
        private DropdownField alphaField;
        private ObjectField clipField;
        private TextField locationField;
        private Vector2IntField sizeField;
        private ObjectField savedField;
        private ObjectField canvasField;
        private ObjectField playerField;
        private HelpBox alphaHelp;
        private HelpBox conversionStatus;
        private HelpBox status;
        private Label playerStatus;
        private Button cancelButton;
        private Button nativeImportButton;
        private Button packedImportButton;
        private Button createAssetButton;
        private Button createObjectButton;
        private Button convertButton;
        private double nextStatusTime;

        [MenuItem("Tools/Video to Unity/고급/영상 이펙트 세부 설정")]
        public static void Open()
        {
            GetWindow<VideoEffectWindow>("영상 이펙트 만들기").Show();
        }

        // Hands a newly generated effect to the existing draft/placement workflow.
        public static void OpenWithEffect(VideoEffectAsset asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            string path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path) || !AssetDatabase.Contains(asset))
                throw new ArgumentException("저장된 프로젝트 에셋만 영상 이펙트 창에서 열 수 있습니다.", nameof(asset));
            var window = GetWindow<VideoEffectWindow>("영상 이펙트 만들기");
            window.LoadEffect(asset, path);
            window.Show();
            window.Repaint();
        }

        private void LoadEffect(VideoEffectAsset asset, string path)
        {
            savedAsset = asset;
            sourceInput = asset.source == VideoSourceKind.StreamingAssets ? 1 : 0;
            clip = asset.clip;
            location = asset.location ?? "";
            alphaSource = "";
            alphaMode = asset.alphaMode;
            premultiplied = asset.premultiplied;
            tint = asset.tint;
            opacity = asset.opacity;
            keyColor = asset.keyColor;
            keyThreshold = asset.keyThreshold;
            keySoftness = asset.keySoftness;
            loop = asset.loop;
            flipY = asset.flipY;
            speed = asset.speed;
            audioEnabled = asset.audioEnabled;
            contentSize = asset.contentSize;
            effectPath = path;
            CreateGUI();
            EditorGUIUtility.PingObject(asset);
            ShowStatus("저장된 이펙트를 불러왔습니다. 아래에서 배치 방식을 고르고 새 씬 오브젝트를 만드세요.");
        }

        private void OnEnable()
        {
            VideoEffectConversionOperation.Changed += ConversionChanged;
            EditorApplication.update += UpdateStatus;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            Undo.undoRedoPerformed += RefreshState;
        }

        private void OnDisable()
        {
            VideoEffectConversionOperation.Changed -= ConversionChanged;
            EditorApplication.update -= UpdateStatus;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            Undo.undoRedoPerformed -= RefreshState;
            VideoEffectConversionOperation.Cancel();
        }

        public void CreateGUI()
        {
            minSize = new Vector2(500, 560);
            rootVisualElement.Clear();
            VideoEffectEditorPaths.AddStyle(rootVisualElement);
            var scroll = new ScrollView();
            scroll.AddToClassList("video-effects-window");
            rootVisualElement.Add(scroll);
            var title = new Label("영상 이펙트 만들기");
            title.AddToClassList("video-effects-title");
            scroll.Add(title);
            Description(scroll, "영상을 준비하고 합성 방식을 정한 뒤, 재사용할 이펙트 에셋과 씬 오브젝트를 만듭니다.");
            scroll.Add(new Button(BackgroundRemovalWindow.Open) { text = "MP4 배경 분리…" });
            editControls = new VisualElement();
            scroll.Add(editControls);
            BuildSource(editControls);
            BuildAppearance(editControls);
            BuildAsset(editControls);
            BuildPlacement(editControls);

            var progress = Section(scroll, "변환 상태");
            conversionStatus = new HelpBox("", HelpBoxMessageType.Info);
            progress.Add(conversionStatus);
            cancelButton = new Button(VideoEffectConversionOperation.Cancel) { text = "변환 취소 및 임시 파일 정리" };
            progress.Add(cancelButton);
            Description(progress, "변환 중에도 창은 응답합니다. 창 닫기·Play Mode 진입·에디터 종료 시 변환을 취소합니다. 마지막 Unity 재임포트는 Unity가 처리하며 잠시 응답이 없을 수 있습니다.");
            BuildPreview(scroll);
            status = new HelpBox("준비되면 새 이펙트 에셋을 저장하세요.", HelpBoxMessageType.Info);
            scroll.Add(status);
            ConversionChanged();
            RefreshState();
        }

        private void BuildSource(VisualElement parent)
        {
            var section = Section(parent, "1. 영상 준비");
            sourceField = Dropdown(section, "영상 가져오기", SourceLabels, sourceInput, index =>
            {
                sourceInput = index;
                RefreshState();
            });
            clipPanel = new VisualElement();
            section.Add(clipPanel);
            clipField = ObjectInput<VideoClip>(clipPanel, "VideoClip", clip, false, value =>
            {
                clip = value;
                UseClipSize();
                RefreshState();
            });
            Description(clipPanel, "프로젝트의 영상을 끌어 넣으세요. 알파 없는 MP4도 Additive / Black Key / Chroma Key / Opaque로 사용할 수 있습니다. 임포트 설정은 아래 버튼을 누를 때만 변경됩니다.");
            Description(clipPanel, "WebGL 빌드는 VideoClip을 지원하지 않습니다. Web용 이펙트는 StreamingAssets 파일 경로로 저장하세요.");
            streamingPanel = new VisualElement();
            section.Add(streamingPanel);
            locationField = TextInput(streamingPanel, "상대 파일 경로", location, value => location = value);
            streamingPanel.Add(new Button(() => Run(() =>
            {
                string path = EditorUtility.OpenFilePanel("StreamingAssets 영상 선택", Application.streamingAssetsPath, "");
                if (string.IsNullOrEmpty(path)) return;
                string relative = FileUtil.GetProjectRelativePath(path).Replace('\\', '/');
                if (!VideoEffectEditorPaths.IsStreamingAssets(relative))
                    throw new ArgumentException("Assets/StreamingAssets 안의 파일을 선택하세요.");
                location = VideoEffectEditorPaths.StreamingLocation(relative.Substring("Assets/StreamingAssets/".Length));
                locationField.SetValueWithoutNotify(location);
            })) { text = "StreamingAssets 파일 선택…" });
            Description(streamingPanel, "예: Effects/Smoke.mp4 (Assets/StreamingAssets/ 제외). WebGL에서는 VideoClip 대신 이 경로를 사용합니다. Packed 영상은 표시 너비를 파일 너비의 절반으로 입력하세요. 브라우저 자동재생과 코덱 지원은 실제 빌드에서 확인하세요.");

            conversionPanel = new VisualElement();
            section.Add(conversionPanel);
            var source = TextInput(conversionPanel, "알파 원본 파일", alphaSource, value => alphaSource = value);
            conversionPanel.Add(new Button(() =>
            {
                string path = EditorUtility.OpenFilePanel("알파 원본 선택 (ProRes 4444 / Alpha WebM 등)", "", "");
                if (!string.IsNullOrEmpty(path)) source.value = path;
            }) { text = "원본 파일 선택…" });
            Description(conversionPanel, "실제 알파 채널이 있는 원본만 변환합니다. 검정 배경이 합쳐진 MP4에서 원래 알파를 복원하지 않습니다. 변환 결과의 오디오는 제거됩니다.");
            TextField output = null;
            Dropdown(conversionPanel, "변환 형식", FormatLabels, (int)format, index =>
            {
                format = (VideoConversionFormat)index;
                conversionPath = Path.ChangeExtension(conversionPath, format == VideoConversionFormat.PackedH264 ? ".mp4" : ".webm");
                output?.SetValueWithoutNotify(conversionPath);
            });
            ToggleInput(conversionPanel, "StreamingAssets에 저장", conversionStreaming, value =>
            {
                conversionStreaming = value;
                string filename = Path.GetFileName(conversionPath);
                conversionPath = (value ? "Assets/StreamingAssets/" : "Assets/") + filename;
                output?.SetValueWithoutNotify(conversionPath);
            });
            output = TextInput(conversionPanel, "새 영상 저장 경로", conversionPath, value => conversionPath = value);
            conversionPanel.Add(new Button(() => Run(() =>
            {
                if (!AssetDatabase.IsValidFolder("Assets/StreamingAssets"))
                {
                    if (Directory.Exists(Application.streamingAssetsPath)) AssetDatabase.Refresh();
                    else if (string.IsNullOrEmpty(AssetDatabase.CreateFolder("Assets", "StreamingAssets")))
                        throw new IOException("StreamingAssets 폴더를 만들지 못했습니다.");
                }
                ShowStatus("Assets/StreamingAssets 폴더가 준비되었습니다. 저장 경로를 선택하세요.");
            })) { text = "StreamingAssets 폴더 준비" });
            conversionPanel.Add(new Button(() =>
            {
                string extension = format == VideoConversionFormat.PackedH264 ? "mp4" : "webm";
                string path = EditorUtility.SaveFilePanelInProject("새 변환 영상 저장", "VideoEffect", extension,
                    "기존 파일을 덮어쓰지 않습니다. StreamingAssets 저장 시 해당 폴더를 선택하세요.",
                    conversionStreaming && AssetDatabase.IsValidFolder("Assets/StreamingAssets") ? "Assets/StreamingAssets" : "Assets");
                if (!string.IsNullOrEmpty(path)) output.value = path;
            }) { text = "새 영상 경로 선택…" });
            Description(conversionPanel, "Packed H.264: RGB와 알파를 좌우에 저장하며 Windows·모바일·Web 경로에 적합합니다. 파일 가로 크기는 표시 너비의 2배입니다. Unity Linux 플레이어는 VP8 WebM을 사용하세요. StreamingAssets 변환은 Packed H.264만 지원합니다.");

            var preferences = new Foldout { text = "FFmpeg / ffprobe 실행 파일 설정", value = false };
            conversionPanel.Add(preferences);
            AddExecutablePreference(preferences, "FFmpeg", FfmpegPreference, "ffmpeg");
            AddExecutablePreference(preferences, "ffprobe", FfprobePreference, "ffprobe");
            Description(preferences, "PATH에 등록했다면 명령 이름만 입력해도 됩니다. 선택한 경로는 이 컴퓨터의 EditorPrefs에 저장되어 다른 프로젝트에서도 유지됩니다. 실행 파일은 직접 준비하세요.");
            convertButton = new Button(() => Run(() => VideoEffectConversionOperation.Start(alphaSource, conversionPath,
                EditorPrefs.GetString(FfmpegPreference, "ffmpeg"), EditorPrefs.GetString(FfprobePreference, "ffprobe"), format, conversionStreaming)))
            { text = "알파 검사 후 새 영상으로 변환" };
            convertButton.AddToClassList("video-effects-primary");
            conversionPanel.Add(convertButton);
        }

        private void BuildAppearance(VisualElement parent)
        {
            var section = Section(parent, "2. 합성과 재생 설정");
            alphaField = Dropdown(section, "합성 방식", AlphaLabels, (int)alphaMode, index =>
            {
                alphaMode = (VideoAlphaMode)index;
                if (sourceInput == 0) UseClipSize();
                RefreshState();
            });
            alphaHelp = new HelpBox("", HelpBoxMessageType.Info);
            section.Add(alphaHelp);
            ToggleInput(section, "Premultiplied 원본", premultiplied, value => premultiplied = value,
                "RGB에 알파가 이미 곱해진 원본에만 켜세요. 일반 Straight Alpha는 끕니다.");
            var color = new ColorField("색상 곱하기") { value = tint };
            color.RegisterValueChangedCallback(e => tint = e.newValue);
            section.Add(color);
            SliderInput(section, "불투명도", opacity, 0, 1, value => opacity = value);
            keyPanel = new VisualElement();
            section.Add(keyPanel);
            var key = new ColorField("제거할 색 (Chroma Key)") { value = keyColor };
            key.RegisterValueChangedCallback(e => keyColor = e.newValue);
            keyPanel.Add(key);
            SliderInput(keyPanel, "제거 임계값", keyThreshold, 0, 1, value => keyThreshold = value);
            SliderInput(keyPanel, "경계 부드러움", keySoftness, 0, 1, value => keySoftness = value);
            ToggleInput(section, "반복 재생", loop, value => loop = value);
            ToggleInput(section, "상하 반전", flipY, value => flipY = value);
            FloatInput(section, "재생 속도", speed, value => speed = value);
            ToggleInput(section, "원본 오디오 재생", audioEnabled, value => audioEnabled = value,
                "기존 영상에 오디오가 있을 때만 사용합니다. 알파 변환 결과에는 오디오가 없습니다.");
            sizeField = new Vector2IntField("표시 크기 (픽셀)") { value = contentSize };
            sizeField.RegisterValueChangedCallback(e => contentSize = e.newValue);
            section.Add(sizeField);
            Description(section, "표시 비율 계산에 사용합니다. 일반 VideoClip은 자동으로 읽습니다. Packed Alpha는 RGB 한쪽의 크기이며 가로가 두 배인 파일 크기와 다릅니다.");
            var imports = Row(section);
            nativeImportButton = new Button(() => ApplyImport(VideoAlphaMode.NativeAlpha)) { text = "선택 Clip: Keep Alpha + Transcode 켜기" };
            packedImportButton = new Button(() => ApplyImport(VideoAlphaMode.PackedAlpha)) { text = "선택 Clip: Packed용 Transcode 끄기" };
            imports.Add(nativeImportButton);
            imports.Add(packedImportButton);
            Description(section, "위 버튼은 지정한 VideoClip 하나의 기본값과 기존 플랫폼별 설정만 재임포트합니다. 같은 영상 에셋을 사용하는 다른 오브젝트에도 영향을 줍니다. Native 설정은 원본 알파가 필요하고 Packed 설정은 이미 좌우로 포장된 영상에만 사용하세요.");
            Description(section, "Packed 설정은 임포터의 상하·좌우 반전과 디인터레이스를 끄고 오디오 가져오기를 켜 원본을 유지합니다. 알파 변환 결과에는 원래 오디오가 없습니다. 반전은 위 재생 설정에서 적용하세요.");
        }

        private void BuildAsset(VisualElement parent)
        {
            var section = Section(parent, "3. 재사용할 에셋 저장");
            var path = TextInput(section, "새 에셋 저장 경로", effectPath, value => effectPath = value);
            section.Add(new Button(() =>
            {
                string chosen = EditorUtility.SaveFilePanelInProject("새 VideoEffectAsset 저장", "VideoEffect", "asset", "Assets 안의 새 이름을 선택하세요.");
                if (!string.IsNullOrEmpty(chosen)) path.value = chosen;
            }) { text = "새 에셋 경로 선택…" });
            createAssetButton = new Button(() => Run(SaveEffect)) { text = "현재 설정으로 새 VideoEffectAsset 저장" };
            createAssetButton.AddToClassList("video-effects-primary");
            section.Add(createAssetButton);
            savedField = ObjectInput<VideoEffectAsset>(section, "씬에 사용할 에셋", savedAsset, false, value =>
            {
                savedAsset = value;
                RefreshState();
            });
            Description(section, "저장한 에셋은 여러 플레이어에서 재사용할 수 있습니다. 아래 씬 배치는 이 에셋을 사용합니다. 위 입력값을 바꿨다면 새 에셋으로 저장하거나 기존 에셋을 Inspector에서 편집하세요.");
        }

        private void BuildPlacement(VisualElement parent)
        {
            var section = Section(parent, "4. 씬에 배치");
            Dropdown(section, "배치 방식", PlacementLabels, (int)placement, index =>
            {
                placement = (VideoEffectPlacement)index;
                RefreshState();
            });
            canvasPanel = new VisualElement();
            section.Add(canvasPanel);
            canvasField = ObjectInput<Canvas>(canvasPanel, "기존 Canvas", existingCanvas, true, value => existingCanvas = value);
            canvasPanel.Add(new Button(() => Run(() =>
            {
                var selected = Selection.activeGameObject;
                var canvas = selected != null ? selected.GetComponentInParent<Canvas>(true) : null;
                if (canvas == null) throw new ArgumentException("Hierarchy에서 Canvas 또는 그 자식을 선택하세요.");
                existingCanvas = canvas;
                canvasField.SetValueWithoutNotify(canvas);
            })) { text = "현재 선택에서 Canvas 가져오기" });
            worldPanel = new VisualElement();
            section.Add(worldPanel);
            FloatInput(worldPanel, "월드 너비 (Unity 단위)", worldWidth, value => worldWidth = value);
            var position = new Vector3Field("월드 위치") { value = worldPosition };
            position.RegisterValueChangedCallback(e => worldPosition = e.newValue);
            worldPanel.Add(position);
            Description(worldPanel, "기본 너비는 2 Unity 단위입니다. 생성 후 이동·회전하여 카메라가 보도록 배치하세요. 기존 카메라를 수정하거나 자동 추적하지 않습니다.");
            Dropdown(section, "화면 맞춤", FitLabels, (int)fit, index => fit = (VideoFit)index);
            ToggleInput(section, "활성화되면 자동 재생", playOnEnable, value => playOnEnable = value);
            createObjectButton = new Button(() => Run(() =>
            {
                previewPlayer = VideoEffectObjectFactory.Create(savedAsset, placement, existingCanvas, fit, playOnEnable, worldWidth, worldPosition);
                playerField.SetValueWithoutNotify(previewPlayer);
                ShowStatus("새 영상 이펙트를 만들었습니다. 씬을 저장하고 Play Mode에서 확인하세요. 생성은 Undo로 되돌릴 수 있습니다.");
            })) { text = "새 이펙트 오브젝트 만들기 (Undo 가능)" };
            createObjectButton.AddToClassList("video-effects-primary");
            section.Add(createObjectButton);
            Description(section, "루트 VideoEffectPlayer와 자식 RawImage / VideoEffectView를 생성합니다. RawImage Raycast와 CanvasGroup 입력 차단은 꺼집니다. 기존 Canvas·카메라·오브젝트 설정은 유지됩니다.");
        }

        private void BuildPreview(VisualElement parent)
        {
            var section = Section(parent, "Play Mode에서 확인");
            Description(section, "이 창에는 편집 중 실시간 영상 미리보기가 없습니다. 씬에 배치한 뒤 Unity의 Play 버튼을 누르고 Game 뷰에서 확인하세요. 아래 버튼은 Play Mode의 지정 플레이어만 제어합니다.");
            playerField = ObjectInput<VideoEffectPlayer>(section, "확인할 플레이어", previewPlayer, true, value =>
            {
                previewPlayer = value;
                RefreshState();
            });
            section.Add(new Button(() => Run(() =>
            {
                var selected = Selection.activeGameObject;
                var player = selected != null ? selected.GetComponentInParent<VideoEffectPlayer>(true) : null;
                if (player == null) throw new ArgumentException("생성한 영상 이펙트 루트 또는 자식을 선택하세요.");
                previewPlayer = player;
                playerField.SetValueWithoutNotify(player);
                RefreshState();
            })) { text = "현재 선택에서 플레이어 가져오기" });
            previewButtons = Row(section);
            PreviewButton("처음부터 재생", player => player.Play());
            PreviewButton("일시정지", player => player.Pause());
            PreviewButton("계속 재생", player => player.Resume());
            PreviewButton("정지", player => player.Stop());
            playerStatus = new Label();
            playerStatus.AddToClassList("video-effects-description");
            section.Add(playerStatus);
        }

        private void SaveEffect()
        {
            if (sourceInput == 2) throw new ArgumentException("먼저 변환을 완료하거나 기존 영상 경로를 선택하세요.");
            string path = VideoEffectEditorPaths.Normalize(effectPath);
            VideoEffectEditorPaths.NewAssetPath(path, ".asset", false);
            if (contentSize.x <= 0 || contentSize.y <= 0) throw new ArgumentException("표시 너비와 높이를 1 이상으로 입력하세요.");
            if (float.IsNaN(speed) || float.IsInfinity(speed) || speed < 0.05f || speed > 4)
                throw new ArgumentException("재생 속도는 0.05~4 사이로 입력하세요. 플랫폼별 지원 범위는 다를 수 있습니다.");
            if (sourceInput == 0 && (clip == null || !AssetDatabase.Contains(clip)))
                throw new ArgumentException("프로젝트의 VideoClip을 선택하세요.");
            if (sourceInput == 0)
            {
                string clipPath = AssetDatabase.GetAssetPath(clip);
                if (clipPath.StartsWith("Assets/", StringComparison.Ordinal)) VideoEffectEditorPaths.AssetPath(clipPath, false);
                else if (Array.Exists(clipPath.Split('/'), part => part.Equals("Editor", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Editor 폴더의 영상은 게임에 포함되지 않습니다. 재생용 영상을 선택하세요.");
            }
            if (sourceInput == 1) location = VideoEffectEditorPaths.StreamingLocation(location);
            var effect = CreateInstance<VideoEffectAsset>();
            try
            {
                effect.name = Path.GetFileNameWithoutExtension(path);
                effect.source = sourceInput == 0 ? VideoSourceKind.VideoClip : VideoSourceKind.StreamingAssets;
                effect.clip = sourceInput == 0 ? clip : null;
                effect.location = sourceInput == 1 ? location : "";
                effect.alphaMode = alphaMode;
                effect.premultiplied = premultiplied;
                effect.tint = tint;
                effect.opacity = opacity;
                effect.keyColor = keyColor;
                effect.keyThreshold = keyThreshold;
                effect.keySoftness = keySoftness;
                effect.loop = loop;
                effect.flipY = flipY;
                effect.speed = speed;
                effect.audioEnabled = audioEnabled;
                effect.contentSize = contentSize;
                if (!effect.Validate(out string reason)) throw new ArgumentException(reason);
                AssetDatabase.CreateAsset(effect, path);
                AssetDatabase.SaveAssetIfDirty(effect);
                savedAsset = effect;
                savedField.SetValueWithoutNotify(effect);
                EditorGUIUtility.PingObject(effect);
                ShowStatus("새 에셋을 저장했습니다: " + path + "\n아래에서 배치 방식을 선택하고 씬 오브젝트를 만드세요.");
            }
            finally
            {
                if (!AssetDatabase.Contains(effect)) DestroyImmediate(effect);
            }
        }

        private void ApplyImport(VideoAlphaMode mode)
        {
            Run(() =>
            {
                if (sourceInput != 0) throw new InvalidOperationException("기존 VideoClip 입력을 선택하세요.");
                VideoEffectImportSettings.Apply(clip, mode);
                ShowStatus("지정한 VideoClip의 임포트 설정을 적용했습니다. 합성 방식은 위 드롭다운에서 선택하세요.");
            });
        }

        private void UseClipSize()
        {
            if (clip == null) return;
            int width = (int)Math.Min(clip.width, int.MaxValue);
            int height = (int)Math.Min(clip.height, int.MaxValue);
            if (alphaMode == VideoAlphaMode.PackedAlpha) width /= 2;
            contentSize = new Vector2Int(width, height);
            sizeField?.SetValueWithoutNotify(contentSize);
        }

        private void ConversionChanged()
        {
            if (conversionStatus == null) return;
            conversionStatus.text = VideoEffectConversionOperation.Message;
            conversionStatus.messageType = VideoEffectConversionOperation.Failed ? HelpBoxMessageType.Error : HelpBoxMessageType.Info;
            var output = VideoEffectConversionOperation.LastOutput;
            if (output != null && seenRevision != VideoEffectConversionOperation.Revision)
            {
                seenRevision = VideoEffectConversionOperation.Revision;
                sourceInput = output.Source == VideoSourceKind.VideoClip ? 0 : 1;
                clip = output.Clip;
                location = output.Location;
                alphaMode = output.AlphaMode;
                contentSize = output.Size;
                audioEnabled = false;
                sourceField.SetValueWithoutNotify(SourceLabels[sourceInput]);
                clipField.SetValueWithoutNotify(clip);
                locationField.SetValueWithoutNotify(location);
                alphaField.SetValueWithoutNotify(AlphaLabels[(int)alphaMode]);
                sizeField.SetValueWithoutNotify(contentSize);
                // Rebuild to keep all persisted toggles consistent with the imported draft.
                CreateGUI();
                return;
            }
            RefreshState();
        }

        private void RefreshState()
        {
            if (editControls == null) return;
            bool editing = !EditorApplication.isPlayingOrWillChangePlaymode && !VideoEffectConversionOperation.Busy;
            editControls.SetEnabled(editing);
            Show(clipPanel, sourceInput == 0);
            Show(streamingPanel, sourceInput == 1);
            Show(conversionPanel, sourceInput == 2);
            Show(keyPanel, alphaMode == VideoAlphaMode.BlackKey || alphaMode == VideoAlphaMode.ChromaKey);
            Show(canvasPanel, placement == VideoEffectPlacement.ExistingCanvas);
            Show(worldPanel, placement == VideoEffectPlacement.WorldSpaceCanvas);
            alphaHelp.text = AlphaDescription(alphaMode);
            alphaHelp.messageType = alphaMode == VideoAlphaMode.BlackKey ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info;
            cancelButton?.SetEnabled(VideoEffectConversionOperation.Busy);
            nativeImportButton?.SetEnabled(sourceInput == 0 && clip != null && alphaMode == VideoAlphaMode.NativeAlpha);
            packedImportButton?.SetEnabled(sourceInput == 0 && clip != null && alphaMode == VideoAlphaMode.PackedAlpha);
            createAssetButton?.SetEnabled(sourceInput != 2);
            createObjectButton?.SetEnabled(savedAsset != null);
            convertButton?.SetEnabled(!EditorApplication.isCompiling);
            previewButtons?.SetEnabled(EditorApplication.isPlaying && previewPlayer != null &&
                !EditorUtility.IsPersistent(previewPlayer) && previewPlayer.gameObject.scene.IsValid());
        }

        internal static string AlphaDescription(VideoAlphaMode mode)
        {
            switch (mode)
            {
                case VideoAlphaMode.NativeAlpha: return "영상 자체의 알파를 사용합니다. 알파 없는 MP4에는 적용할 수 없습니다. VideoClip은 Keep Alpha + Transcode가 필요합니다. Native Alpha의 플랫폼 지원은 재생 기기에서 확인하세요.";
                case VideoAlphaMode.Additive: return "밝은 색을 배경에 더합니다. 불꽃·빛에 적합하며 검정은 보이지 않습니다. 불투명한 검정 그림이나 배경을 가리는 물체에는 적합하지 않습니다.";
                case VideoAlphaMode.BlackKey: return "검정에 가까운 픽셀을 제거합니다. 검정 배경과 검정 그림을 구별할 수 없어 검정 선·그림자도 함께 사라집니다. 검정 그림을 보존하려면 실제 알파 원본과 Native / Packed Alpha를 사용하세요.";
                case VideoAlphaMode.ChromaKey: return "지정한 배경색에 가까운 픽셀을 제거합니다. 같은 색의 그림도 제거됩니다. 임계값과 부드러움을 조정해 경계를 확인하세요.";
                case VideoAlphaMode.PackedAlpha: return "왼쪽 RGB, 오른쪽 회색 알파를 읽습니다. 실제 알파 원본으로 만들면 검정 그림도 보존됩니다. 일반 MP4에 이 모드를 선택해도 알파가 생기지 않습니다. VideoClip Transcode는 꺼야 합니다.";
                default: return "전체 영상을 불투명하게 표시합니다. 영상 안의 배경도 그대로 표시됩니다. 투명도 채널이나 배경 제거가 필요하지 않을 때 사용하세요.";
            }
        }

        private void PlayModeChanged(PlayModeStateChange state) => RefreshState();

        private void UpdateStatus()
        {
            if (EditorApplication.timeSinceStartup < nextStatusTime) return;
            nextStatusTime = EditorApplication.timeSinceStartup + .25;
            RefreshState();
            if (playerStatus == null) return;
            if (!EditorApplication.isPlaying) { playerStatus.text = "편집 모드 · 미리보기는 Play Mode에서 확인하세요."; return; }
            if (previewPlayer == null) { playerStatus.text = "확인할 씬 플레이어를 선택하세요."; return; }
            string error = previewPlayer.LastError;
            playerStatus.text = string.IsNullOrEmpty(error)
                ? $"{(previewPlayer.IsPlaying ? "재생 중" : previewPlayer.IsReady ? "준비됨 / 정지" : "준비 전")} · {previewPlayer.CurrentTime:F2} / {previewPlayer.Duration:F2}초"
                : "재생 오류: " + error;
        }

        private void PreviewButton(string label, Action<VideoEffectPlayer> action)
        {
            previewButtons.Add(new Button(() => Run(() =>
            {
                if (!EditorApplication.isPlaying || previewPlayer == null || EditorUtility.IsPersistent(previewPlayer))
                    throw new InvalidOperationException("Play Mode의 씬 플레이어를 선택하세요.");
                action(previewPlayer);
            })) { text = label });
        }

        private void Run(Action action)
        {
            try { action(); }
            catch (Exception ex) { ShowStatus(ex.Message, HelpBoxMessageType.Error); }
            RefreshState();
        }

        private void ShowStatus(string message, HelpBoxMessageType type = HelpBoxMessageType.Info)
        {
            if (status == null) return;
            status.text = message;
            status.messageType = type;
        }

        private static void AddExecutablePreference(VisualElement parent, string label, string preference, string fallback)
        {
            // Assign the saved value BEFORE callbacks: defaults never replace a user's preference.
            var field = TextInput(parent, label, EditorPrefs.GetString(preference, fallback), value => EditorPrefs.SetString(preference, value));
            parent.Add(new Button(() =>
            {
                string path = EditorUtility.OpenFilePanel(label + " 실행 파일 선택", "", Application.platform == RuntimePlatform.WindowsEditor ? "exe" : "");
                if (!string.IsNullOrEmpty(path)) field.value = path;
            }) { text = label + " 실행 파일 찾기…" });
        }

        private static VisualElement Section(VisualElement parent, string heading)
        {
            var section = new VisualElement();
            section.AddToClassList("video-effects-section");
            var label = new Label(heading);
            label.AddToClassList("video-effects-heading");
            section.Add(label);
            parent.Add(section);
            return section;
        }

        private static VisualElement Row(VisualElement parent)
        {
            var row = new VisualElement();
            row.AddToClassList("video-effects-row");
            parent.Add(row);
            return row;
        }

        private static void Description(VisualElement parent, string text)
        {
            var label = new Label(text);
            label.AddToClassList("video-effects-description");
            parent.Add(label);
        }

        private static void Show(VisualElement element, bool visible) => element?.EnableInClassList("video-effects-hidden", !visible);

        private static TextField TextInput(VisualElement parent, string label, string value, Action<string> changed)
        {
            var field = new TextField(label) { value = value };
            field.RegisterValueChangedCallback(e => changed(e.newValue));
            parent.Add(field);
            return field;
        }

        private static ObjectField ObjectInput<T>(VisualElement parent, string label, T value, bool sceneObjects, Action<T> changed) where T : UnityEngine.Object
        {
            var field = new ObjectField(label) { objectType = typeof(T), allowSceneObjects = sceneObjects, value = value };
            field.RegisterValueChangedCallback(e => changed(e.newValue as T));
            parent.Add(field);
            return field;
        }

        private static DropdownField Dropdown(VisualElement parent, string label, List<string> choices, int index, Action<int> changed)
        {
            var field = new DropdownField(label, choices, Mathf.Clamp(index, 0, choices.Count - 1));
            field.RegisterValueChangedCallback(e => changed(choices.IndexOf(e.newValue)));
            parent.Add(field);
            return field;
        }

        private static void ToggleInput(VisualElement parent, string label, bool value, Action<bool> changed, string tooltip = "")
        {
            var field = new Toggle(label) { value = value, tooltip = tooltip };
            field.RegisterValueChangedCallback(e => changed(e.newValue));
            parent.Add(field);
        }

        private static void FloatInput(VisualElement parent, string label, float value, Action<float> changed)
        {
            var field = new FloatField(label) { value = value };
            field.RegisterValueChangedCallback(e => changed(e.newValue));
            parent.Add(field);
        }

        private static void SliderInput(VisualElement parent, string label, float value, float low, float high, Action<float> changed)
        {
            var field = new Slider(label, low, high) { value = value, showInputField = true };
            field.RegisterValueChangedCallback(e => changed(e.newValue));
            parent.Add(field);
        }
    }
}
