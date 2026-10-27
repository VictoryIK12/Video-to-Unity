using System;
using System.IO;
using System.Threading;

namespace VideoToUnity.Editor
{
    internal enum QuickEffectMode { RemoveBackground, PreserveAlpha, Additive, Opaque, PackedAlpha }

    internal static class QuickWorkflowPolicy
    {
        internal static bool IsVideo(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".mp4" || extension == ".mov" || extension == ".webm" ||
                extension == ".m4v" || extension == ".avi" || extension == ".wmv" || extension == ".mkv";
        }

        internal static QuickEffectMode Recommend(QuickVideoInfo info)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (info.AlphaCapable || info.HasTransparency) return QuickEffectMode.PreserveAlpha;
            return info.Analysis != null && info.Analysis.Reliable ? QuickEffectMode.RemoveBackground : QuickEffectMode.Opaque;
        }

        internal static string FileStem(string source)
        {
            string name = Path.GetFileNameWithoutExtension(source);
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.');
            if (name.Length > 24) name = name.Substring(0, 24);
            return string.IsNullOrEmpty(name) ? "VideoEffect" : "Video_" + name;
        }

        internal static BackgroundPreview Compose(BackgroundPreview source, QuickEffectMode mode,
            BackgroundOptions options, bool linear, CancellationToken token)
        {
            if (source == null || source.OriginalRgba == null) throw new ArgumentException("미리보기 프레임이 없습니다.");
            int width = source.Width, height = source.Height;
            byte[] original = source.OriginalRgba;
            var output = new byte[original.Length];
            if (mode == QuickEffectMode.RemoveBackground)
            {
                var matte = new BackgroundMatteSession(width, height, options);
                matte.Process(original, output, token);
            }
            else if (mode == QuickEffectMode.PackedAlpha)
            {
                if (width % 2 != 0) throw new ArgumentException("좌우 알파 영상은 짝수 너비여야 합니다.");
                int half = width / 2;
                var color = new byte[half * height * 4];
                output = new byte[color.Length];
                for (int y = 0; y < height; y++) for (int x = 0; x < half; x++)
                {
                    int from = (y * width + x) * 4, to = (y * half + x) * 4;
                    Array.Copy(original, from, color, to, 4);
                    Array.Copy(original, from, output, to, 4);
                    color[to + 3] = 255;
                    output[to + 3] = original[from + half * 4];
                }
                original = color; width = half;
            }
            else
            {
                Array.Copy(original, output, original.Length);
                int block = Math.Max(4, (int)Math.Round(width / 40f));
                for (int y = 0; y < height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int x = 0; x < width; x++)
                    {
                        int p = (y * width + x) * 4;
                        if (mode == QuickEffectMode.Opaque) output[p + 3] = 255;
                        if (mode != QuickEffectMode.Additive) continue;
                        float background = ((x / block + (height - 1 - y) / block) & 1) == 0 ? 92f / 255f : 154f / 255f;
                        for (int c = 0; c < 3; c++)
                        {
                            double foreground = original[p + c] / 255.0;
                            double value = linear ? Encode(Math.Min(1, Decode(background) + Decode(foreground))) : Math.Min(1, background + foreground);
                            output[p + c] = (byte)Math.Round(value * 255);
                        }
                        output[p + 3] = 255;
                    }
                }
            }
            return new BackgroundPreview { Width = width, Height = height, OriginalRgba = original,
                ResultRgba = output, TimeSeconds = source.TimeSeconds };
        }

        private static double Decode(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        private static double Encode(double value) => value <= .0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - .055;
    }
}
