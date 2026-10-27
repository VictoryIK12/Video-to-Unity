using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VideoToUnity.Editor
{
    public enum VideoConversionFormat { PackedH264, AlphaWebM }

    /// <summary>
    /// One asynchronous, verified temporary conversion. Copy Result.FilePath to a NEW destination
    /// before disposing. No Unity API, shell, import, permanent destination, or audio processing.
    /// FFmpeg/FFprobe 5.1+ with libx264 and libvpx are required. See Conversion.md for limits.
    /// </summary>
    public sealed class VideoConverter : IDisposable
    {
        public sealed class Result
        {
            public string FilePath;
            public int Width, Height;
            public long Frames;
            public int MinimumAlpha, MaximumAlpha;
            public VideoConversionFormat Format;
        }

        public Task<Result> Completion { get; private set; }
        public string Status { get { return status; } }

        private volatile string status = "Inspecting source video…";
        private int cancelled, disposed;
        private readonly string source, temporaryDirectory, ffmpeg, ffprobe;
        private readonly VideoConversionFormat format;
        private readonly List<string> ownedFiles = new List<string>();
        private string workingDirectory, outputPath;
        private readonly object cleanupLock = new object();
        private const int DiagnosticLimit = 16384;
        // Per-stage FFmpeg thread cap: bounded CPU and memory (packed x264 at 8 threads peaked at 1.4 GB
        // versus 2 GB automatic). Tools also run below normal priority; see Run.
        private static readonly string Threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount)).ToString(CultureInfo.InvariantCulture);
        private const string Alpha8 = "alphaextract,scale=in_range=full:out_range=full,format=gray";
        // Alpha is a coverage VALUE, not light. No gamma/transfer function is applied to it.
        // Explicit full -> limited -> full range mapping is tested against every decoded pixel.
        // Expand gray to neutral RGB first: direct gray->YUV swscale can introduce a ~2-code
        // bias with some builds. The explicit RGB path preserves 0/255 endpoints and gray steps.
        private const string Matte420 = Alpha8 + ",format=rgb24,scale=in_range=full:out_range=limited:out_color_matrix=bt709,format=yuv420p,setsar=1";

        private VideoConverter(string source, string directory, string ffmpeg, string ffprobe, VideoConversionFormat format)
        { this.source = source; temporaryDirectory = directory; this.ffmpeg = ffmpeg; this.ffprobe = ffprobe; this.format = format; }

        public static VideoConverter Start(string source, string temporaryDirectory, string ffmpeg, string ffprobe,
            VideoConversionFormat format = VideoConversionFormat.PackedH264)
        {
            if (format != VideoConversionFormat.PackedH264 && format != VideoConversionFormat.AlphaWebM)
                throw new ArgumentOutOfRangeException(nameof(format));
            source = FullPath(source, "source");
            temporaryDirectory = FullPath(temporaryDirectory, "temporaryDirectory");
            if (!File.Exists(source)) throw new FileNotFoundException("Source video does not exist.", source);
            if (!Directory.Exists(temporaryDirectory)) throw new DirectoryNotFoundException("Temporary directory does not exist: " + temporaryDirectory);
            var job = new VideoConverter(source, temporaryDirectory, Executable(ffmpeg), Executable(ffprobe), format);
            job.Completion = job.ConvertAsync();
            return job;
        }

        private async Task<Result> ConvertAsync()
        {
            // The async boundary converts OperationCanceledException into a genuinely Canceled
            // public task, even when cancellation occurs before the worker starts.
            return await Task.Run(() => Convert()).ConfigureAwait(false);
        }

        public void Cancel()
        {
            if (!Completion.IsCompleted && Interlocked.Exchange(ref cancelled, 1) == 0)
                status = "Cancelling conversion…";
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Cancel();
            // Never wait on the caller/UI thread; children and pipe readers finish before cleanup.
            if (Completion.IsCompleted) Cleanup(false);
            else Completion.ContinueWith(_ => Cleanup(false), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        }

        private void CheckCancelled()
        { if (Volatile.Read(ref cancelled) != 0) throw new OperationCanceledException("Video conversion cancelled."); }

        private Result Convert()
        {
            bool success = false;
            try
            {
                CheckCancelled();
                // Prevent replacement/writes while probing and making references on Windows.
                using (var sourceGuard = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var input = Probe(source);
                    ValidateGeometry(input);
                    CreateWorkingDirectory();
                    string timingPath = Path.Combine(workingDirectory, "source.times");
                    status = "Checking every source frame and timestamp…";
                    using (var times = CreateOwned(timingPath)) ReadTimeline(source, input, times, null);
                    string alphaPath = Path.Combine(workingDirectory, "source.alpha");
                    string colorPath = Path.Combine(workingDirectory, "source.rgb");
                    status = "Decoding real alpha across the complete source…";
                    using (var alpha = CreateOwned(alphaPath))
                    {
                        var extrema = ScanRaw(source, input, Alpha8, "gray", input.Pixels * input.Frames, alpha, null, true);
                        if (extrema.Minimum == 255)
                            throw new InvalidDataException("Source is opaque at 8-bit precision. Select a video with real, representable alpha pixels.");
                    }
                    status = "Decoding full source color reference…";
                    using (var color = CreateOwned(colorPath))
                        ScanRaw(source, input, ColorReference(input), "rgb24", input.Pixels * 3 * input.Frames, color, null, false);
                    Encode(input);
                    status = "Verifying output geometry, frame count and timing…";
                    var output = Probe(outputPath);
                    ValidateColorMetadata(output, input);
                    if (format == VideoConversionFormat.PackedH264) ValidatePackedBitstream(input);
                    int expectedWidth = format == VideoConversionFormat.PackedH264 ? checked(input.width * 2) : input.width;
                    if (output.width != expectedWidth || output.height != input.height || output.sample_aspect_ratio != "1:1" ||
                        output.codec_name != (format == VideoConversionFormat.PackedH264 ? "h264" : "vp8") ||
                        (format == VideoConversionFormat.PackedH264 && (output.pix_fmt != "yuv420p" || output.color_range != "tv")))
                        throw new InvalidDataException("Output codec, geometry, pixel format or range does not match the conversion contract.");
                    using (var times = File.OpenRead(timingPath)) ReadTimeline(outputPath, output, times, input);
                    if (input.Frames != output.Frames) throw new InvalidDataException("Decoded frame count changed during conversion.");

                    status = "Comparing the complete decoded matte to source alpha…";
                    // Packed H.264 has NO alpha plane. Decode the full frame to RGB before
                    // cropping (including chroma interpolation across the seam), then check the
                    // entire right crop's red/gray code values as exposed to a video texture.
                    string matteFilter = format == VideoConversionFormat.PackedH264
                        ? "scale=in_range=limited:out_range=full,format=rgb24," + Crop(input.width, input.height, input.width) + ",extractplanes=r"
                        : Alpha8;
                    Extrema resultAlpha;
                    using (var reference = File.OpenRead(alphaPath))
                        resultAlpha = ScanRaw(outputPath, output, matteFilter, "gray", input.Pixels * input.Frames, null, reference, true, input.Pixels);
                    if (resultAlpha.Minimum == 255) throw new InvalidDataException("Decoded output contains no transparent pixels.");
                    status = "Comparing every decoded color pixel to the source…";
                    // Decode with the output decoder's OWN matrix/range. Forcing our expected
                    // matrix here would conceal a falsely tagged or misinterpreted YUV stream.
                    string colorFilter = format == VideoConversionFormat.PackedH264
                        ? "scale=out_range=full,format=rgb24," + Crop(input.width, input.height, 0)
                        : "scale=out_range=full,format=rgb24";
                    using (var reference = File.OpenRead(colorPath))
                        ScanRaw(outputPath, output, colorFilter, "rgb24", input.Pixels * 3 * input.Frames, null, reference, false, input.Pixels * 3);
                    CheckCancelled();
                    success = true;
                    status = "Complete: all frames, timestamps, color and alpha verified.";
                    return new Result { FilePath = outputPath, Width = input.width, Height = input.height,
                        Frames = output.Frames, MinimumAlpha = resultAlpha.Minimum, MaximumAlpha = resultAlpha.Maximum, Format = format };
                }
            }
            catch (OperationCanceledException) { status = "Video conversion cancelled."; throw; }
            catch (Exception ex)
            {
                if (Volatile.Read(ref cancelled) != 0) { status = "Video conversion cancelled."; CheckCancelled(); }
                status = "Video conversion failed: " + ex.Message;
                throw new InvalidOperationException(status, ex);
            }
            finally { Cleanup(success && Volatile.Read(ref disposed) == 0); }
        }

        private void ValidateGeometry(Video video)
        {
            if (video.width < 2 || video.height < 2 || video.width > 16384 || video.height > 16384)
                throw new InvalidDataException("Video dimensions must be between 2 and 16384 pixels.");
            // Padding changes content UVs and resizing changes the matte. Neither is implicit.
            if ((video.width & 1) != 0 || (video.height & 1) != 0)
                throw new InvalidDataException("An even source width and height are required for 4:2:0 output. Export even dimensions; no pixels are resized or cropped.");
            if (!string.IsNullOrEmpty(video.sample_aspect_ratio) && video.sample_aspect_ratio != "N/A" &&
                video.sample_aspect_ratio != "0:1" && video.sample_aspect_ratio != "1:1")
                throw new InvalidDataException("Non-square source pixels are unsupported. Export square pixels to preserve display geometry.");
            if (video.rotated) throw new InvalidDataException("Rotation/display-transform metadata is unsupported. Bake the transform into the source pixels before conversion.");
            if (!string.IsNullOrEmpty(video.field_order) && video.field_order != "unknown" && video.field_order != "progressive")
                throw new InvalidDataException("Interlaced video is unsupported. Export progressive frames.");
            if (video.color_transfer == "smpte2084" || video.color_transfer == "arib-std-b67" ||
                (!string.IsNullOrEmpty(video.color_primaries) && video.color_primaries != "unknown" && video.color_primaries != "bt709"))
                throw new InvalidDataException("Export SDR Rec.709/sRGB video before conversion; HDR or wide-gamut primaries require a deliberate color transform.");
        }

        private void Encode(Video input)
        {
            CheckCancelled();
            outputPath = Path.Combine(workingDirectory, format == VideoConversionFormat.PackedH264 ? "converted.mp4" : "converted.webm");
            if (File.Exists(outputPath)) throw new IOException("Refusing to overwrite an existing temporary output.");
            // Private per-job directory; FFmpeg -n also refuses collisions. No -y is ever used.
            ownedFiles.Add(outputPath);
            var args = DecodeArguments(source, input);
            args.AddRange(new[] { "-map_metadata", "-1", "-map_chapters", "-1", "-an", "-sn", "-dn", "-filter_threads", Threads, "-filter_complex_threads", Threads });
            string stream = "[0:" + input.index.ToString(CultureInfo.InvariantCulture) + "]";
            if (format == VideoConversionFormat.PackedH264)
            {
                status = "Encoding RGB + matte H.264…";
                args.AddRange(new[] { "-filter_complex", stream + "setpts=PTS-STARTPTS,split=2[c][a];[c]" + Color420(input, false) +
                    "[rgb];[a]" + Matte420 + "[matte];[rgb][matte]hstack=inputs=2," + FrameColor(input) + "[out]", "-map", "[out]",
                    "-c:v", "libx264", "-preset", "medium", "-crf", "4", "-x264-params",
                    "qpmax=12:aq-mode=0:psy=0:colorprim=bt709:transfer=" + Transfer(input) + ":colormatrix=bt709",
                    "-profile:v", "high", "-bf", "0", "-pix_fmt", "yuv420p",
                    "-color_range", "tv", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", Transfer(input),
                    "-movflags", "+faststart+write_colr", "-f", "mp4" });
            }
            else
            {
                status = "Encoding VP8 alpha WebM…";
                args.AddRange(new[] { "-map", "0:" + input.index.ToString(CultureInfo.InvariantCulture), "-vf",
                    "setpts=PTS-STARTPTS," + Color420(input, true),
                    "-c:v", "libvpx", "-pix_fmt", "yuva420p", "-auto-alt-ref", "0", "-crf", "4", "-b:v", "0",
                    "-deadline", "good", "-cpu-used", "2", "-color_range", "tv", "-colorspace", OutputMatrix,
                    "-color_primaries", "bt709", "-color_trc", Transfer(input), "-f", "webm" });
            }
            // The encoder thread count changes the bitstream, never the verification below.
            args.AddRange(new[] { "-threads:v", Threads, "-fps_mode", "passthrough", "-enc_time_base:v", input.time_base,
                "-avoid_negative_ts", "disabled", "-n", outputPath });
            Run(ffmpeg, args, pipe => pipe.CopyTo(Stream.Null));
        }

        private static string Transfer(Video input)
        { return string.IsNullOrEmpty(input.color_transfer) || input.color_transfer == "unknown" ? "bt709" : input.color_transfer; }

        // VP8's bitstream supports a BT.601-like matrix (RFC 6386 section 9.2), not BT.709.
        // FFmpeg's native decoder reports bt470bg and libvpx drops the matrix then falls back
        // to 601. Encode actual 601 YUV so both decoders agree; do NOT relabel 709 YUV as 601.
        private string OutputMatrix { get { return format == VideoConversionFormat.PackedH264 ? "bt709" : "bt470bg"; } }
        private string FrameColor(Video input)
        { return "setparams=range=limited:color_primaries=bt709:color_trc=" + Transfer(input) + ":colorspace=" + OutputMatrix; }
        private string Color420(Video input, bool alpha)
        {
            return "scale=out_color_matrix=" + OutputMatrix + ":out_range=limited,format=" +
                (alpha ? "yuva420p" : "yuv420p") + ",setsar=1," + FrameColor(input);
        }
        private string ColorReference(Video input)
        {
            // Canonical 4:2:0 source accounts for unavoidable chroma subsampling at hard edges.
            // Explicitly interpret that reference matrix, then compare in RGB against the
            // unforced output decode. YUV-plane equality alone cannot detect a matrix error.
            // Match the target's alpha-plane presence too: swscale's YUV and YUVA -> RGB
            // kernels differ at chroma edges even with identical color-plane bytes.
            return Color420(input, format == VideoConversionFormat.AlphaWebM) + ",scale=in_color_matrix=" + OutputMatrix +
                ":in_range=limited:out_range=full,format=rgb24";
        }
        private void ValidateColorMetadata(Video output, Video input)
        {
            if (output.color_range != "tv" || output.color_space != OutputMatrix || output.color_primaries != "bt709" ||
                output.color_transfer != Transfer(input))
                throw new InvalidDataException("Output color metadata is missing or inconsistent (matrix=" + output.color_space +
                    ", primaries=" + output.color_primaries + ", transfer=" + output.color_transfer + ", range=" + output.color_range + ").");
        }
        private void ValidatePackedBitstream(Video input)
        {
            // MP4 colr tags can conceal an incomplete H.264 SPS when probing the container.
            // Re-probe one copied access unit without ANY container metadata; no re-encoding.
            string headerPath = Path.Combine(workingDirectory, "header.h264");
            using (var header = CreateOwned(headerPath))
                Run(ffmpeg, new[] { "-v", "error", "-nostdin", "-i", outputPath, "-map", "0:v:0", "-frames:v", "1",
                    "-c:v", "copy", "-bsf:v", "h264_mp4toannexb", "-f", "h264", "pipe:1" }, pipe => pipe.CopyTo(header));
            ValidateColorMetadata(Probe(headerPath), input);
        }

        private Video Probe(string path)
        {
            Video video = null; int streams = 0;
            Run(ffprobe, new[] { "-v", "error", "-select_streams", "v", "-show_entries",
                "stream=index,codec_name,width,height,pix_fmt,time_base,sample_aspect_ratio,field_order,color_range,color_space,color_transfer,color_primaries,nb_frames:stream_side_data=side_data_type,rotation:stream_tags=rotate",
                "-of", "default", "-i", path }, pipe => Lines(pipe, line =>
            {
                if (line == "[STREAM]") { streams++; video = new Video(); return; }
                int separator = line.IndexOf('=');
                if (video == null || separator <= 0) return;
                string key = line.Substring(0, separator), value = line.Substring(separator + 1);
                switch (key)
                {
                    case "index": video.index = Int(value); break;
                    case "width": video.width = Int(value); break;
                    case "height": video.height = Int(value); break;
                    case "codec_name": video.codec_name = value; break;
                    case "pix_fmt": video.pix_fmt = value; break;
                    case "time_base": video.time_base = value; break;
                    case "sample_aspect_ratio": video.sample_aspect_ratio = value; break;
                    case "field_order": video.field_order = value; break;
                    case "color_range": video.color_range = value; break;
                    case "color_space": video.color_space = value; break;
                    case "color_transfer": video.color_transfer = value; break;
                    case "color_primaries": video.color_primaries = value; break;
                    case "nb_frames": long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out video.DeclaredFrames); break;
                    case "rotation": case "TAG:rotate": video.rotated |= Math.Abs(Number(value)) > 0.001; break;
                    case "side_data_type": video.rotated |= value == "Display Matrix"; break;
                }
            }));
            if (streams != 1 || video == null || video.index < 0 || video.width <= 0 || video.height <= 0 || !Ratio(video.time_base, out video.Tick))
                throw new InvalidDataException("Exactly one video stream with valid dimensions and time base is required.");
            return video;
        }

        // Reference is disk-backed: memory remains bounded for long clips. FFprobe scans every
        // decoded frame. Native VP8 can count/timestamp color frames; alpha is separately forced
        // through libvpx in ALL FFmpeg source/output decode and encode paths below.
        private void ReadTimeline(string path, Video video, Stream reference, Video sourceVideo)
        {
            var writer = sourceVideo == null ? new BinaryWriter(reference, Encoding.UTF8, true) : null;
            var reader = sourceVideo != null ? new BinaryReader(reference, Encoding.UTF8, true) : null;
            double first = 0, previous = -1, lastDuration = 0;
            long frames = 0;
            double tolerance = sourceVideo == null ? 0 : Math.Max(0.000002, sourceVideo.Tick + video.Tick + 0.000002);
            try
            {
                // Frame-threaded decoding returns the same frames and timestamps, several times faster.
                Run(ffprobe, new[] { "-v", "error", "-threads", Threads, "-select_streams", "v:0", "-show_frames", "-show_entries",
                    "frame=best_effort_timestamp_time,duration_time,pkt_duration_time,color_range,color_space,color_primaries,color_transfer", "-of", "compact=p=0:nk=0", "-i", path }, pipe => Lines(pipe, line =>
                {
                    // Ignore standalone side-data lines (e.g. H.264 unregistered SEI).
                    if (line.IndexOf("best_effort_timestamp_time=", StringComparison.Ordinal) < 0) return;
                    double pts = double.NaN, duration = 0;
                    var frameColor = sourceVideo == null ? null : new Video();
                    foreach (string part in line.Split('|'))
                    {
                        int equal = part.IndexOf('='); if (equal < 0) continue;
                        string key = part.Substring(0, equal), value = part.Substring(equal + 1);
                        if (key == "best_effort_timestamp_time") pts = Number(value);
                        if ((key == "duration_time" || key == "pkt_duration_time") && value != "N/A") duration = Number(value);
                        if (frameColor != null)
                        {
                            if (key == "color_range") frameColor.color_range = value;
                            if (key == "color_space") frameColor.color_space = value;
                            if (key == "color_primaries") frameColor.color_primaries = value;
                            if (key == "color_transfer") frameColor.color_transfer = value;
                        }
                    }
                    if (frameColor != null) ValidateColorMetadata(frameColor, sourceVideo);
                    if (double.IsNaN(pts) || double.IsInfinity(pts)) throw new InvalidDataException("A decoded frame has no usable timestamp.");
                    if (frames == 0) first = pts;
                    pts -= first;
                    if (frames > 0 && pts <= previous) throw new InvalidDataException("Frame timestamps are not strictly increasing.");
                    if (writer != null) { writer.Write(pts); writer.Write(duration); }
                    else
                    {
                        if (reference.Position == reference.Length) throw new InvalidDataException("Output has extra frames.");
                        double expected = reader.ReadDouble(), expectedDuration = reader.ReadDouble();
                        if (Math.Abs(pts - expected) > tolerance || (frames == 0 && Math.Abs(first) > tolerance))
                            throw new InvalidDataException("Frame timestamp changed at frame " + frames + ".");
                        if (reference.Position == reference.Length && Math.Abs(duration - expectedDuration) > tolerance)
                            throw new InvalidDataException("Last-frame duration changed; this source timing cannot be represented faithfully in the selected format.");
                    }
                    previous = pts; lastDuration = duration; frames++;
                    if ((frames & 255) == 0) CheckCancelled();
                }));
                if (frames == 0 || lastDuration <= 0 || (video.DeclaredFrames > 0 && video.DeclaredFrames != frames))
                    throw new InvalidDataException("Decoded frames or final-frame duration disagree with source metadata or are missing.");
                if (reader != null && reference.Position != reference.Length) throw new InvalidDataException("Output lost frames.");
                video.Frames = frames;
                // Reject impossible lengths before doing any multiplication in raw readers.
                checked { long byteCount = video.Pixels * 3 * frames; if (byteCount <= 0) throw new InvalidDataException("Empty video."); }
            }
            finally { writer?.Dispose(); reader?.Dispose(); }
        }

        private Extrema ScanRaw(string path, Video video, string filter, string pixelFormat, long expectedBytes,
            Stream save, Stream compare, bool alpha, long bytesPerFrame = 0)
        {
            var args = DecodeArguments(path, video);
            args.AddRange(new[] { "-map", "0:" + video.index.ToString(CultureInfo.InvariantCulture), "-an", "-sn", "-dn", "-filter_threads", Threads,
                "-vf", filter, "-pix_fmt", "+" + pixelFormat, "-c:v", "rawvideo",
                "-fps_mode", "passthrough", "-f", "rawvideo", "pipe:1" });
            int minimum = 255, maximum = 0;
            long bytes = 0, frameBytes = 0, frameError = 0;
            int worstError = 0;
            Run(ffmpeg, args, pipe =>
            {
                var buffer = new byte[65536]; var original = compare == null ? null : new byte[buffer.Length]; int count;
                while ((count = pipe.Read(buffer, 0, buffer.Length)) != 0)
                {
                    CheckCancelled();
                    if (bytes > expectedBytes - count) throw new InvalidDataException("Decoded video contains extra pixel bytes.");
                    save?.Write(buffer, 0, count);
                    if (compare != null) ReadExactly(compare, original, count);
                    for (int i = 0; i < count; i++)
                    {
                        int value = buffer[i];
                        if (alpha) { minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value); }
                        if (compare == null) continue;
                        int error = Math.Abs(value - original[i]);
                        worstError = Math.Max(worstError, error); frameError += error; frameBytes++;
                        // Every pixel and every frame must pass. Whole-video averages can hide a
                        // single damaged frame or a small misplaced corner mark, so aren't used.
                        if (error > 16)
                            throw new InvalidDataException((alpha ? "Matte" : "Color") + " differs from source at decoded byte " + (bytes + i) + " (error " + error + ").");
                        if (frameBytes == bytesPerFrame)
                        {
                            if ((double)frameError / frameBytes > 2.0)
                                throw new InvalidDataException((alpha ? "Matte" : "Color") + " mean error exceeds the per-frame quality limit (max " + worstError + ").");
                            frameError = frameBytes = 0; worstError = 0;
                        }
                    }
                    bytes += count;
                }
            });
            if (bytes != expectedBytes || (compare != null && (frameBytes != 0 || compare.ReadByte() != -1)))
                throw new InvalidDataException("Full-frame decoded pixel dimensions/count do not match the source.");
            return new Extrema { Minimum = minimum, Maximum = maximum };
        }

        private List<string> DecodeArguments(string path, Video video)
        {
            // H.264, FFV1 and libvpx decoding and these swscale chains were measured byte-identical with
            // 1, 8, 16 and automatic threads (Validation~/Result.md), so the cap only bounds the load.
            var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-xerror", "-copyts", "-err_detect", "explode", "-threads", Threads, "-noautorotate" };
            if (video.codec_name == "vp8") args.AddRange(new[] { "-c:v", "libvpx" });
            if (video.codec_name == "vp9") args.AddRange(new[] { "-c:v", "libvpx-vp9" });
            args.AddRange(new[] { "-i", path });
            return args;
        }

        private void Run(string executable, IEnumerable<string> arguments, Action<Stream> consume)
        {
            CheckCancelled();
            var quoted = new List<string>(); foreach (var argument in arguments) quoted.Add(Quote(argument));
            using (var process = new Process { StartInfo = new ProcessStartInfo {
                FileName = executable, Arguments = string.Join(" ", quoted), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8 } })
            {
                try { if (!process.Start()) throw new IOException("Could not start video tool: " + executable); }
                catch (Win32Exception ex)
                {
                    // The framework message also names the current working directory; keep only the OS reason.
                    throw new IOException("Could not start video tool '" + executable + "': " + new Win32Exception(ex.NativeErrorCode).Message, ex);
                }
                // Below normal: many tool threads must not starve the Editor's interactive threads.
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch (Win32Exception) { }
                catch (InvalidOperationException) { }
                catch (NotSupportedException) { }
                Task output = null; Task<string> errors = null;
                try
                {
                    process.StandardInput.Close();
                    errors = DrainErrors(process.StandardError);
                    output = Task.Run(() => consume(process.StandardOutput.BaseStream));
                    while (!process.WaitForExit(100))
                        if (Volatile.Read(ref cancelled) != 0 || output.IsFaulted || errors.IsFaulted) Kill(process);
                    CheckCancelled();
                    // Retain a validation/pipe exception instead of replacing it with broken-pipe stderr.
                    output.GetAwaiter().GetResult();
                    string tail = errors.GetAwaiter().GetResult();
                    if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(tail))
                        throw new IOException(Path.GetFileName(executable) + " failed (exit " + process.ExitCode + "): " + tail.Trim());
                }
                finally
                {
                    // Completion (and deletion) cannot race a live writer, even if reading failed.
                    while (!process.WaitForExit(100)) Kill(process);
                    try { output?.GetAwaiter().GetResult(); } catch { }
                    try { errors?.GetAwaiter().GetResult(); } catch { }
                }
            }
        }

        private static async Task<string> DrainErrors(StreamReader reader)
        {
            var tail = new StringBuilder(); var buffer = new char[4096]; int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            {
                tail.Append(buffer, 0, count);
                if (tail.Length > DiagnosticLimit) tail.Remove(0, tail.Length - DiagnosticLimit);
            }
            return tail.ToString();
        }

        private void Lines(Stream pipe, Action<string> consume)
        {
            using (var reader = new StreamReader(pipe, Encoding.UTF8, true, 4096, true))
            {
                var line = new StringBuilder(); var buffer = new char[4096]; int count;
                while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
                {
                    CheckCancelled();
                    for (int i = 0; i < count; i++)
                    {
                        char c = buffer[i];
                        if (c == '\n') { consume(line.ToString().TrimEnd('\r')); line.Clear(); }
                        else { line.Append(c); if (line.Length > DiagnosticLimit) throw new InvalidDataException("Video tool response line is too long."); }
                    }
                }
                if (line.Length != 0) consume(line.ToString().TrimEnd('\r'));
            }
        }

        private void Kill(Process process)
        {
            try { process.Kill(); } // Direct child only; no shell, tree helper, or kill-by-name.
            catch (InvalidOperationException) { }
            catch (Win32Exception) { status = "Waiting for the video tool to terminate…"; }
        }

        private void CreateWorkingDirectory()
        {
            string candidate = Path.Combine(temporaryDirectory, "video-to-unity-" + Guid.NewGuid().ToString("N"));
            // The sibling lease is reserved with CreateNew before touching the directory.
            using (CreateOwned(candidate + ".lease")) { }
            if (Directory.Exists(candidate) || File.Exists(candidate)) throw new IOException("Temporary workspace collision.");
            Directory.CreateDirectory(candidate);
            workingDirectory = candidate;
        }

        private FileStream CreateOwned(string path)
        {
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
            ownedFiles.Add(path);
            return stream;
        }

        private void Cleanup(bool keepOutput)
        {
            lock (cleanupLock)
            {
                for (int i = ownedFiles.Count - 1; i >= 0; i--)
                {
                    string path = ownedFiles[i]; if (keepOutput && path == outputPath) continue;
                    try { File.Delete(path); ownedFiles.RemoveAt(i); }
                    catch (IOException ex) { status = "Temporary cleanup failed: " + ex.Message; }
                    catch (UnauthorizedAccessException ex) { status = "Temporary cleanup failed: " + ex.Message; }
                }
                if (!keepOutput && workingDirectory != null)
                {
                    try { Directory.Delete(workingDirectory, false); workingDirectory = null; }
                    catch (IOException ex) { status = "Temporary cleanup failed: " + ex.Message; }
                    catch (UnauthorizedAccessException ex) { status = "Temporary cleanup failed: " + ex.Message; }
                }
            }
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count) { int read = stream.Read(buffer, offset, count - offset); if (read == 0) throw new InvalidDataException("Truncated source reference."); offset += read; }
        }

        private static string Crop(int width, int height, int x)
        { return string.Format(CultureInfo.InvariantCulture, "crop=w={0}:h={1}:x={2}:y=0:exact=1", width, height, x); }
        private static int Int(string value) { return int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture); }
        private static double Number(string value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || double.IsInfinity(number) || double.IsNaN(number))
                throw new InvalidDataException("Invalid video timestamp/number: " + value);
            return number;
        }
        private static bool Ratio(string value, out double result)
        {
            result = 0; var parts = (value ?? "").Split('/');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var numerator) || numerator <= 0 ||
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var denominator) || denominator <= 0) return false;
            result = (double)numerator / denominator; return true;
        }
        private static string FullPath(string path, string name)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0) throw new ArgumentException("Specify a valid local path.", name);
            return Path.GetFullPath(path);
        }
        private static string Executable(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(new[] { '\0', '\r', '\n', '"' }) >= 0)
                throw new ArgumentException("Specify the FFmpeg/FFprobe executable, without command arguments.");
            if (path.IndexOfAny(new[] { '/', '\\' }) >= 0 || Path.IsPathRooted(path))
            { path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("Video tool not found.", path); }
            return path;
        }
        // .NET/Mono ProcessStartInfo.Arguments quoting, including embedded quotes and trailing
        // backslashes. UseShellExecute=false: metacharacters and non-ASCII names remain arguments.
        private static string Quote(string argument)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); result.Append(c); slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        private sealed class Video
        {
            public int index = -1, width, height;
            public string codec_name, pix_fmt, time_base, sample_aspect_ratio, field_order, color_range, color_space, color_transfer, color_primaries;
            public long Frames, DeclaredFrames;
            public double Tick;
            public bool rotated;
            public long Pixels { get { return (long)width * height; } }
        }
        private struct Extrema { public int Minimum, Maximum; }
    }
}
