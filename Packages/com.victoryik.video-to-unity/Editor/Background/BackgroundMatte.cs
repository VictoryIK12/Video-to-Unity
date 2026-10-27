using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace VideoToUnity.Editor
{
    public static class BackgroundMatte
    {
        public static BackgroundAnalysis Analyze(IList<byte[]> frames, int width, int height)
        {
            int size = BackgroundMatteSession.CheckedPixels(width, height);
            if (frames == null || frames.Count == 0) throw new ArgumentException("No analysis frames.");
            var histogram = new int[32768];
            var red = new long[32768]; var green = new long[32768]; var blue = new long[32768];
            int border = Math.Max(1, Math.Min(width, height) / 20), total = 0, best = 0;
            foreach (var frame in frames)
            {
                if (frame == null || frame.Length != size * 4) throw new ArgumentException("Analysis frame size mismatch.");
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    if (x >= border && x < width - border && y >= border && y < height - border) continue;
                    int p = (y * width + x) * 4;
                    if (frame[p + 3] == 0) continue;
                    int bin = ((frame[p] >> 3) << 10) | ((frame[p + 1] >> 3) << 5) | (frame[p + 2] >> 3);
                    histogram[bin]++; red[bin] += frame[p]; green[bin] += frame[p + 1]; blue[bin] += frame[p + 2]; total++;
                    if (histogram[bin] > histogram[best]) best = bin;
                }
            }
            if (total == 0) return new BackgroundAnalysis { Message = "No visible border pixels; choose a background color manually." };
            byte r = (byte)(red[best] / histogram[best]), g = (byte)(green[best] / histogram[best]), b = (byte)(blue[best] / histogram[best]);
            int support = 0;
            foreach (var frame in frames)
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    if (x >= border && x < width - border && y >= border && y < height - border) continue;
                    int p = (y * width + x) * 4;
                    if (frame[p + 3] != 0 && Distance(frame, p, r, g, b) <= .08f) support++;
                }
            double confidence = (double)support / total;
            return new BackgroundAnalysis { Red = r, Green = g, Blue = b, Confidence = confidence,
                Message = confidence >= .6 ? "Consistent border color detected. Review before converting." : "Mixed border colors. Select the intended background color manually." };
        }

        internal static float Distance(byte[] rgba, int p, byte r, byte g, byte b)
        { return Math.Max(Math.Abs(rgba[p] - r), Math.Max(Math.Abs(rgba[p + 1] - g), Math.Abs(rgba[p + 2] - b))) / 255f; }
    }

    public sealed class BackgroundMatteSession
    {
        private readonly int width, height, pixels, bands;
        private readonly BackgroundOptions options;
        private readonly bool[] protectedPixels, reached;
        private readonly int[] queue;
        private readonly byte[] previousRgb;
        private readonly float[] alpha, previousAlpha, keyAlpha = new float[256];
        private bool hasPrevious;
        private static readonly float[] Linear = MakeLinear();
        private static readonly byte[] Srgb = MakeSrgb();

        public BackgroundMatteSession(int width, int height, BackgroundOptions settings, BackgroundAnalysis analysis = null)
        {
            pixels = CheckedPixels(width, height); this.width = width; this.height = height;
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            options = settings.Copy(); Validate(options);
            if (options.AutoDetect)
            {
                if (analysis == null || !analysis.Reliable) throw new ArgumentException("Automatic background detection is uncertain. Choose a background color manually.");
                options.Red = analysis.Red; options.Green = analysis.Green; options.Blue = analysis.Blue;
            }
            protectedPixels = new bool[pixels]; reached = new bool[pixels]; queue = new int[pixels];
            previousRgb = new byte[pixels * 3]; alpha = new float[pixels]; previousAlpha = new float[pixels];
            // Key alpha depends only on the largest channel difference (0..255): the same float
            // arithmetic as the per-pixel formula, evaluated once per possible difference.
            for (int difference = 0; difference < 256; difference++)
            {
                float t = (difference / 255f - options.Threshold) / options.Softness;
                t = Math.Max(0, Math.Min(1, t));
                keyAlpha[difference] = t * t * (3 - 2 * t);
            }
            // Per-pixel passes are independent, so bands give identical bytes on any core count. At most
            // eight workers, and one logical processor stays free for the Editor's main thread.
            bands = Math.Max(1, Math.Min(Math.Min(8, Environment.ProcessorCount - 1), height / 16));
            foreach (var region in options.ProtectedRegions)
            {
                int left = (int)Math.Floor(region.X * width), top = (int)Math.Floor(region.Y * height);
                int right = Math.Min(width, (int)Math.Ceiling((region.X + region.Width) * width));
                int bottom = Math.Min(height, (int)Math.Ceiling((region.Y + region.Height) * height));
                for (int y = top; y < bottom; y++) for (int x = left; x < right; x++) protectedPixels[y * width + x] = true;
            }
        }

        public static int CheckedPixels(int width, int height)
        {
            if (width < 1 || height < 1 || width > 8192 || height > 8192 || (long)width * height > 16777216)
                throw new ArgumentOutOfRangeException(nameof(width), "Background processing supports up to 16 megapixels, maximum dimension 8192.");
            return checked(width * height);
        }

        public static void Validate(BackgroundOptions value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (!Range(value.Threshold, 0, 1) || !Range(value.Softness, .001f, 1) || !Range(value.TemporalSmoothing, 0, .9f))
                throw new ArgumentException("Invalid key threshold, softness or temporal smoothing.");
            if (value.ProtectedRegions == null) return;
            if (value.ProtectedRegions.Length > 64) throw new ArgumentException("At most 64 protected rectangles are supported.");
            foreach (var r in value.ProtectedRegions)
                if (!Range(r.X,0,1) || !Range(r.Y,0,1) || !Range(r.Width,0,1) || !Range(r.Height,0,1) ||
                    r.Width <= 0 || r.Height <= 0 || r.X + r.Width > 1.000001f || r.Y + r.Height > 1.000001f)
                    throw new ArgumentException("Protected rectangles must be within normalized source bounds (top-left origin).");
        }

        public void Process(byte[] rgba, byte[] output, CancellationToken token = default)
        {
            if (rgba == null || output == null || rgba.Length != pixels * 4 || output.Length != pixels * 4 || ReferenceEquals(rgba, output))
                throw new ArgumentException("Separate, exact-sized RGBA buffers are required.");
            token.ThrowIfCancellationRequested();
            Bands(token, (start, end) => Key(rgba, start, end, token));
            if (options.BorderConnected) Flood(token);
            Bands(token, (start, end) => Compose(rgba, output, start, end, token));
            hasPrevious = true;
        }

        private void Bands(CancellationToken token, Action<int, int> body)
        {
            if (bands == 1) body(0, pixels);
            else
            {
                try
                {
                    Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = bands },
                        band => body((int)((long)pixels * band / bands), (int)((long)pixels * (band + 1) / bands)));
                }
                catch (AggregateException ex) { ExceptionDispatchInfo.Capture(ex.Flatten().InnerExceptions[0]).Throw(); }
            }
            token.ThrowIfCancellationRequested(); // Bodies stop early on cancellation; report it here.
        }

        private void Key(byte[] rgba, int start, int end, CancellationToken token)
        {
            byte r = options.Red, g = options.Green, b = options.Blue;
            float[] key = keyAlpha, a = alpha; bool[] keep = protectedPixels;
            for (int i = start; i < end; i++)
            {
                if ((i & 8191) == 0 && token.IsCancellationRequested) return;
                int p = i * 4;
                int d = Math.Abs(rgba[p] - r), dg = Math.Abs(rgba[p + 1] - g), db = Math.Abs(rgba[p + 2] - b);
                if (dg > d) d = dg;
                if (db > d) d = db;
                a[i] = keep[i] ? 1 : key[d];
            }
        }

        // 4-connected pixels below full alpha that touch the border. Protected pixels are already 1.
        private void Flood(CancellationToken token)
        {
            bool[] seen = reached; float[] a = alpha; int[] q = queue; int w = width, last = pixels - w, head = 0, tail = 0;
            Array.Clear(seen, 0, pixels);
            for (int x = 0; x < w; x++)
            {
                if (a[x] < 1 && !seen[x]) { seen[x] = true; q[tail++] = x; }
                int bottom = last + x; if (a[bottom] < 1 && !seen[bottom]) { seen[bottom] = true; q[tail++] = bottom; }
            }
            for (int y = 0, i = 0; y < height; y++, i += w)
            {
                if (a[i] < 1 && !seen[i]) { seen[i] = true; q[tail++] = i; }
                int right = i + w - 1; if (a[right] < 1 && !seen[right]) { seen[right] = true; q[tail++] = right; }
            }
            while (head < tail)
            {
                if ((head & 8191) == 0) token.ThrowIfCancellationRequested();
                int i = q[head++], x = i % w, n;
                if (x > 0 && !seen[n = i - 1] && a[n] < 1) { seen[n] = true; q[tail++] = n; }
                if (x < w - 1 && !seen[n = i + 1] && a[n] < 1) { seen[n] = true; q[tail++] = n; }
                if (i >= w && !seen[n = i - w] && a[n] < 1) { seen[n] = true; q[tail++] = n; }
                if (i < last && !seen[n = i + w] && a[n] < 1) { seen[n] = true; q[tail++] = n; }
            }
        }

        private void Compose(byte[] rgba, byte[] output, int start, int end, CancellationToken token)
        {
            bool border = options.BorderConnected, history = hasPrevious;
            float smoothing = options.TemporalSmoothing;
            byte r = options.Red, g = options.Green, b = options.Blue;
            for (int i = start; i < end; i++)
            {
                if ((i & 8191) == 0 && token.IsCancellationRequested) return;
                int p = i * 4, h = i * 3;
                // Background-colored pixels the border cannot reach are enclosed artwork: opaque.
                float a = border && !reached[i] ? 1 : alpha[i];
                // Only stabilize ambiguous, nearly stationary pixels. Hard 0/1 and motion reset history.
                if (history && a > 0 && a < 1 && !protectedPixels[i] &&
                    Math.Abs(rgba[p] - previousRgb[h]) <= 4 && Math.Abs(rgba[p + 1] - previousRgb[h + 1]) <= 4 && Math.Abs(rgba[p + 2] - previousRgb[h + 2]) <= 4)
                    a = a * (1 - smoothing) + previousAlpha[i] * smoothing;
                previousAlpha[i] = a;
                previousRgb[h] = rgba[p]; previousRgb[h + 1] = rgba[p + 1]; previousRgb[h + 2] = rgba[p + 2];
                output[p + 3] = (byte)Math.Round(a * rgba[p + 3]);
                if (a <= 0 || output[p + 3] == 0) { output[p] = output[p + 1] = output[p + 2] = 0; continue; }
                if (a >= 1) { output[p] = rgba[p]; output[p + 1] = rgba[p + 1]; output[p + 2] = rgba[p + 2]; continue; }
                // Recover straight RGB from a flattened sRGB background. Alpha remains coverage data.
                output[p] = Unmix(rgba[p], r, a);
                output[p + 1] = Unmix(rgba[p + 1], g, a);
                output[p + 2] = Unmix(rgba[p + 2], b, a);
            }
        }

        private static bool Range(float x, float min, float max) { return !float.IsNaN(x) && !float.IsInfinity(x) && x >= min && x <= max; }
        private static byte Unmix(byte color, byte background, float alpha)
        { float linear = (Linear[color] - (1 - alpha) * Linear[background]) / alpha; return Srgb[(int)Math.Round(Math.Max(0, Math.Min(1, linear)) * 65535)]; }
        private static float[] MakeLinear()
        { var result = new float[256]; for (int i=0;i<256;i++) { double s=i/255.0; result[i]=(float)(s<=.04045?s/12.92:Math.Pow((s+.055)/1.055,2.4)); } return result; }
        private static byte[] MakeSrgb()
        { var result=new byte[65536]; for(int i=0;i<result.Length;i++) { double l=i/65535.0; result[i]=(byte)Math.Round(255*(l<=.0031308?l*12.92:1.055*Math.Pow(l,1/2.4)-.055)); } return result; }
    }
}
