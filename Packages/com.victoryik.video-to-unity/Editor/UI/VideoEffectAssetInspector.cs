using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace VideoToUnity.Editor
{
    [CustomEditor(typeof(VideoEffectAsset))]
    public sealed class VideoEffectAssetInspector : UnityEditor.Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            VideoEffectEditorPaths.AddStyle(root);
            root.AddToClassList("video-effects-section");
            root.Add(new HelpBox("Video to Unity 플러그인의 설정 에셋입니다. 영상은 별도 VideoClip/파일에 저장됩니다. 이 설정을 변경하면 같은 에셋을 사용하는 모든 플레이어에 반영됩니다.", HelpBoxMessageType.Info));
            Add(root, "source", "영상 위치 (Source)");
            Add(root, "clip", "VideoClip");
            Add(root, "location", "StreamingAssets 상대 경로 / URL");
            Add(root, "alphaMode", "합성 방식");
            var explanation = new HelpBox("", HelpBoxMessageType.Info);
            root.Add(explanation);
            Add(root, "premultiplied", "Premultiplied 원본");
            Add(root, "tint", "색상 곱하기");
            Add(root, "opacity", "불투명도");
            Add(root, "keyColor", "Chroma Key 제거 색");
            Add(root, "keyThreshold", "제거 임계값");
            Add(root, "keySoftness", "경계 부드러움");
            Add(root, "loop", "반복 재생");
            AddOptional(root, "holdLastFrame", "재생 종료 후 마지막 프레임 유지");
            Add(root, "flipY", "상하 반전");
            Add(root, "speed", "재생 속도");
            Add(root, "audioEnabled", "원본 오디오 재생");
            Add(root, "contentSize", "표시 크기 (Packed는 RGB 한쪽)");
            AddOptional(root, "correctPackedAlphaGamma", "Linear 색 공간에서 Packed 알파 감마 보정");
            var validation = new HelpBox("", HelpBoxMessageType.Info);
            root.Add(validation);
            root.Add(new Button(VideoEffectWindow.Open) { text = "영상 이펙트 만들기 창 열기" });
            void Refresh()
            {
                if (target == null) return;
                var effect = (VideoEffectAsset)target;
                explanation.text = VideoEffectWindow.AlphaDescription(effect.alphaMode);
                bool valid = effect.Validate(out string reason);
                bool unusedClip = effect.source != VideoSourceKind.VideoClip && effect.clip != null;
                validation.text = unusedClip
                    ? "경로 기반 재생에서는 VideoClip 칸을 비우세요. 남아 있는 참조는 Web 빌드에 불필요한 VideoClip 의존성을 만듭니다."
                    : valid ? "기본 설정 확인됨. 실제 영상 디코딩과 알파는 대상 플랫폼의 Play Mode / 빌드에서 확인하세요." : reason;
                validation.messageType = unusedClip ? HelpBoxMessageType.Warning : valid ? HelpBoxMessageType.Info : HelpBoxMessageType.Error;
            }
            root.RegisterCallback<SerializedPropertyChangeEvent>(_ => Refresh());
            root.schedule.Execute(Refresh).Every(500);
            Refresh();
            return root;
        }

        private void Add(VisualElement root, string property, string label)
        {
            root.Add(new PropertyField(serializedObject.FindProperty(property), label));
        }

        private void AddOptional(VisualElement root, string property, string label)
        {
            var value = serializedObject.FindProperty(property);
            if (value != null) root.Add(new PropertyField(value, label));
        }
    }
}
