using System;
using System.IO;
using UnityEngine;
using UnityEngine.Video;

namespace VideoToUnity
{
    public enum VideoAlphaMode { NativeAlpha, Additive, BlackKey, ChromaKey, PackedAlpha, Opaque }
    public enum VideoSourceKind { VideoClip, StreamingAssets, Url }
    public enum VideoFit { Contain, Cover, Stretch }

    /// <summary>A reusable description; playback instances never modify this asset.</summary>
    [CreateAssetMenu(menuName = "Video to Unity/Video Effect", fileName = "VideoEffect")]
    public sealed class VideoEffectAsset : ScriptableObject
    {
        public VideoSourceKind source;
        public VideoClip clip;
        [Tooltip("StreamingAssets-relative path, or absolute file/HTTP(S) URL depending on Source.")]
        public string location = "";
        public VideoAlphaMode alphaMode = VideoAlphaMode.NativeAlpha;
        [Tooltip("Enable only if the RGB is already multiplied by its alpha.")]
        public bool premultiplied;
        public Color tint = Color.white;
        [Range(0, 1)] public float opacity = 1;
        public Color keyColor = Color.green;
        [Range(0, 1)] public float keyThreshold = 0.05f;
        [Range(0.001f, 1)] public float keySoftness = 0.08f;
        public bool loop;
        public bool holdLastFrame;
        public bool flipY;
        [Range(0.05f, 4)] public float speed = 1;
        public bool audioEnabled;
        [Tooltip("Logical content dimensions; packed videos have twice this width on disk. Zero uses decoder dimensions.")]
        public Vector2Int contentSize;
        [Tooltip("Packed alpha is grayscale data. Undo the RGB gamma conversion in Linear projects.")]
        public bool correctPackedAlphaGamma = true;

        public bool Validate(out string reason)
        {
            if (!Enum.IsDefined(typeof(VideoSourceKind), source) || !Enum.IsDefined(typeof(VideoAlphaMode), alphaMode))
            { reason = "Unknown video source or compositing mode."; return false; }
            if (source == VideoSourceKind.VideoClip && clip == null)
            { reason = "Assign a VideoClip."; return false; }
            if (source != VideoSourceKind.VideoClip && string.IsNullOrWhiteSpace(location))
            { reason = "Assign a media path or URL."; return false; }
            if (source == VideoSourceKind.StreamingAssets)
            {
                string path = location.Replace('\\', '/');
                if (Path.IsPathRooted(path) || path.Contains(":") || path.StartsWith("/") ||
                    Array.Exists(path.Split('/'), part => part == ".."))
                { reason = "StreamingAssets requires a relative path without '..'."; return false; }
            }
            if (float.IsNaN(speed) || float.IsInfinity(speed) || speed < 0.05f || speed > 4)
            { reason = "Playback speed must be between 0.05 and 4."; return false; }
#if UNITY_WEBGL && !UNITY_EDITOR
            if (source == VideoSourceKind.VideoClip)
            { reason = "Web builds require StreamingAssets or URL, not a VideoClip."; return false; }
#endif
            reason = null; return true;
        }

        public string ResolveUrl()
        {
            if (source == VideoSourceKind.VideoClip) return "";
            string path = (location ?? "").Trim();
            if (source == VideoSourceKind.StreamingAssets)
            {
                string root = Application.streamingAssetsPath.Replace('\\', '/').TrimEnd('/');
                string relative = path.Replace('\\', '/');
                if (root.Contains("://"))
                    return root + "/" + string.Join("/", Array.ConvertAll(relative.Split('/'), Uri.EscapeDataString));
                return new Uri(Path.GetFullPath(Path.Combine(root, relative))).AbsoluteUri;
            }
            if (Path.IsPathRooted(path)) return new Uri(Path.GetFullPath(path)).AbsoluteUri;
            return path;
        }
    }
}
