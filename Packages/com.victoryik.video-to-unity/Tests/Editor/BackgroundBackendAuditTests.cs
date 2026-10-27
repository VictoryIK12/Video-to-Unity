using System;
using System.IO;
using NUnit.Framework;
using VideoToUnity.Editor;

namespace VideoToUnity.Tests
{
    /// <summary>
    /// Backend audit regressions that need no FFmpeg: the parallel matte must produce exactly the bytes
    /// of the previous sequential algorithm under the Editor's own runtime, and a tool that cannot
    /// start must be reported without the Editor's working directory.
    /// </summary>
    public sealed class BackgroundBackendAuditTests
    {
        private const int Width = 160, Height = 96;

        [Test] public void ParallelMatteMatchesSequentialAlgorithmByteForByte()
        {
            var sets = new[]
            {
                new BackgroundOptions { AutoDetect = false },
                new BackgroundOptions { AutoDetect = false, BorderConnected = false, TemporalSmoothing = 0, Threshold = .02f, Softness = .7f },
                new BackgroundOptions { AutoDetect = false, TemporalSmoothing = .9f, Softness = .3f,
                    ProtectedRegions = new[] { new BackgroundKeepRegion(0, 0, .2f, .25f), new BackgroundKeepRegion(.55f, .4f, .3f, .35f) } },
                new BackgroundOptions { AutoDetect = false, Red = 0, Green = 177, Blue = 64, Threshold = .1f, Softness = .25f },
            };
            for (int s = 0; s < sets.Length; s++)
            {
                var session = new BackgroundMatteSession(Width, Height, sets[s]);
                var reference = new SequentialMatte(Width, Height, sets[s]);
                var actual = new byte[Width * Height * 4]; var expected = new byte[actual.Length];
                for (int frame = 0; frame < 6; frame++)
                {
                    var source = Scene(frame, s == 3);
                    session.Process(source, actual); reference.Process(source, expected);
                    for (int i = 0; i < actual.Length; i++)
                        if (actual[i] != expected[i])
                            Assert.Fail("Option set " + s + ", frame " + frame + ", byte " + i + ": " + actual[i] + " vs sequential " + expected[i]);
                }
            }
        }

        [Test] public void ToolStartFailuresNameTheToolButNotTheWorkingDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "VideoToUnity-audit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string source = Path.Combine(root, "source.mp4");
                File.WriteAllBytes(source, new byte[] { 0, 0, 0, 24 });
                string missing = "video-to-unity-missing-tool-" + Guid.NewGuid().ToString("N");
                string workingDirectory = Directory.GetCurrentDirectory();

                using (var job = BackgroundVideoProcessor.Start(new BackgroundRequest { SourcePath = source, TemporaryDirectory = root,
                    Ffmpeg = missing, Ffprobe = missing, Kind = BackgroundJobKind.Analyze }))
                {
                    Assert.Throws<AggregateException>(() => job.Completion.Wait(TimeSpan.FromSeconds(30)));
                    string message = job.Completion.Exception.InnerException.Message;
                    StringAssert.Contains("Could not start video tool '" + missing + "'", message);
                    StringAssert.DoesNotContain(workingDirectory, message);
                }

