using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using VideoToUnity.Editor;

// Standalone test host for the PRODUCTION BackgroundVideoProcessor + VideoConverter with real FFmpeg.
// Unity ignores this Validation~ folder. Pure matte algorithm tests belong to the main worker; here the
// parallel matte is only compared byte for byte with the previous sequential implementation.
// Console lines and evidence pass through Scrub(): machine-specific absolute paths are never recorded.
internal static class BackgroundHarness
{
    private const int W = 192, H = 108, N = 48;
    private static string root, fixtures, ffmpeg = "ffmpeg", ffprobe = "ffprobe";
    private static readonly List<Dictionary<string, object>> evidence = new List<Dictionary<string, object>>();
    private static List<string> notes;
    private static int failures;

    private static int Main(string[] args)
    {
        root = Path.GetFullPath(args[0]);
        fixtures = Path.Combine(root, "fixtures");
        Directory.CreateDirectory(fixtures);
        string only = args.Length > 1 ? args[1] : null;
        var tests = new (string, Action)[]
        {
            ("analysis-auto-black-and-color", AnalysisAutoBlackAndColor),
            ("convert-packed-auto-black", () => ConvertBlack(VideoConversionFormat.PackedH264)),
            ("convert-webm-auto-green", ConvertGreenWebM),
            ("preview-time-and-orientation", PreviewTimeAndOrientation),
            ("preview-protected-region", PreviewProtectedRegion),
            ("straight-rgb-soft-edges-packed", () => StraightSoftEdges(VideoConversionFormat.PackedH264)),
            ("straight-rgb-soft-edges-webm", () => StraightSoftEdges(VideoConversionFormat.AlphaWebM)),
            ("low-confidence-stops-auto-manual-proceeds", LowConfidence),
            ("timing-ntsc-offset-audio", TimingNtscOffsetAudio),
            ("reject-vfr", () => Reject("vfr", MakeVfr(), "Variable frame timing")),
            ("reject-odd-dimensions", () => Reject("odd", MakeOdd(), "even width and height")),
            ("reject-hdr", () => Reject("hdr", MakeHdr(), "HDR")),
            ("reject-rotation", () => Reject("rotation", MakeRotated(), "Rotation")),
            ("reject-two-video-streams", () => Reject("two-video", MakeTwoVideo(), "Exactly one video stream")),
            ("reject-truncated", RejectTruncated),
            ("reject-nothing-removed", RejectOpaque),
            ("warn-whole-video-transparent", WarnTransparent),
            ("cancel-during-frames-and-source-lock", CancelDuringFrames),
            ("dispose-during-final-encode", DisposeDuringFinalEncode),
            ("dispose-after-success-and-concurrent-unique", DisposeAfterSuccessAndConcurrent),
            ("bounded-memory-720p-full-convert", BoundedMemory),
            ("unicode-metacharacter-paths", UnicodePaths),
            ("argument-validation", ArgumentValidation),
            ("exact-source-clock-packed", ExactSourceClock),
            ("open-gop-seek-preview-and-analysis", OpenGopSeek),
            ("parallel-matte-equals-sequential-reference", ParallelMatteEqualsReference),
            ("frame-hash-matches-ffmpeg-murmur3", FrameHashMatchesFfmpeg),
            ("reject-transparent-source-accept-opaque-alpha-channel", AlphaSources),
        };
        foreach (var (name, test) in tests)
        {
            if (only != null && only != name) continue;
            notes = new List<string>();
            var watch = Stopwatch.StartNew();
            bool pass = false; string error = null;
            try { test(); pass = true; }
            catch (Exception ex) { failures++; error = Scrub(ex.ToString()); }
            notes = notes.Select(Scrub).ToList();
            Console.WriteLine((pass ? "PASS " : "FAIL ") + name + " (" + watch.ElapsedMilliseconds + "ms)" + (notes.Count > 0 ? " — " + string.Join("; ", notes) : ""));
            if (error != null) Console.WriteLine(error);
            evidence.Add(new Dictionary<string, object> { { "test", name }, { "pass", pass }, { "ms", watch.ElapsedMilliseconds }, { "notes", notes }, { "error", error } });
        }
        File.WriteAllText(Path.Combine(root, "evidence.json"), JsonSerializer.Serialize(new Dictionary<string, object> {
            { "ffmpeg", Tool(ffmpeg, "-hide_banner", "-version").Split('\n')[0].Trim() }, { "results", evidence } },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }

    // ---------- public-safe evidence ----------
    // Known roots become stable placeholders first; any remaining absolute path (including stack
    // trace source locations) becomes <path>. File names below a placeholder are kept.
    private static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var roots = new List<(string, string)> { (root, "<run>"), (AppContext.BaseDirectory, "<bin>"), (Environment.CurrentDirectory, "<cwd>"),
            (Path.GetTempPath(), "<temp>"), (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<home>") };
        foreach (var (path, name) in roots.Where(r => !string.IsNullOrEmpty(r.Item1)).OrderByDescending(r => r.Item1.Length))
        {
            string trimmed = path.TrimEnd('\\', '/');
            if (trimmed.Length < 3) continue;
            text = text.Replace(trimmed, name, StringComparison.OrdinalIgnoreCase).Replace(trimmed.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase);
        }
        text = Regex.Replace(text, @"(?<![A-Za-z0-9])[A-Za-z]:[\\/][^\s'""|<>*?]*", "<path>");
        text = Regex.Replace(text, @"\\\\[^\s'""\\]+\\[^\s'""|<>*?]*", "<path>");
        return Regex.Replace(text, @"(?<=^|[\s'""(=])/(?:home|Users|tmp|var|private|mnt|Volumes)/[^\s'""]*", "<path>");
    }

    // ---------- scenes ----------
    private static void Fill(byte[] rgb, int w, int x0, int y0, int x1, int y1, int r, int g, int b)
    {
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int p = (y * w + x) * 3; rgb[p] = (byte)r; rgb[p + 1] = (byte)g; rgb[p + 2] = (byte)b; }
    }
    private static int Marker(int f) { return 40 + 4 * f; }
    // Black background, top green bar (orientation), moving red square, per-frame gray marker.
    private static void BlackScene(int f, byte[] rgb, int w, int h)
    {
        Array.Clear(rgb, 0, rgb.Length);
        int sx = Math.Max(1, w / W), sy = Math.Max(1, h / H);
        Fill(rgb, w, 60 * sx, 4 * sy, 132 * sx, 12 * sy, 40, 200, 60);
        Fill(rgb, w, (16 + f % 48) * sx, 36 * sy, (52 + f % 48) * sx, 72 * sy, 220, 40, 40);
        int m = Marker(f % 48); Fill(rgb, w, 140 * sx, 60 * sy, 176 * sx, 96 * sy, m, m, m);
    }
    private static void GreenScene(int f, byte[] rgb, int w, int h)
    {
        Fill(rgb, w, 0, 0, w, h, 0, 177, 64);
        Fill(rgb, w, 30 + f, 30, 70 + f, 70, 200, 30, 200);
        Fill(rgb, w, 130, 50, 170, 90, 240, 240, 240);
    }
    private static void MixedScene(int f, byte[] rgb, int w, int h)
    {
        Fill(rgb, w, 0, 0, w / 2, h, 200, 20, 20);
        Fill(rgb, w, w / 2, 0, w, h, 20, 20, 200);
        Fill(rgb, w, 76, 34, 116, 74, 30, 200, 30);
    }

    private static readonly string[] H264 = { "-map", "0:v", "-vf",
        "scale=out_color_matrix=bt709:out_range=limited,format=yuv420p,setparams=range=limited:color_primaries=bt709:color_trc=bt709:colorspace=bt709",
        "-c:v", "libx264", "-preset", "medium", "-crf", "6", "-color_range", "tv", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709" };

    private static string MakeVideo(string name, int w, int h, int frames, string rate, Action<int, byte[], int, int> paint, IEnumerable<string> tail = null, string directory = null)
    {
        string path = Path.Combine(directory ?? fixtures, name);
        if (File.Exists(path)) return path;
        var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", w + "x" + h, "-framerate", rate, "-i", "pipe:0" };
        args.AddRange(tail ?? H264); args.Add("-n"); args.Add(path);
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using (var p = Process.Start(info))
        {
            var err = p.StandardError.ReadToEndAsync();
            var rgb = new byte[w * h * 3];
            for (int f = 0; f < frames; f++) { paint(f, rgb, w, h); p.StandardInput.BaseStream.Write(rgb, 0, rgb.Length); }
            p.StandardInput.Close(); p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception("fixture " + name + ": " + err.Result);
        }
        return path;
    }

    private static string Black() { return MakeVideo("black.mp4", W, H, N, "24", BlackScene); }

    // ---------- tool helpers ----------
    private static byte[] ToolBytes(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using (var p = Process.Start(info))
        {
            var err = p.StandardError.ReadToEndAsync();
            var ms = new MemoryStream(); p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception(exe + " failed: " + err.Result);
            return ms.ToArray();
        }
    }
    private static string Tool(string exe, params string[] args) { return Encoding.UTF8.GetString(ToolBytes(exe, args)); }

    private static List<double> FrameTimes(string path)
    {
        var args = new List<string> { "-v", "error", "-select_streams", "v:0" };
        if (path.EndsWith(".webm")) args.AddRange(new[] { "-c:v", "libvpx" });
        args.AddRange(new[] { "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", "-i", path });
        return Tool(ffprobe, args.ToArray()).Split('\n').Select(l => l.Trim().TrimEnd(',')).Where(l => l.Length > 0)
            .Select(l => double.Parse(l, CultureInfo.InvariantCulture)).ToList();
    }

    // Decoded final output as straight RGBA frames (packed: left RGB, right gray matte).
    private static List<byte[]> DecodeOutput(string path, int w, int h, VideoConversionFormat format)
    {
        bool packed = format == VideoConversionFormat.PackedH264;
        var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin" };
        if (!packed) args.AddRange(new[] { "-c:v", "libvpx" });
        args.AddRange(new[] { "-i", path, "-vf", packed ? "scale=out_range=full,format=rgb24" : "scale=out_range=full,format=rgba",
            "-pix_fmt", packed ? "rgb24" : "rgba", "-fps_mode", "passthrough", "-f", "rawvideo", "pipe:1" });
        var bytes = ToolBytes(ffmpeg, args.ToArray());
        int frameBytes = packed ? w * 2 * h * 3 : w * h * 4;
        if (bytes.Length % frameBytes != 0) throw new Exception("Decoded output size mismatch.");
        var frames = new List<byte[]>();
        for (int f = 0; f < bytes.Length / frameBytes; f++)
        {
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                if (packed)
                {
                    int l = f * frameBytes + (y * w * 2 + x) * 3, r = f * frameBytes + (y * w * 2 + w + x) * 3;
                    rgba[o] = bytes[l]; rgba[o + 1] = bytes[l + 1]; rgba[o + 2] = bytes[l + 2]; rgba[o + 3] = bytes[r];
                }
                else Buffer.BlockCopy(bytes, f * frameBytes + o, rgba, o, 4);
            }
            frames.Add(rgba);
        }
        return frames;
    }

    // ---------- assertions ----------
    private static void True(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pixel(byte[] rgba, int w, int x, int y, int? r, int? g, int? b, int alphaMin, int alphaMax, int tolerance, string what)
    {
        int p = (y * w + x) * 4;
        string actual = rgba[p] + "," + rgba[p + 1] + "," + rgba[p + 2] + " a=" + rgba[p + 3];
        True(rgba[p + 3] >= alphaMin && rgba[p + 3] <= alphaMax, what + ": alpha out of range [" + alphaMin + "," + alphaMax + "] actual " + actual);
        if (r.HasValue) True(Math.Abs(rgba[p] - r.Value) <= tolerance && Math.Abs(rgba[p + 1] - g.Value) <= tolerance && Math.Abs(rgba[p + 2] - b.Value) <= tolerance,
            what + ": expected " + r + "," + g + "," + b + " ±" + tolerance + " actual " + actual);
    }

    private static string TempDir(string name)
    {
        string dir = Path.Combine(root, "tmp", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sentinel.txt"), "keep");
        return dir;
    }
    private static void Clean(string dir)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var rest = Directory.GetFileSystemEntries(dir).Where(e => Path.GetFileName(e) != "sentinel.txt").ToArray();
            if (rest.Length == 0) break;
            if (watch.Elapsed.TotalSeconds > 20) throw new Exception("Temporary files remain: " + string.Join(", ", rest));
            Thread.Sleep(50);
        }
        True(File.ReadAllText(Path.Combine(dir, "sentinel.txt")) == "keep", "Pre-existing file in temporary directory changed.");
    }

    private static BackgroundRequest Request(string source, string temp, BackgroundJobKind kind, BackgroundOptions options = null)
    { return new BackgroundRequest { SourcePath = source, TemporaryDirectory = temp, Ffmpeg = ffmpeg, Ffprobe = ffprobe, Kind = kind, Options = options ?? new BackgroundOptions() }; }

    private static BackgroundResult Await(IBackgroundJob job)
    {
        if (!job.Completion.Wait(TimeSpan.FromMinutes(5))) throw new Exception("Job timed out: " + job.Status);
        return job.Completion.Result;
    }
    private static string Fails(IBackgroundJob job)
    {
        try { job.Completion.Wait(TimeSpan.FromMinutes(5)); }
        catch (AggregateException ex)
        {
            True(job.Completion.IsFaulted, "Expected a fault, got " + job.Completion.Status);
            return ex.InnerException.Message;
        }
        throw new Exception("Job unexpectedly succeeded: " + job.Status);
    }
    private static BackgroundOptions Manual(byte r, byte g, byte b)
    { return new BackgroundOptions { AutoDetect = false, Red = r, Green = g, Blue = b }; }
    private static string Hash(string path) { using (var s = File.OpenRead(path)) return Convert.ToHexString(SHA256.HashData(s)); }

    // ---------- tests ----------
    private static void AnalysisAutoBlackAndColor()
    {
        string temp = TempDir("analysis");
        foreach (var (file, scene, r, g, b) in new[] { ("black.mp4", (Action<int, byte[], int, int>)BlackScene, 0, 0, 0), ("green.mp4", GreenScene, 0, 177, 64) })
        {
            string source = MakeVideo(file, W, H, N, "24", scene);
            using (var job = BackgroundVideoProcessor.Start(Request(source, temp, BackgroundJobKind.Analyze)))
            {
                var result = Await(job);
                var a = result.Analysis;
                notes.Add(file + " color=" + a.Red + "," + a.Green + "," + a.Blue + " confidence=" + a.Confidence.ToString("0.000", CultureInfo.InvariantCulture));
                True(a.Reliable && a.Confidence > .9, "Expected reliable analysis.");
                True(Math.Abs(a.Red - r) <= 6 && Math.Abs(a.Green - g) <= 6 && Math.Abs(a.Blue - b) <= 6, "Wrong analysis color.");
                True(result.Width == W && result.Height == H && result.Preview == null && result.Conversion == null && result.Warning == null, "Wrong metadata.");
                True(Math.Abs(result.FramesPerSecond - 24) < 1e-9 && Math.Abs(result.DurationSeconds - 2) < 1e-9, "Wrong timing metadata.");
            }
        }
        Clean(temp);
    }

    private static void ConvertBlack(VideoConversionFormat format)
    {
        string temp = TempDir("convert-black-" + format), source = Black(), sourceHash = Hash(source);
        using (var job = BackgroundVideoProcessor.Start(new BackgroundRequest { SourcePath = source, TemporaryDirectory = temp, Kind = BackgroundJobKind.Convert, Format = format }))
        {
            var result = Await(job);
            var c = result.Conversion;
            True(c != null && File.Exists(c.FilePath) && c.FilePath.StartsWith(temp + Path.DirectorySeparatorChar), "Result must be inside the temporary directory.");
            True(c.Width == W && c.Height == H && c.Frames == N && c.Format == format && result.Warning == null, "Wrong conversion result.");
            True(c.MinimumAlpha <= 5 && c.MaximumAlpha >= 250, "Alpha extrema " + c.MinimumAlpha + ".." + c.MaximumAlpha);
            var frames = DecodeOutput(c.FilePath, W, H, format);
            True(frames.Count == N, "Decoded " + frames.Count + " frames.");
            for (int f = 0; f < N; f++)
            {
                var px = frames[f];
                Pixel(px, W, 2, 2, 0, 0, 0, 0, 6, 6, "corner background f" + f);
                Pixel(px, W, 100, 100, null, null, null, 0, 6, 0, "lower background f" + f);
                Pixel(px, W, 34 + f, 54, 220, 40, 40, 248, 255, 14, "red square f" + f);
                Pixel(px, W, 96, 8, 40, 200, 60, 248, 255, 14, "top bar f" + f);
                int m = Marker(f); Pixel(px, W, 158, 78, m, m, m, 248, 255, 6, "marker f" + f);
            }
            var times = FrameTimes(c.FilePath);
            True(times.Count == N, "Output frame count " + times.Count);
            double worst = 0;
            for (int f = 0; f < N; f++) worst = Math.Max(worst, Math.Abs(times[f] - times[0] - f / 24.0));
            True(worst <= .0015, "Timing error " + worst);
            notes.Add(format + " frames=" + c.Frames + " alpha=" + c.MinimumAlpha + ".." + c.MaximumAlpha + " worstTimingError=" + (worst * 1000).ToString("0.###", CultureInfo.InvariantCulture) + "ms");
        }
        True(Hash(source) == sourceHash, "Source changed.");
        Clean(temp);
    }

    private static void ConvertGreenWebM()
    {
        string temp = TempDir("convert-green"), source = MakeVideo("green.mp4", W, H, N, "24", GreenScene);
        using (var job = BackgroundVideoProcessor.Start(new BackgroundRequest { SourcePath = source, TemporaryDirectory = temp, Kind = BackgroundJobKind.Convert, Format = VideoConversionFormat.AlphaWebM }))
        {
            var result = Await(job);
            var c = result.Conversion;
            True(c.Format == VideoConversionFormat.AlphaWebM && c.FilePath.EndsWith(".webm") && c.Frames == N, "Wrong WebM result.");
            True(Math.Abs(result.Analysis.Green - 177) <= 6, "Analysis not green.");
            var frames = DecodeOutput(c.FilePath, W, H, VideoConversionFormat.AlphaWebM);
            True(frames.Count == N, "Decoded " + frames.Count + " frames.");
            for (int f = 0; f < N; f++)
            {
                Pixel(frames[f], W, 3, 3, null, null, null, 0, 6, 0, "green background f" + f);
                Pixel(frames[f], W, 180, 100, null, null, null, 0, 6, 0, "green background corner f" + f);
                Pixel(frames[f], W, 50 + f, 50, 200, 30, 200, 248, 255, 16, "magenta f" + f);
                Pixel(frames[f], W, 150, 70, 240, 240, 240, 248, 255, 10, "white f" + f);
            }
            var probe = Tool(ffprobe, "-v", "error", "-show_entries", "stream=codec_type,codec_name", "-of", "csv=p=0", c.FilePath).Trim();
            True(probe == "vp8,video", "Unexpected streams: " + probe);
            notes.Add("WebM VP8 alpha frames=" + c.Frames + " alpha=" + c.MinimumAlpha + ".." + c.MaximumAlpha);
        }
        Clean(temp);
    }

    private static void PreviewTimeAndOrientation()
    {
        string temp = TempDir("preview"), source = Black();
        foreach (var (time, frame) in new[] { (0.0, 0), (0.5, 12), (0.52, 12), (0.499, 11), (1.97, 47), (99.0, 47) })
        {
            var request = Request(source, temp, BackgroundJobKind.Preview);
            request.PreviewTimeSeconds = time; request.PreviewMaximumWidth = 96;
            using (var job = BackgroundVideoProcessor.Start(request))
            {
                var p = Await(job).Preview;
                True(p.Width == 96 && p.Height == 54 && p.OriginalRgba.Length == 96 * 54 * 4 && p.ResultRgba.Length == p.OriginalRgba.Length, "Wrong preview size.");
                True(Math.Abs(p.TimeSeconds - frame / 24.0) < 1e-6, "Preview time " + p.TimeSeconds + " for request " + time);
                int m = Marker(frame);
                // Nearest marker level identifies the frame (levels are 4 apart; codec error is smaller).
                Pixel(p.OriginalRgba, 96, 79, 39, m, m, m, 255, 255, 2, "marker of frame " + frame + " at t=" + time);
                // Top-down rows: green bar near the TOP, black at the mirrored bottom location.
                Pixel(p.OriginalRgba, 96, 48, 4, 40, 200, 60, 255, 255, 12, "top bar original");
                Pixel(p.OriginalRgba, 96, 48, 49, 0, 0, 0, 255, 255, 4, "bottom original");
                Pixel(p.ResultRgba, 96, 48, 4, 40, 200, 60, 255, 255, 12, "top bar result");
                Pixel(p.ResultRgba, 96, 48, 49, 0, 0, 0, 0, 0, 0, "bottom result");
            }
        }
        var full = Request(source, temp, BackgroundJobKind.Preview); full.PreviewMaximumWidth = 4096;
        using (var job = BackgroundVideoProcessor.Start(full)) { var p = Await(job).Preview; True(p.Width == W && p.Height == H, "Preview must not upscale."); }
        // Full-size seek previews must equal an independent sequential decode of that frame number, byte for byte.
        foreach (var (time, frame) in new[] { (0.0, 0), (0.5, 12), (1.97, 47) })
        {
            var exact = Request(source, temp, BackgroundJobKind.Preview); exact.PreviewTimeSeconds = time; exact.PreviewMaximumWidth = W;
            using (var job = BackgroundVideoProcessor.Start(exact))
            {
                var p = Await(job).Preview;
                var reference = ToolBytes(ffmpeg, "-hide_banner", "-v", "error", "-nostdin", "-i", source, "-vf",
                    "select=eq(n\\," + frame + "),scale=in_color_matrix=auto:out_range=full,format=rgba,setsar=1",
                    "-fps_mode", "passthrough", "-frames:v", "1", "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1");
                True(reference.Length == p.OriginalRgba.Length && reference.SequenceEqual(p.OriginalRgba), "Preview at t=" + time + " is not exactly frame " + frame + ".");
            }
        }
        notes.Add("t→frame 0→0, .5→12, .52→12, .499→11, 1.97→47, 99→47 (clamped); 96x54 top-down; full-size seek frames 0/12/47 byte-identical to sequential decode");
        Clean(temp);
    }

    private static void PreviewProtectedRegion()
    {
        string temp = TempDir("protected"), source = Black();
        byte[] Run(BackgroundOptions options)
        {
            var request = Request(source, temp, BackgroundJobKind.Preview, options); request.PreviewMaximumWidth = 96;
            using (var job = BackgroundVideoProcessor.Start(request)) return Await(job).Preview.ResultRgba;
        }
        var plain = Run(new BackgroundOptions());
        var auto = new BackgroundOptions { ProtectedRegions = new[] { new BackgroundKeepRegion(0, 0, .25f, .25f) } };
        var kept = Run(auto);
        var manual = Manual(0, 0, 0); manual.ProtectedRegions = auto.ProtectedRegions;
        var keptManual = Run(manual);
        Pixel(plain, 96, 5, 5, null, null, null, 0, 0, 0, "unprotected corner removed");
        Pixel(kept, 96, 5, 5, 0, 0, 0, 255, 255, 4, "protected corner kept");
        Pixel(keptManual, 96, 5, 5, 0, 0, 0, 255, 255, 4, "protected corner kept (manual)");
        Pixel(kept, 96, 60, 50, null, null, null, 0, 0, 0, "outside protected region still removed");
        notes.Add("protected top-left 25% alpha 0→255; outside stays 0");
        Clean(temp);
    }

    // Soft red ramp over black: partial alpha must decode as STRAIGHT (unpremultiplied) RGB and match the
    // production matte session run on independently decoded source frames.
    private static void SoftScene(int f, byte[] rgb, int w, int h)
    {
        Array.Clear(rgb, 0, rgb.Length);
        for (int y = 20; y < 88; y++) for (int x = 40; x < 152; x++)
        {
            double k = Math.Min(1, (x - 40 + f) / 80.0), lin = k * k; // varied coverage
            int p = (y * w + x) * 3;
            rgb[p] = (byte)Math.Round(220 * Math.Pow(lin, 1 / 2.2)); rgb[p + 1] = (byte)Math.Round(40 * Math.Pow(lin, 1 / 2.2)); rgb[p + 2] = rgb[p + 1];
        }
    }
    private static void StraightSoftEdges(VideoConversionFormat format)
    {
        string temp = TempDir("straight-" + format), source = MakeVideo("soft.mp4", W, H, 24, "24", SoftScene);
        var options = Manual(0, 0, 0); options.Threshold = .02f; options.Softness = .7f; options.TemporalSmoothing = 0; options.BorderConnected = false;
        var request = Request(source, temp, BackgroundJobKind.Convert, options); request.Format = format;
        using (var job = BackgroundVideoProcessor.Start(request))
        {
            var r = Await(job);
            var output = DecodeOutput(r.Conversion.FilePath, W, H, format);
            var raw = ToolBytes(ffmpeg, "-hide_banner", "-v", "error", "-nostdin", "-i", source, "-vf", "scale=in_color_matrix=auto:out_range=full,format=rgba,setsar=1",
                "-fps_mode", "passthrough", "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1");
            True(raw.Length == W * H * 4 * 24 && output.Count == 24, "Frame count mismatch.");
            var session = new BackgroundMatteSession(W, H, options);
            int worstAlpha = 0, worstColor = 0, partial = 0, premultipliedGap = 0;
            var input = new byte[W * H * 4]; var expected = new byte[input.Length];
            for (int f = 0; f < 24; f++)
            {
                Buffer.BlockCopy(raw, f * input.Length, input, 0, input.Length);
                session.Process(input, expected);
                for (int i = 0; i < W * H; i++)
                {
                    int p = i * 4, x = i % W, y = i / W;
                    worstAlpha = Math.Max(worstAlpha, Math.Abs(expected[p + 3] - output[f][p + 3]));
                    // Interior of the ramp, away from 4:2:0 chroma edges.
                    if (x < 44 || x > 148 || y < 24 || y > 84) continue;
                    int a = expected[p + 3];
                    if (a < 64 || a > 192) continue;
                    partial++;
                    worstColor = Math.Max(worstColor, Math.Abs(expected[p] - output[f][p]));
                    premultipliedGap = Math.Max(premultipliedGap, expected[p] - expected[p] * a / 255);
                }
            }
            True(partial > 1000, "Too few partial-alpha pixels: " + partial);
            True(worstAlpha <= 16 && worstColor <= 16, "Alpha error " + worstAlpha + ", straight red error " + worstColor);
            True(premultipliedGap >= 40, "Test cannot distinguish straight from premultiplied RGB.");
            notes.Add(format + ": " + partial + " partial-alpha pixels; max alpha error " + worstAlpha + ", straight red error " + worstColor + " (premultiplied would differ by up to " + premultipliedGap + ")");
        }
        Clean(temp);
    }

    private static void LowConfidence()
    {
        string temp = TempDir("low-confidence"), source = MakeVideo("mixed.mp4", W, H, N, "24", MixedScene);
        using (var job = BackgroundVideoProcessor.Start(Request(source, temp, BackgroundJobKind.Analyze)))
        {
            var r = Await(job);
            True(!r.Analysis.Reliable && r.Warning == r.Analysis.Message, "Mixed border must be unreliable with a warning.");
            notes.Add("analysis confidence=" + r.Analysis.Confidence.ToString("0.000", CultureInfo.InvariantCulture));
        }
        foreach (var kind in new[] { BackgroundJobKind.Preview, BackgroundJobKind.Convert })
            using (var job = BackgroundVideoProcessor.Start(Request(source, temp, kind)))
            {
                string message = Fails(job);
                True(message.Contains("uncertain") && message.Contains("manually"), kind + " message: " + message);
                notes.Add(kind + " auto stopped: " + message);
            }
        Clean(temp);
        var preview = Request(source, temp, BackgroundJobKind.Preview, Manual(200, 20, 20)); preview.PreviewMaximumWidth = W;
        using (var job = BackgroundVideoProcessor.Start(preview))
        {
            var r = Await(job);
            True(r.Analysis == null, "Manual preview must not analyze.");
            Pixel(r.Preview.ResultRgba, W, 5, 50, null, null, null, 0, 0, 0, "manual red removed");
            Pixel(r.Preview.ResultRgba, W, 185, 50, 20, 20, 200, 255, 255, 6, "blue kept");
            Pixel(r.Preview.ResultRgba, W, 96, 54, 30, 200, 30, 255, 255, 12, "foreground kept");
        }
        using (var job = BackgroundVideoProcessor.Start(Request(source, temp, BackgroundJobKind.Convert, Manual(200, 20, 20))))
        {
            var r = Await(job);
            var frames = DecodeOutput(r.Conversion.FilePath, W, H, VideoConversionFormat.PackedH264);
            Pixel(frames[10], W, 5, 50, null, null, null, 0, 6, 0, "manual convert red removed");
            Pixel(frames[10], W, 185, 50, 20, 20, 200, 248, 255, 14, "manual convert blue kept");
            notes.Add("manual preview and packed convert proceed");
        }
        Clean(temp);
    }

    private static void TimingNtscOffsetAudio()
    {
        string temp = TempDir("timing");
        const int frames = 60;
        var tail = new List<string> { "-f", "lavfi", "-i", "sine=f=440:d=2.5" };
        tail.AddRange(H264); tail.AddRange(new[] { "-map", "1:a", "-c:a", "aac", "-output_ts_offset", "1.25" });
        string source = MakeVideo("ntsc-offset-audio.mp4", W, H, frames, "30000/1001", BlackScene, tail);
        var start = Tool(ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=start_time", "-of", "csv=p=0", source).Trim().TrimEnd(',');
        True(double.Parse(start, CultureInfo.InvariantCulture) > 1.2, "Fixture start time not offset: " + start);
        using (var job = BackgroundVideoProcessor.Start(new BackgroundRequest { SourcePath = source, TemporaryDirectory = temp, Kind = BackgroundJobKind.Convert, Format = VideoConversionFormat.AlphaWebM }))
        {
            var r = Await(job);
            True(Math.Abs(r.FramesPerSecond - 30000 / 1001.0) < 1e-9 && Math.Abs(r.DurationSeconds - frames * 1001 / 30000.0) < 1e-9, "Wrong NTSC metadata " + r.FramesPerSecond + " " + r.DurationSeconds);
            True(r.Conversion.Frames == frames, "Wrong frame count.");
            var times = FrameTimes(r.Conversion.FilePath);
            True(times.Count == frames && Math.Abs(times[0]) < 1e-9, "Output must start at zero with all frames.");
            double worst = 0;
            for (int f = 0; f < frames; f++) worst = Math.Max(worst, Math.Abs(times[f] - f * 1001 / 30000.0));
            True(worst <= .0011, "Timing error " + worst);
            var streams = Tool(ffprobe, "-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", r.Conversion.FilePath).Trim();
            True(streams == "video", "Audio must be dropped: " + streams);
            var decoded = DecodeOutput(r.Conversion.FilePath, W, H, VideoConversionFormat.AlphaWebM);
            for (int f = 0; f < frames; f++) Pixel(decoded[f], W, 158, 78, Marker(f % 48), Marker(f % 48), Marker(f % 48), 248, 255, 6, "marker order f" + f);
            notes.Add("29.97fps start=" + start + "s +audio → " + frames + " frames, start 0, worst " + (worst * 1000).ToString("0.###", CultureInfo.InvariantCulture) + "ms (WebM ms clock), audio dropped");
        }
        Clean(temp);
    }

    private static string MakeVfr()
    {
        var tail = new List<string>(H264);
        int vf = tail.IndexOf("-vf"); tail[vf + 1] = "settb=1/48000,setpts='if(lt(N,20),N,N+0.5)/24/TB'," + tail[vf + 1];
        tail.AddRange(new[] { "-fps_mode", "passthrough", "-enc_time_base:v", "1/48000", "-video_track_timescale", "48000" });
        string path = MakeVideo("vfr2.mp4", W, H, N, "24", BlackScene, tail);
        var stamps = Tool(ffprobe, "-v", "error", "-select_streams", "0", "-show_entries", "packet=pts", "-of", "csv=p=0", path)
            .Split('\n').Where(l => l.Trim().Length > 0).Select(l => long.Parse(l.Trim().TrimEnd(','), CultureInfo.InvariantCulture)).OrderBy(x => x).ToList();
        True(stamps.Count == N && stamps[20] - stamps[19] != stamps[2] - stamps[1], "VFR fixture is not variable.");
        return path;
    }
    private static string MakeOdd()
    {
        var tail = new List<string>(H264);
        int vf = tail.IndexOf("-vf"); tail[vf + 1] = tail[vf + 1].Replace("yuv420p", "yuv444p");
        return MakeVideo("odd.mp4", W - 1, H, 8, "24", BlackScene, tail);
    }
    private static string MakeHdr()
    {
        var tail = new List<string> { "-map", "0:v", "-vf", "scale=out_color_matrix=bt2020:out_range=limited,format=yuv420p10le,setparams=range=limited:color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc",
            "-c:v", "libx265", "-x265-params", "log-level=error", "-tag:v", "hvc1", "-color_range", "tv", "-colorspace", "bt2020nc", "-color_primaries", "bt2020", "-color_trc", "smpte2084" };
        return MakeVideo("hdr.mp4", W, H, 8, "24", BlackScene, tail);
    }
    private static string Derived(string name, params string[] args)
    {
        string path = Path.Combine(fixtures, name);
        if (!File.Exists(path)) { var all = new List<string> { "-hide_banner", "-v", "error", "-nostdin" }; all.AddRange(args); all.Add("-n"); all.Add(path); Tool(ffmpeg, all.ToArray()); }
        return path;
    }
    private static string MakeRotated() { return Derived("rotated.mp4", "-display_rotation", "90", "-i", Black(), "-map", "0:v", "-c", "copy"); }
    private static string MakeTwoVideo() { return Derived("two-video.mkv", "-i", Black(), "-map", "0:v", "-map", "0:v", "-c", "copy"); }

    private static void Reject(string name, string source, string expected)
    {
        string temp = TempDir("reject-" + name);
        foreach (var kind in new[] { BackgroundJobKind.Analyze, BackgroundJobKind.Convert })
            using (var job = BackgroundVideoProcessor.Start(Request(source, temp, kind, Manual(0, 0, 0))))
            {
                string message = Fails(job);
                True(message.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0, kind + " message: " + message);
                if (kind == BackgroundJobKind.Analyze) notes.Add(message);
            }
        Clean(temp);
    }

    private static void RejectTruncated()
    {
        string temp = TempDir("truncated");
        string whole = MakeVideo("faststart.mp4", W, H, N, "24", BlackScene, H264.Concat(new[] { "-movflags", "+faststart" }));
        string cut = Path.Combine(fixtures, "truncated.mp4");
        if (!File.Exists(cut)) { var bytes = File.ReadAllBytes(whole); File.WriteAllBytes(cut, bytes.Take(bytes.Length * 55 / 100).ToArray()); }
        using (var job = BackgroundVideoProcessor.Start(Request(cut, temp, BackgroundJobKind.Convert, Manual(0, 0, 0))))
            notes.Add(Fails(job));
        Clean(temp);
    }

    private static void RejectOpaque()
    {
        string temp = TempDir("opaque");
        var options = Manual(255, 0, 255); options.Threshold = .01f; options.Softness = .01f;
        using (var job = BackgroundVideoProcessor.Start(Request(Black(), temp, BackgroundJobKind.Convert, options)))
        {
            string message = Fails(job);
            True(message.Contains("No background was removed"), message);
            notes.Add(message);
        }
        Clean(temp);
    }

    private static void WarnTransparent()
    {
        string temp = TempDir("transparent");
        string source = MakeVideo("all-black.mp4", 64, 64, 12, "24", (f, rgb, w, h) => Array.Clear(rgb, 0, rgb.Length));
        using (var job = BackgroundVideoProcessor.Start(Request(source, temp, BackgroundJobKind.Convert)))
        {
            var r = Await(job);
            True(r.Conversion != null && r.Conversion.MaximumAlpha == 0 && r.Warning != null && r.Warning.Contains("transparent"), "Expected transparent warning, got " + r.Warning);
            notes.Add(r.Warning);
        }
        Clean(temp);
    }

    private static string Large() { return MakeVideo("large.mp4", 1280, 720, 240, "24", BlackScene); }

    private static void WaitFor(IBackgroundJob job, string prefix)
    {
        var watch = Stopwatch.StartNew();
        while (!job.Status.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (job.Completion.IsCompleted) throw new Exception("Finished before stage '" + prefix + "': " + job.Status);
            if (watch.Elapsed.TotalSeconds > 120) throw new Exception("Never reached '" + prefix + "': " + job.Status);
            Thread.Sleep(2);
        }
    }

    private static void CancelDuringFrames()
    {
        string temp = TempDir("cancel"), source = Large(), hash = Hash(source);
        var job = BackgroundVideoProcessor.Start(Request(source, temp, BackgroundJobKind.Convert));
        WaitFor(job, "Removing background: frame");
        bool locked = false;
        try { using (new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) { } } catch (IOException) { locked = true; }
        True(locked, "Source must be locked against writers during processing.");
        var watch = Stopwatch.StartNew();
        job.Cancel();
        long cancelMs = watch.ElapsedMilliseconds;
        True(cancelMs < 100, "Cancel blocked caller " + cancelMs + "ms.");
        try { job.Completion.Wait(TimeSpan.FromSeconds(30)); } catch (AggregateException) { }
        True(job.Completion.IsCanceled, "Completion must be Canceled, was " + job.Completion.Status);
        long stopMs = watch.ElapsedMilliseconds;
        job.Dispose();
        Clean(temp);
        True(Hash(source) == hash, "Source changed.");
        using (new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        notes.Add("source locked=" + locked + "; Cancel() " + cancelMs + "ms; Canceled after " + stopMs + "ms; temp clean; source unlocked afterwards; status '" + job.Status + "'");
    }

    private static void DisposeDuringFinalEncode()
    {
        string temp = TempDir("dispose-final"), source = Large();
        var job = BackgroundVideoProcessor.Start(Request(source, temp, BackgroundJobKind.Convert, Manual(0, 0, 0)));
        WaitFor(job, "Encoding final video");
        var watch = Stopwatch.StartNew();
        job.Dispose();
        long ms = watch.ElapsedMilliseconds;
        True(ms < 100, "Dispose blocked caller " + ms + "ms.");
        try { job.Completion.Wait(TimeSpan.FromSeconds(60)); } catch (AggregateException) { }
        True(job.Completion.IsCanceled, "Completion must be Canceled, was " + job.Completion.Status);
        Clean(temp);
        notes.Add("Dispose() " + ms + "ms during converter stage; Canceled; temp clean");
    }

    private static void DisposeAfterSuccessAndConcurrent()
    {
        string temp = TempDir("concurrent"), source = Black();
        var first = BackgroundVideoProcessor.Start(Request(source, temp, BackgroundJobKind.Convert));
        var second = BackgroundVideoProcessor.Start(Request(source, temp, BackgroundJobKind.Convert));
        var a = Await(first).Conversion; var b = Await(second).Conversion;
        True(a.FilePath != b.FilePath && File.Exists(a.FilePath) && File.Exists(b.FilePath), "Concurrent jobs must have unique, live outputs.");
        string copy = Path.Combine(root, "copied-result.mp4");
        if (File.Exists(copy)) File.Delete(copy);
        File.Copy(a.FilePath, copy, false);
        first.Dispose();
        True(!File.Exists(a.FilePath) && File.Exists(b.FilePath), "Disposing one job must delete only its own result.");
        second.Dispose();
        Clean(temp);
        True(File.Exists(copy), "Caller copy must survive.");
        notes.Add("unique outputs; result alive until Dispose; dispose deletes only own files; sentinel untouched");
    }

    private static void BoundedMemory()
    {
        string temp = TempDir("memory"), source = Large();
        var self = Process.GetCurrentProcess();
        GC.Collect(); self.Refresh();
        long before = self.WorkingSet64, peak = before;
        using (var job = BackgroundVideoProcessor.Start(Request(source, temp, BackgroundJobKind.Convert)))
        {
            while (!job.Completion.IsCompleted) { self.Refresh(); peak = Math.Max(peak, self.WorkingSet64); Thread.Sleep(20); }
            var r = Await(job);
            long raw = 1280L * 720 * 4 * 240;
            True(r.Conversion.Frames == 240 && r.Conversion.Width == 1280, "Large conversion failed.");
            True(peak - before < raw / 4, "Working set grew by " + (peak - before) / 1048576 + " MiB for a " + raw / 1048576 + " MiB raw movie.");
            notes.Add("1280x720x240 (" + raw / 1048576 + " MiB raw RGBA): harness working set +" + (peak - before) / 1048576 + " MiB peak; " + r.Conversion.Format + " frames=" + r.Conversion.Frames);
        }
        Clean(temp);
    }

    private static void UnicodePaths()
    {
        string dir = Path.Combine(root, "한글 경로 & %PATH% ;'^!(1) $x #");
        Directory.CreateDirectory(dir);
        string source = MakeVideo("배경 제거 & ^ %CD% [1] 'q'.mp4", W, H, N, "24", BlackScene, null, dir);
        string temp = Path.Combine(dir, "임시 작업 $HOME & %TEMP%");
        Directory.CreateDirectory(temp); File.WriteAllText(Path.Combine(temp, "sentinel.txt"), "keep");
        using (var job = BackgroundVideoProcessor.Start(Request(source, temp, BackgroundJobKind.Convert)))
        {
            var r = Await(job);
            True(r.Conversion.Frames == N && r.Conversion.FilePath.StartsWith(temp), "Unicode conversion failed.");
            var preview = Request(source, temp, BackgroundJobKind.Preview); preview.PreviewTimeSeconds = 1;
            using (var p = BackgroundVideoProcessor.Start(preview)) Pixel(Await(p).Preview.OriginalRgba, 192, 158, 78, Marker(24), Marker(24), Marker(24), 255, 255, 1, "unicode preview");
            notes.Add("source=" + Path.GetFileName(source) + " → " + Path.GetFileName(r.Conversion.FilePath));
        }
        Clean(temp);
    }

    private static void ArgumentValidation()
    {
        string temp = TempDir("arguments"), source = Black();
        void Throws<T>(Action action, string what) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception(what + " did not throw " + typeof(T).Name); }
        Throws<ArgumentNullException>(() => BackgroundVideoProcessor.Start(null), "null request");
        Throws<FileNotFoundException>(() => BackgroundVideoProcessor.Start(Request(source + ".missing", temp, BackgroundJobKind.Analyze)), "missing source");
        Throws<DirectoryNotFoundException>(() => BackgroundVideoProcessor.Start(Request(source, temp + "-missing", BackgroundJobKind.Analyze)), "missing temp");
        var bad = new BackgroundOptions { Threshold = 2 };
        Throws<ArgumentException>(() => BackgroundVideoProcessor.Start(Request(source, temp, BackgroundJobKind.Analyze, bad)), "bad options");
        var preview = Request(source, temp, BackgroundJobKind.Preview); preview.PreviewTimeSeconds = double.NaN;
        Throws<ArgumentOutOfRangeException>(() => BackgroundVideoProcessor.Start(preview), "NaN preview time");
        var tool = Request(source, temp, BackgroundJobKind.Analyze); tool.Ffmpeg = "ffmpeg -y";
        using (var job = BackgroundVideoProcessor.Start(tool))
        {
            string message = Fails(job);
            // The OS reason only: the framework's start error would also name the working directory.
            True(message.Contains("Could not start video tool 'ffmpeg -y'") && !message.Contains(Environment.CurrentDirectory, StringComparison.OrdinalIgnoreCase) &&
                !message.Contains("working directory", StringComparison.OrdinalIgnoreCase), "Tool start failure must not expose the working directory: " + Scrub(message));
            notes.Add("arguments in tool name are not split: " + message.Split('\n')[0]);
        }
        var converter = VideoConverter.Start(source, temp, "ffmpeg -y", ffprobe);
        try { converter.Completion.Wait(TimeSpan.FromMinutes(1)); } catch (AggregateException) { }
        string converterMessage = converter.Completion.Exception?.InnerException?.Message ?? "";
        converter.Dispose();
        True(converterMessage.Contains("Could not start video tool") && !converterMessage.Contains(Environment.CurrentDirectory, StringComparison.OrdinalIgnoreCase),
            "Converter start failure must not expose the working directory: " + Scrub(converterMessage));
        // Options are copied at Start: mutating afterwards cannot change a running job.
        var options = Manual(0, 0, 0);
        var request = Request(source, temp, BackgroundJobKind.Preview, options); request.PreviewMaximumWidth = 96;
        using (var job = BackgroundVideoProcessor.Start(request))
        {
            options.Red = 255; options.Green = 0; options.Blue = 255;
            Pixel(Await(job).Preview.ResultRgba, 96, 48, 49, null, null, null, 0, 0, 0, "copied options");
        }
        Clean(temp);
    }

    // ---------- audit additions ----------
    private static List<long> PacketTicks(string path, out string timeBase)
    {
        timeBase = Tool(ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=time_base", "-of", "csv=p=0", path).Trim().TrimEnd(',');
        return Tool(ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts", "-of", "csv=p=0", path).Split('\n')
            .Select(l => l.Trim().TrimEnd(',')).Where(l => l.Length > 0).Select(l => long.Parse(l, CultureInfo.InvariantCulture)).OrderBy(x => x).ToList();
    }

    // Final packed output must keep the source's exact constant-rate clock, not a millisecond grid.
    private static void ExactSourceClock()
    {
        string temp = TempDir("exact-clock");
        var tail = new List<string>(H264) { "-video_track_timescale", "60000" };
        var sources = new[] { (MakeVideo("sixty.mp4", W, H, 60, "60", BlackScene, tail), 60, 1000L, 60000L),
            (MakeVideo("ntsc-offset-audio.mp4", W, H, 60, "30000/1001", BlackScene, NtscTail()), 60, 1001L, 30000L) };
        foreach (var (source, frames, step, scale) in sources)
        {
            using (var job = BackgroundVideoProcessor.Start(Request(source, temp, BackgroundJobKind.Convert, Manual(0, 0, 0))))
            {
                var r = Await(job);
                string timeBase; var ticks = PacketTicks(r.Conversion.FilePath, out timeBase);
                True(r.Conversion.Frames == frames && ticks.Count == frames, Path.GetFileName(source) + ": frame count " + ticks.Count);
                True(timeBase == "1/" + scale, Path.GetFileName(source) + ": output time base " + timeBase);
                for (int i = 0; i < frames; i++)
                    True(ticks[i] - ticks[0] == i * step, Path.GetFileName(source) + ": frame " + i + " at tick " + (ticks[i] - ticks[0]) + ", expected " + i * step);
                notes.Add(Path.GetFileName(source) + " → time base " + timeBase + ", all " + frames + " frames exactly " + step + " ticks apart");
            }
        }
        Clean(temp);
    }

    private static List<string> NtscTail()
    {
        var tail = new List<string> { "-f", "lavfi", "-i", "sine=f=440:d=2.5" };
        tail.AddRange(H264); tail.AddRange(new[] { "-map", "1:a", "-c:a", "aac", "-output_ts_offset", "1.25" });
        return tail;
    }

    // Open GOP: a seek starts at a keyframe whose leading pictures reference the previous GOP.
    private static void OpenGopSeek()
    {
        string temp = TempDir("open-gop");
        // Busy motion makes x264 place B-frames before each open-GOP keyframe in display order.
        var args = new List<string> { "-f", "lavfi", "-i", "testsrc2=size=" + W + "x" + H + ":rate=24", "-frames:v", "96" };
        args.AddRange(H264);
        int crf = args.IndexOf("-crf"); args.InsertRange(crf, new[] { "-bf", "3", "-x264-params", "open-gop=1:keyint=24:min-keyint=24:scenecut=0" });
        string source = Derived("open-gop.mp4", args.ToArray());
        // Precondition: the old strict seek (error-level diagnostics fatal) fails on this fixture.
        var strict = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-v", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-ss", "1.479166667", "-i", source,
            "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1" }) strict.ArgumentList.Add(a);
        string strictError;
        using (var p = Process.Start(strict)) { var e = p.StandardError.ReadToEndAsync(); p.StandardOutput.BaseStream.CopyTo(Stream.Null); p.WaitForExit(); strictError = e.Result; }
        True(strictError.Trim().Length > 0, "Fixture does not reproduce leading-picture decode errors after a seek.");
        // Analysis seeks to five mid-GOP frames; the test pattern has no uniform border, so only completion matters.
        using (var job = BackgroundVideoProcessor.Start(Request(source, temp, BackgroundJobKind.Analyze)))
            True(Await(job).Analysis != null, "Open-GOP analysis returned no result.");
        foreach (int frame in new[] { 23, 35, 47, 62, 95 })
        {
            var request = Request(source, temp, BackgroundJobKind.Preview, Manual(0, 0, 0));
            request.PreviewTimeSeconds = frame / 24.0; request.PreviewMaximumWidth = W;
            using (var job = BackgroundVideoProcessor.Start(request))
            {
                var preview = Await(job).Preview;
                var reference = ToolBytes(ffmpeg, "-hide_banner", "-v", "error", "-nostdin", "-i", source, "-vf",
                    "select=eq(n\\," + frame + "),scale=in_color_matrix=auto:out_range=full,format=rgba,setsar=1",
                    "-fps_mode", "passthrough", "-frames:v", "1", "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1");
                True(reference.SequenceEqual(preview.OriginalRgba), "Open-GOP preview of frame " + frame + " differs from the sequential decode.");
            }
        }
        notes.Add("a strict seek into this stream errors (" + strictError.Split('\n')[0].Trim() + "); the keyframe seek falls back to an exact sequential decode: five-frame analysis completes, previews of frames 23/35/47/62/95 byte-identical to sequential decode");
        Clean(temp);
    }

    // Existing transparency must never be replaced: real alpha is rejected with a redirect, while an
    // alpha channel that is opaque everywhere is ordinary opaque video.
    private static void AlphaSources()
    {
        string temp = TempDir("alpha-sources");
        string prores = Derived("alpha-prores.mov", "-f", "lavfi", "-i", "testsrc2=size=" + W + "x" + H + ":rate=24", "-frames:v", "24",
            "-vf", "format=rgba,colorchannelmixer=aa=0.5", "-c:v", "prores_ks", "-profile:v", "4444", "-pix_fmt", "yuva444p10le");
        string webm = Derived("alpha-vp8.webm", "-f", "lavfi", "-i", "testsrc2=size=" + W + "x" + H + ":rate=24", "-frames:v", "24",
            "-vf", "format=rgba,colorchannelmixer=aa=0.5", "-c:v", "libvpx", "-pix_fmt", "yuva420p", "-auto-alt-ref", "0", "-b:v", "2M");
        foreach (var source in new[] { prores, webm })
            foreach (var kind in new[] { BackgroundJobKind.Analyze, BackgroundJobKind.Preview, BackgroundJobKind.Convert })
                using (var job = BackgroundVideoProcessor.Start(Request(source, temp, kind, kind == BackgroundJobKind.Analyze ? null : Manual(0, 0, 0))))
                {
                    string message = Fails(job);
                    True(message.Contains("already contains transparent pixels") && message.Contains("영상 이펙트 만들기"), Path.GetFileName(source) + " " + kind + ": " + message);
                    if (kind == BackgroundJobKind.Convert && source == prores) notes.Add(message);
                }
        Clean(temp);
        var opaqueTail = new List<string> { "-map", "0:v", "-c:v", "prores_ks", "-profile:v", "4444", "-pix_fmt", "yuva444p10le" };
        string opaque = MakeVideo("opaque-alpha-channel.mov", W, H, N, "24", BlackScene, opaqueTail);
        using (var job = BackgroundVideoProcessor.Start(Request(opaque, temp, BackgroundJobKind.Convert, Manual(0, 0, 0))))
        {
            var r = Await(job);
            True(r.Conversion.Frames == N && r.Conversion.MinimumAlpha <= 5, "Opaque alpha-channel source must convert like opaque video.");
        }
        notes.Add("50% alpha ProRes 4444 and VP8 alpha WebM rejected in Analyze/Preview/Convert with redirect; ProRes 4444 whose alpha is opaque converts normally");
        Clean(temp);
    }

    private static void ParallelMatteEqualsReference()
    {
        int compared = 0;
        var cases = new (string, int, int, int, Action<int, byte[], int, int>, BackgroundOptions)[]
        {
            ("black.mp4", W, H, N, BlackScene, Manual(0, 0, 0)),
            ("soft.mp4", W, H, 24, SoftScene, new BackgroundOptions { AutoDetect = false, Threshold = .02f, Softness = .7f, TemporalSmoothing = .9f }),
            ("soft.mp4", W, H, 24, SoftScene, new BackgroundOptions { AutoDetect = false, Threshold = .02f, Softness = .7f, TemporalSmoothing = 0, BorderConnected = false }),
            ("green.mp4", W, H, N, GreenScene, new BackgroundOptions { AutoDetect = false, Red = 0, Green = 177, Blue = 64, Softness = .2f,
                ProtectedRegions = new[] { new BackgroundKeepRegion(.1f, .2f, .3f, .4f), new BackgroundKeepRegion(.75f, 0, .25f, 1) } }),
            ("large.mp4", 1280, 720, 240, BlackScene, new BackgroundOptions { AutoDetect = false, TemporalSmoothing = .5f }),
        };
        foreach (var (file, w, h, frames, scene, options) in cases)
        {
            string source = MakeVideo(file, w, h, frames, "24", scene);
            var raw = ToolBytes(ffmpeg, "-hide_banner", "-v", "error", "-nostdin", "-i", source, "-frames:v", "24", "-vf",
                "scale=in_color_matrix=auto:out_range=full,format=rgba,setsar=1", "-fps_mode", "passthrough", "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1");
            int size = w * h * 4, count = raw.Length / size;
            var session = new BackgroundMatteSession(w, h, options); var reference = new ReferenceMatte(w, h, options);
            var input = new byte[size]; var actual = new byte[size]; var expected = new byte[size];
            for (int f = 0; f < count; f++)
            {
                Buffer.BlockCopy(raw, f * size, input, 0, size);
                session.Process(input, actual); reference.Process(input, expected);
                if (!actual.AsSpan().SequenceEqual(expected))
                {
                    int at = 0; while (actual[at] == expected[at]) at++;
                    throw new Exception(file + " frame " + f + ": byte " + at + " is " + actual[at] + ", sequential reference " + expected[at]);
                }
                compared++;
            }
        }
        notes.Add(compared + " frames across 5 option sets (border/no border, smoothing 0/.15/.5/.9, protected regions, 1280x720) byte-identical on " + Environment.ProcessorCount + " logical processors");
    }

    private static void FrameHashMatchesFfmpeg()
    {
        // Provenance: SMHasher's VerificationTest for the public-domain MurmurHash3_x64_128 reference.
        // Keys {}, {0}, {0,1}, ... {0..254} hashed with seeds 256..1; their hashes hashed with seed 0;
        // the first four result bytes (little-endian) must be 0x6384BA69.
        var key = new byte[256]; var all = new byte[16 * 256];
        for (int i = 0; i < 256; i++)
        {
            key[i] = (byte)i;
            Buffer.BlockCopy(BackgroundFrameHash.Compute(key, 0, i, (ulong)(256 - i)), 0, all, i * 16, 16);
        }
        var final = BackgroundFrameHash.Compute(all, 0, all.Length, 0);
        uint verification = (uint)(final[0] | final[1] << 8 | final[2] << 16 | final[3] << 24);
        True(verification == 0x6384BA69u, "SMHasher verification value " + verification.ToString("X8") + ", expected 6384BA69.");
        var random = new Random(20261027);
        var lengths = new[] { 0, 1, 7, 15, 16, 17, 31, 32, 33, 4095, 65536, 192 * 108 * 4, 1920 * 1080 * 4 + 5 };
        string file = Path.Combine(root, "hash-vector.bin");
        foreach (int length in lengths)
        {
            var data = new byte[length + 3]; random.NextBytes(data);
            File.WriteAllBytes(file, data.Skip(3).ToArray());
            string ours = Convert.ToHexString(BackgroundFrameHash.Compute(data, 3, length)).ToLowerInvariant();
            if (length == 0) { True(ours.Length == 32, "Empty hash."); continue; }
            string theirs = Tool(ffmpeg, "-hide_banner", "-v", "error", "-nostdin", "-f", "data", "-i", file, "-map", "0", "-c", "copy", "-f", "hash", "-hash", "murmur3", "pipe:1").Trim();
            True(string.Equals(theirs, "murmur3=" + ours, StringComparison.OrdinalIgnoreCase), "Length " + length + ": C# " + ours + " vs FFmpeg " + theirs);
        }
        File.Delete(file);
        notes.Add("SMHasher MurmurHash3_x64_128 verification value 0x6384BA69 reproduced; with FFmpeg's seed it equals FFmpeg murmur3 for " + (lengths.Length - 1) + " lengths 1.." + lengths.Max() + " bytes at an unaligned offset");
    }

    /// <summary>The pre-audit sequential BackgroundMatteSession, kept verbatim as the byte-exact reference.</summary>
    private sealed class ReferenceMatte
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

        public ReferenceMatte(int width, int height, BackgroundOptions settings)
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
                    if (x > 0) Enqueue(i - 1, ref tail); if (x < width - 1) Enqueue(i + 1, ref tail);
                    if (i >= width) Enqueue(i - width, ref tail); if (i < pixels - width) Enqueue(i + width, ref tail);
                }
                for (int i = 0; i < pixels; i++) if (!reached[i]) alpha[i] = 1;
            }
            for (int i = 0; i < pixels; i++)
            {
                int p = i * 4, history = i * 3;
                float a = alpha[i];
                if (hasPrevious && a > 0 && a < 1 && !protectedPixels[i] &&
                    Math.Abs(rgba[p] - previousRgb[history]) <= 4 && Math.Abs(rgba[p+1] - previousRgb[history+1]) <= 4 && Math.Abs(rgba[p+2] - previousRgb[history+2]) <= 4)
                    a = a * (1 - options.TemporalSmoothing) + previousAlpha[i] * options.TemporalSmoothing;
                previousAlpha[i] = a;
                for (int c = 0; c < 3; c++) previousRgb[history+c] = rgba[p+c];
                output[p+3] = (byte)Math.Round(a * rgba[p+3]);
                if (a <= 0 || output[p+3] == 0) { output[p] = output[p+1] = output[p+2] = 0; continue; }
                if (a >= 1) { output[p] = rgba[p]; output[p+1] = rgba[p+1]; output[p+2] = rgba[p+2]; continue; }
                output[p] = Unmix(rgba[p], options.Red, a);
                output[p+1] = Unmix(rgba[p+1], options.Green, a);
                output[p+2] = Unmix(rgba[p+2], options.Blue, a);
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
        { var result = new float[256]; for (int i=0;i<256;i++) { double s=i/255.0; result[i]=(float)(s<=.04045?s/12.92:Math.Pow((s+.055)/1.055,2.4)); } return result; }
        private static byte[] MakeSrgb()
        { var result=new byte[65536]; for(int i=0;i<result.Length;i++) { double l=i/65535.0; result[i]=(byte)Math.Round(255*(l<=.0031308?l*12.92:1.055*Math.Pow(l,1/2.4)-.055)); } return result; }
    }
}

