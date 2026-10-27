using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace VideoToUnity.Editor
{
    internal sealed class QuickVideoInfo
    {
        public string SourcePath;
        public int Width, Height;
        public double DurationSeconds, FramesPerSecond;
        public bool AlphaCapable, HasTransparency;
        public BackgroundAnalysis Analysis;
        public BackgroundPreview Preview;
        internal BackgroundSourceVideo Video;
        internal long SourceLength;
        internal DateTime SourceWriteTime;
    }

    internal static class QuickVideoProbe
    {
        internal static QuickVideoInfo Inspect(string source, string ffmpeg, string ffprobe, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(2)); token = timeout.Token;
                source = Path.GetFullPath(source);
                var file = new FileInfo(source);
                using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var video = BackgroundSourceVideo.Inspect(ffprobe, source, token);
                    var info = new QuickVideoInfo { SourcePath = source, Width = video.Width, Height = video.Height,
                        DurationSeconds = video.DurationSeconds, FramesPerSecond = video.FramesPerSecond,
                        Video = video, SourceLength = file.Length, SourceWriteTime = file.LastWriteTimeUtc,
                        AlphaCapable = AlphaPixelFormat(video.PixelFormat) };
                    if (video.Codec == "vp8" || video.Codec == "vp9")
                    {
                        BackgroundProcess.Run(ffprobe, new[] { "-v", "error", "-protocol_whitelist", "file", "-select_streams", "v:0",
                            "-show_entries", "stream_tags=alpha_mode", "-of", "default=noprint_wrappers=1", video.Url },
                            pipe => BackgroundProcess.Lines(pipe, line => { if (line.Trim().EndsWith("alpha_mode=1", StringComparison.OrdinalIgnoreCase)) info.AlphaCapable = true; }, token), token);
                    }
                    var frames = new List<byte[]>();
                    int width, height; PreviewSize(video.Width, video.Height, out width, out height);
                    foreach (double part in new[] { .0, .15, .4, .7, .95 })
                    {
                        int index = video.FrameAt(video.DurationSeconds * part);
                        var rgba = Decode(video, ffmpeg, index, width, height, token);
                        frames.Add(rgba);
                        for (int p = 3; p < rgba.Length; p += 4) if (rgba[p] < 255) { info.HasTransparency = true; break; }
                        if (part == .15) info.Preview = new BackgroundPreview { Width = width, Height = height,
                            OriginalRgba = rgba, ResultRgba = rgba, TimeSeconds = video.RelativeSeconds(index) };
                    }
                    info.Analysis = BackgroundMatte.Analyze(frames, width, height);
                    return info;
                }
            }
        }

        internal static void ValidateSource(QuickVideoInfo info)
        {
            if (info == null) throw new ArgumentException("영상 분석이 필요합니다.");
            var file = new FileInfo(info.SourcePath);
            if (!file.Exists || file.Length != info.SourceLength || file.LastWriteTimeUtc != info.SourceWriteTime)
                throw new IOException("원본 영상이 바뀌었습니다. 다시 끌어 넣어 결과를 확인하세요.");
        }

        internal static BackgroundPreview Preview(QuickVideoInfo info, string ffmpeg, double seconds, CancellationToken token)
        {
            ValidateSource(info);
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (new FileStream(info.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                int width, height; PreviewSize(info.Width, info.Height, out width, out height);
                int index = info.Video.FrameAt(seconds);
                byte[] rgba = Decode(info.Video, ffmpeg, index, width, height, timeout.Token);
                return new BackgroundPreview { Width = width, Height = height, OriginalRgba = rgba, ResultRgba = rgba, TimeSeconds = info.Video.RelativeSeconds(index) };
            }
        }

        internal static bool AlphaPixelFormat(string format)
        {
            format = (format ?? "").ToLowerInvariant();
            return format.StartsWith("yuva", StringComparison.Ordinal) || format.StartsWith("gbrap", StringComparison.Ordinal) ||
                format.StartsWith("rgba", StringComparison.Ordinal) || format.StartsWith("bgra", StringComparison.Ordinal) ||
                format.StartsWith("argb", StringComparison.Ordinal) || format.StartsWith("abgr", StringComparison.Ordinal) ||
                format == "ya8" || format.StartsWith("ya16", StringComparison.Ordinal) || format == "pal8";
        }

        internal static void PreviewSize(int width, int height, out int outputWidth, out int outputHeight)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            double scale = Math.Min(1, 640.0 / Math.Max(width, height));
            outputWidth = Math.Max(2, (int)Math.Round(width * scale) / 2 * 2);
            outputHeight = Math.Max(2, (int)Math.Round(height * scale) / 2 * 2);
        }

        private static byte[] Decode(BackgroundSourceVideo video, string ffmpeg, int frame, int width, int height, CancellationToken token)
        {
            if (video.GopStart(frame) > 0)
            {
                try { return DecodeOne(ffmpeg, video.DecodeArguments(width, height, frame, true), width, height, token); }
                catch (Exception ex) when (!(ex is OperationCanceledException) && !token.IsCancellationRequested) { }
            }
            return DecodeOne(ffmpeg, video.DecodeArguments(width, height, frame, true, true), width, height, token);
        }

        private static byte[] DecodeOne(string ffmpeg, List<string> arguments, int width, int height, CancellationToken token)
        {
            var rgba = new byte[checked(width * height * 4)];
            bool complete = false;
            BackgroundProcess.Run(ffmpeg, arguments, pipe =>
            {
                complete = BackgroundProcess.ReadFrame(pipe, rgba);
                if (pipe.ReadByte() != -1) throw new InvalidDataException("미리보기 데이터 크기가 예상과 다릅니다.");
            }, token);
            if (!complete) throw new InvalidDataException("영상 프레임을 읽지 못했습니다.");
            return rgba;
        }
    }
}