                var converter = VideoConverter.Start(source, root, missing, missing);
                try
                {
                    Assert.Throws<AggregateException>(() => converter.Completion.Wait(TimeSpan.FromSeconds(30)));
                    string message = converter.Completion.Exception.InnerException.Message;
                    StringAssert.Contains("Could not start video tool '" + missing + "'", message);
                    StringAssert.DoesNotContain(workingDirectory, message);
                }
                finally { converter.Dispose(); }
            }
            finally
            {
                // Both jobs failed before creating files; only our own fixture remains.
                foreach (string file in Directory.GetFiles(root)) File.Delete(file);
                Directory.Delete(root, false);
            }
        }

        // Black (or key-green) background, a soft moving glow, a white ring enclosing background-colored
        // artwork, and per-frame noise that alternates between still and moving pixels.
        private static byte[] Scene(int frame, bool green)
        {
            var rgba = new byte[Width * Height * 4];
            var random = new Random(frame * 7919 + (green ? 1 : 0));
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int p = (y * Width + x) * 4;
                    double dx = x - 50 - frame * 3, dy = y - 48, glow = Math.Max(0, 1 - Math.Sqrt(dx * dx + dy * dy) / 30);
                    double ring = Math.Sqrt((x - 115) * (x - 115) + (y - 40) * (y - 40));
                    int r = green ? 0 : 0, g = green ? 177 : 0, b = green ? 64 : 0;
                    r = (int)Math.Round(r * (1 - glow) + 240 * glow); g = (int)Math.Round(g * (1 - glow) + 120 * glow); b = (int)Math.Round(b * (1 - glow) + 40 * glow);
                    if (ring >= 14 && ring < 18) r = g = b = 255;
                    if ((x + y + frame) % 13 == 0) { int n = random.Next(-6, 7); r = Clamp(r + n); g = Clamp(g + n); b = Clamp(b + n); }
                    rgba[p] = (byte)r; rgba[p + 1] = (byte)g; rgba[p + 2] = (byte)b; rgba[p + 3] = 255;
                }
            return rgba;
        }

        private static int Clamp(int value) { return Math.Max(0, Math.Min(255, value)); }

        /// <summary>The pre-audit sequential BackgroundMatteSession algorithm, kept as the byte-exact reference.</summary>
        private sealed class SequentialMatte
        {
            private readonly int width, height, pixels;
            private readonly BackgroundOptions options;
            private readonly bool[] protectedPixels, reached;
            private readonly int[] queue;
            private readonly byte[] previousRgb;
            private readonly float[] alpha, previousAlpha;
            private bool hasPrevious;
            private static readonly float[] Linear = MakeLinear();
            private static readonly byte[] Srgb = MakeSrgb();

            public SequentialMatte(int width, int height, BackgroundOptions settings)
            {
                pixels = width * height; this.width = width; this.height = height; options = settings.Copy();
                protectedPixels = new bool[pixels]; reached = new bool[pixels]; queue = new int[pixels];
                previousRgb = new byte[pixels * 3]; alpha = new float[pixels]; previousAlpha = new float[pixels];
                foreach (var region in options.ProtectedRegions)
                {
                    int left = (int)Math.Floor(region.X * width), top = (int)Math.Floor(region.Y * height);
                    int right = Math.Min(width, (int)Math.Ceiling((region.X + region.Width) * width));
                    int bottom = Math.Min(height, (int)Math.Ceiling((region.Y + region.Height) * height));
                    for (int y = top; y < bottom; y++) for (int x = left; x < right; x++) protectedPixels[y * width + x] = true;
                }
            }

            public void Process(byte[] rgba, byte[] output)
            {
                for (int i = 0; i < pixels; i++)
                {
                    float t = (Distance(rgba, i * 4, options.Red, options.Green, options.Blue) - options.Threshold) / options.Softness;
                    t = Math.Max(0, Math.Min(1, t));
                    alpha[i] = protectedPixels[i] ? 1 : t * t * (3 - 2 * t);
                }
                if (options.BorderConnected)
                {
                    Array.Clear(reached, 0, pixels); int head = 0, tail = 0;
                    for (int x = 0; x < width; x++) { Enqueue(x, ref tail); Enqueue((height - 1) * width + x, ref tail); }
                    for (int y = 0; y < height; y++) { Enqueue(y * width, ref tail); Enqueue(y * width + width - 1, ref tail); }
                    while (head < tail)
                    {
                        int i = queue[head++], x = i % width;
                        if (x > 0) Enqueue(i - 1, ref tail);
                        if (x < width - 1) Enqueue(i + 1, ref tail);
                        if (i >= width) Enqueue(i - width, ref tail);
                        if (i < pixels - width) Enqueue(i + width, ref tail);
                    }
                    for (int i = 0; i < pixels; i++) if (!reached[i]) alpha[i] = 1;
                }
                for (int i = 0; i < pixels; i++)
                {
                    int p = i * 4, history = i * 3;
                    float a = alpha[i];
                    if (hasPrevious && a > 0 && a < 1 && !protectedPixels[i] &&
                        Math.Abs(rgba[p] - previousRgb[history]) <= 4 && Math.Abs(rgba[p + 1] - previousRgb[history + 1]) <= 4 && Math.Abs(rgba[p + 2] - previousRgb[history + 2]) <= 4)
                        a = a * (1 - options.TemporalSmoothing) + previousAlpha[i] * options.TemporalSmoothing;
                    previousAlpha[i] = a;
                    for (int c = 0; c < 3; c++) previousRgb[history + c] = rgba[p + c];
                    output[p + 3] = (byte)Math.Round(a * rgba[p + 3]);
                    if (a <= 0 || output[p + 3] == 0) { output[p] = output[p + 1] = output[p + 2] = 0; continue; }
                    if (a >= 1) { output[p] = rgba[p]; output[p + 1] = rgba[p + 1]; output[p + 2] = rgba[p + 2]; continue; }
                    output[p] = Unmix(rgba[p], options.Red, a);
                    output[p + 1] = Unmix(rgba[p + 1], options.Green, a);
                    output[p + 2] = Unmix(rgba[p + 2], options.Blue, a);
                }
                hasPrevious = true;
            }

            private void Enqueue(int index, ref int tail)
            { if (!reached[index] && !protectedPixels[index] && alpha[index] < 1) { reached[index] = true; queue[tail++] = index; } }
            private static float Distance(byte[] rgba, int p, byte r, byte g, byte b)
            { return Math.Max(Math.Abs(rgba[p] - r), Math.Max(Math.Abs(rgba[p + 1] - g), Math.Abs(rgba[p + 2] - b))) / 255f; }
            private static byte Unmix(byte color, byte background, float alpha)
            { float linear = (Linear[color] - (1 - alpha) * Linear[background]) / alpha; return Srgb[(int)Math.Round(Math.Max(0, Math.Min(1, linear)) * 65535)]; }
            private static float[] MakeLinear()
            { var result = new float[256]; for (int i = 0; i < 256; i++) { double s = i / 255.0; result[i] = (float)(s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4)); } return result; }
            private static byte[] MakeSrgb()
            { var result = new byte[65536]; for (int i = 0; i < result.Length; i++) { double l = i / 65535.0; result[i] = (byte)Math.Round(255 * (l <= .0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - .055)); } return result; }
        }
    }
}
