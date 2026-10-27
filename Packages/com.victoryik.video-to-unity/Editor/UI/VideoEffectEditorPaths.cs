using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace VideoToUnity.Editor
{
    internal static class VideoEffectEditorPaths
    {
        internal const string StyleGuid = "b1a691f2a7d1449a92b0c8c8629cc13f";

        internal static void AddStyle(VisualElement root)
        {
            string path = AssetDatabase.GUIDToAssetPath(StyleGuid);
            var style = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            if (style == null)
            {
                foreach (string guid in AssetDatabase.FindAssets("VideoEffectWindow t:StyleSheet"))
                {
                    path = AssetDatabase.GUIDToAssetPath(guid);
                    if (Path.GetFileName(path) != "VideoEffectWindow.uss") continue;
                    style = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                    if (style != null) break;
                }
            }
            if (style != null && !root.styleSheets.Contains(style)) root.styleSheets.Add(style);
        }

        internal static string Normalize(string path) => (path ?? "").Trim().Replace('\\', '/');

        internal static bool IsStreamingAssets(string path) =>
            Normalize(path).StartsWith("Assets/StreamingAssets/", StringComparison.Ordinal);

        internal static string NewAssetPath(string path, string extension, bool streaming)
        {
            string full = AssetPath(path, streaming);
            if (!string.Equals(Path.GetExtension(full), extension, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(extension + " 확장자로 저장하세요.");
            if (!Directory.Exists(Path.GetDirectoryName(full)))
                throw new ArgumentException("저장 폴더가 없습니다. 경로 선택 창에서 폴더를 만든 뒤 선택하세요.");
            if (File.Exists(full) || Directory.Exists(full) || File.Exists(full + ".meta") ||
                !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(Normalize(path))))
                throw new ArgumentException("이미 존재하는 파일 또는 .meta 경로입니다. 새 이름을 선택하세요.");
            return full;
        }

        internal static string AssetPath(string path, bool streaming)
        {
            path = Normalize(path);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("프로젝트 Assets 아래의 경로를 입력하세요.");
            foreach (string part in path.Split('/'))
            {
                if (string.IsNullOrWhiteSpace(part) || part == "." || part == ".." ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(":"))
                    throw new ArgumentException("빈 경로, 상대 이동(..), 잘못된 파일명은 사용할 수 없습니다.");
                if (part.Equals("Editor", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("재생용 에셋은 Editor 폴더 밖에 저장하세요.");
            }
            if (IsStreamingAssets(path) != streaming ||
                (!streaming && path.StartsWith("Assets/StreamingAssets/", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException(streaming
                    ? "Assets/StreamingAssets/ 아래의 파일을 선택하세요."
                    : "이 에셋은 StreamingAssets 밖에 저장하세요.");
            string assets = Path.GetFullPath(Application.dataPath);
            string full = Path.GetFullPath(Path.Combine(assets, path.Substring("Assets/".Length)));
            if (!full.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Assets 밖의 경로는 사용할 수 없습니다.");
            // Reject redirected descendants so a destination cannot escape Assets via a junction.
            var directory = new DirectoryInfo(Path.GetDirectoryName(full));
            while (directory != null && !string.Equals(directory.FullName, assets, StringComparison.OrdinalIgnoreCase))
            {
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("심볼릭 링크 또는 junction 아래에는 저장할 수 없습니다.");
                directory = directory.Parent;
            }
            return full;
        }

        internal static string StreamingLocation(string location)
        {
            location = Normalize(location);
            string full = AssetPath("Assets/StreamingAssets/" + location, true);
            if (!File.Exists(full)) throw new ArgumentException("StreamingAssets 영상 파일이 없습니다: " + location);
            return location;
        }
    }
}
