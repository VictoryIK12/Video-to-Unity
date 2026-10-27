using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine.Video;

namespace VideoToUnity.Editor
{
    public static class VideoEffectImportSettings
    {
        public static void Apply(VideoClip clip, VideoAlphaMode mode)
        {
            if (clip == null) throw new ArgumentException("먼저 VideoClip을 선택하세요.");
            Apply(AssetDatabase.GetAssetPath(clip), mode);
        }

        internal static void Apply(string path, VideoAlphaMode mode)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("영상 임포트 설정은 편집 모드에서 변경하세요.");
            path = VideoEffectEditorPaths.Normalize(path);
            VideoEffectEditorPaths.AssetPath(path, false);
            var importer = AssetImporter.GetAtPath(path) as VideoClipImporter;
            if (importer == null) throw new ArgumentException("선택 파일의 VideoClipImporter를 찾을 수 없습니다.");
            if (mode != VideoAlphaMode.PackedAlpha && mode != VideoAlphaMode.NativeAlpha)
                throw new ArgumentException("Packed Alpha 또는 Native Alpha 설정만 적용할 수 있습니다.");
            bool native = mode == VideoAlphaMode.NativeAlpha;
            if (native && !importer.sourceHasAlpha)
                throw new ArgumentException("Unity가 원본 알파를 감지하지 못했습니다. 알파 원본을 변환하거나 다른 합성 방식을 선택하세요.");

            // Update existing overrides as well as defaults; do not create platform overrides.
            var overrides = new Dictionary<string, VideoImporterTargetSettings>();
            var names = new HashSet<string>(Enum.GetNames(typeof(BuildTargetGroup)))
            {
                "iPhone", "Windows Store Apps"
            };
            names.Remove("Unknown");
            foreach (string name in names)
            {
                try
                {
                    var settings = importer.GetTargetSettings(name);
                    if (settings != null) overrides[name] = settings;
                }
                catch (ArgumentException) { /* Removed/unsupported platform names differ between Unity versions. */ }
            }
            Undo.RecordObject(importer, "영상 알파 임포트 설정");
            importer.keepAlpha = native;
            if (!native)
            {
                importer.flipHorizontal = false;
                importer.flipVertical = false;
                importer.deinterlaceMode = VideoDeinterlaceMode.Off;
                importer.importAudio = true;
            }
            var defaults = importer.defaultTargetSettings;
            SetTranscoding(defaults, native);
            importer.defaultTargetSettings = defaults;
            foreach (var entry in overrides)
            {
                SetTranscoding(entry.Value, native);
                importer.SetTargetSettings(entry.Key, entry.Value);
            }
            importer.SaveAndReimport();
        }

        private static void SetTranscoding(VideoImporterTargetSettings settings, bool native)
        {
            settings.enableTranscoding = native;
            if (native)
            {
                settings.resizeMode = VideoResizeMode.OriginalSize;
                settings.aspectRatio = VideoEncodeAspectRatio.NoScaling;
            }
        }
    }
}
