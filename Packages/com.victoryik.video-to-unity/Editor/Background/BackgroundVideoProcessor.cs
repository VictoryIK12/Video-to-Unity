using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VideoToUnity.Editor
{
    /// <summary>
    /// Asynchronous background removal for an opaque local video, without any Unity API.
    /// Analyze samples about five frames at modest resolution; Preview processes one frame;
    /// Convert streams every full-resolution frame (FFmpeg decoder stdout → BackgroundMatteSession →
    /// FFmpeg FFV1 encoder stdin) into an owned lossless BGRA QuickTime intermediate whose integer
    /// clock is the source's own tick, so final frame times stay on the exact constant-rate grid.
    /// Every intermediate frame is decoded again and compared by a per-frame 128-bit hash before the
    /// verified <see cref="VideoConverter"/> runs. Output RGB is STRAIGHT (not premultiplied). Audio
    /// is discarded. Result.Conversion.FilePath stays valid until the job is disposed; copy it first.
    /// </summary>
    public static class BackgroundVideoProcessor
    {
        private const int AnalysisMaximumSide = 640, AnalysisSamples = 5;

        public static IBackgroundJob Start(BackgroundRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Kind != BackgroundJobKind.Analyze && request.Kind != BackgroundJobKind.Preview && request.Kind != BackgroundJobKind.Convert)
                throw new ArgumentOutOfRangeException(nameof(request), "Unknown background job kind.");
            if (request.Format != VideoConversionFormat.PackedH264 && request.Format != VideoConversionFormat.AlphaWebM)
                throw new ArgumentOutOfRangeException(nameof(request), "Unknown output format.");
            var options = (request.Options ?? throw new ArgumentException("Background options are required.", nameof(request))).Copy();
            BackgroundMatteSession.Validate(options);
            if (request.Kind == BackgroundJobKind.Preview)
            {
                if (double.IsNaN(request.PreviewTimeSeconds) || double.IsInfinity(request.PreviewTimeSeconds) || request.PreviewTimeSeconds < 0)
                    throw new ArgumentOutOfRangeException(nameof(request), "Preview time must be a finite, non-negative number of seconds.");
                if (request.PreviewMaximumWidth < 2 || request.PreviewMaximumWidth > 8192)
                    throw new ArgumentOutOfRangeException(nameof(request), "Preview width must be between 2 and 8192 pixels.");
            }
            string source = FullPath(request.SourcePath, "SourcePath"), directory = FullPath(request.TemporaryDirectory, "TemporaryDirectory");
            if (!File.Exists(source)) throw new FileNotFoundException("Source video does not exist.", source);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Temporary directory does not exist: " + directory);
            var job = new Job(request, options, source, directory,
                BackgroundProcess.Executable(request.Ffmpeg), BackgroundProcess.Executable(request.Ffprobe));
            job.Begin();
            return job;
        }

        private static string FullPath(string path, string name)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
                throw new ArgumentException("Specify a valid local path.", name);
            path = Path.GetFullPath(path);
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new ArgumentException("Device paths are not supported; use an ordinary local path.", name);
            return path;
        }

        private sealed class Job : IBackgroundJob
        {
            private readonly BackgroundJobKind kind;
            private readonly BackgroundOptions options;
            private readonly string source, temporaryDirectory, ffmpeg, ffprobe;
            private readonly double previewTime;
            private readonly int previewWidth;
            private readonly VideoConversionFormat format;
            private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
            private readonly object cleanupLock = new object();
            private readonly List<string> ownedFiles = new List<string>();
            private volatile string status = "Waiting to start…";
            private volatile VideoConverter converter;
            private string workingDirectory, leasePath;
            private int cancelled, disposed;
            private bool sequentialFrames; // A strict keyframe seek failed once (open GOP): decode from frame 0.

            public Job(BackgroundRequest request, BackgroundOptions options, string source, string directory, string ffmpeg, string ffprobe)
            {
                kind = request.Kind; this.options = options; this.source = source; temporaryDirectory = directory;
                this.ffmpeg = ffmpeg; this.ffprobe = ffprobe; previewTime = request.PreviewTimeSeconds;
                previewWidth = request.PreviewMaximumWidth; format = request.Format;
            }

            public Task<BackgroundResult> Completion { get; private set; }

            public string Status
            {
                get
                {
                    var current = converter;
                    return current != null && !current.Completion.IsCompleted && Volatile.Read(ref cancelled) == 0
                        ? "Encoding final video: " + current.Status : status;
                }
            }

            public void Begin() { Completion = RunAsync(); }

            // The async boundary turns OperationCanceledException into a Canceled task.
            private async Task<BackgroundResult> RunAsync() { return await Task.Run(() => Execute()).ConfigureAwait(false); }

            public void Cancel()
            {
                if (Completion.IsCompleted || Interlocked.Exchange(ref cancelled, 1) != 0) return;
                status = "Cancelling…";
                // Registered callbacks only queue child termination; the caller never waits.
                cancellation.Cancel();
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                Cancel();
                if (Completion.IsCompleted) Cleanup(false);
                else Completion.ContinueWith(_ => Cleanup(false), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }

            private BackgroundResult Execute()
            {
                var token = cancellation.Token;
                bool success = false;
                try
                {
                    token.ThrowIfCancellationRequested();
                    // The source stays read-only (no writers, replacement or deletion) while processing.
                    using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        status = "Checking source video and every frame timestamp…";
                        var video = BackgroundSourceVideo.Inspect(ffprobe, source, token);
                        var result = new BackgroundResult { Width = video.Width, Height = video.Height,
                            DurationSeconds = video.DurationSeconds, FramesPerSecond = video.FramesPerSecond };
                        if (kind == BackgroundJobKind.Analyze)
                        {
                            result.Analysis = Analyze(video, token);
                            if (!result.Analysis.Reliable) result.Warning = result.Analysis.Message;
                        }
                        else
                        {
                            BackgroundAnalysis analysis = null;
                            if (options.AutoDetect)
                            {
                                analysis = Analyze(video, token);
                                result.Analysis = analysis;
                                // Never guess: a mixed border could remove a foreground-colored frame.
                                if (!analysis.Reliable)
                                    throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                                        "Automatic background detection is uncertain ({0:0}% confidence). {1}", analysis.Confidence * 100, analysis.Message));
                            }
                            if (kind == BackgroundJobKind.Preview) result.Preview = Preview(video, analysis, token);
                            else Convert(video, analysis, result, token);
                        }
                        token.ThrowIfCancellationRequested();
                        success = true;
                        status = kind == BackgroundJobKind.Analyze ? "Analysis complete." : kind == BackgroundJobKind.Preview
                            ? "Preview ready." : "Complete: background removed and final video verified.";
                        return result;
                    }
                }
                catch (OperationCanceledException) { status = "Background processing cancelled."; throw; }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) { status = "Background processing cancelled."; throw new OperationCanceledException("Background processing cancelled.", ex, token); }
                    string message = ex is InvalidOperationException && ex.InnerException != null ? ex.Message : "Background processing failed: " + ex.Message;
                    status = message;
                    throw new InvalidOperationException(message, ex);
                }
                finally { Cleanup(success && Volatile.Read(ref disposed) == 0); }
            }

            private BackgroundAnalysis Analyze(BackgroundSourceVideo video, CancellationToken token)
            {
                double scale = Math.Min(1.0, (double)AnalysisMaximumSide / Math.Max(video.Width, video.Height));
                int width = Math.Max(1, (int)Math.Round(video.Width * scale)), height = Math.Max(1, (int)Math.Round(video.Height * scale));
                int count = Math.Min(AnalysisSamples, video.Frames);
                var frames = new List<byte[]>();
                for (int i = 0; i < count; i++)
                {
                    status = "Sampling background color (" + (i + 1) + "/" + count + ")…";
                    int index = Math.Min(video.Frames - 1, (int)((i + .5) * video.Frames / count));
                    frames.Add(DecodeFrame(video, index, width, height, token));
                }
                return BackgroundMatte.Analyze(frames, width, height);
            }

            private BackgroundPreview Preview(BackgroundSourceVideo video, BackgroundAnalysis analysis, CancellationToken token)
            {
                int width = Math.Min(video.Width, previewWidth);
                int height = Math.Max(1, (int)Math.Round((double)video.Height * width / video.Width));
                int index = video.FrameAt(previewTime);
                status = "Decoding preview frame…";
                var original = DecodeFrame(video, index, width, height, token);
                var processed = new byte[original.Length];
                status = "Removing background from preview frame…";
                new BackgroundMatteSession(width, height, options, analysis).Process(original, processed, token);
                return new BackgroundPreview { OriginalRgba = original, ResultRgba = processed, Width = width, Height = height,
                    TimeSeconds = video.RelativeSeconds(index) };
            }

            private byte[] DecodeFrame(BackgroundSourceVideo video, int index, int width, int height, CancellationToken token)
            {
                if (!sequentialFrames && video.GopStart(index) > 0)
                {
                    try { return RequireOpaque(DecodeOne(video.DecodeArguments(width, height, index, true), index, width, height, token), index); }
                    catch (Exception ex) when (!(ex is OperationCanceledException) && !(ex is AlphaSourceException) && !token.IsCancellationRequested)
                    {
                        // Leading pictures of an open-GOP keyframe reference the previous GOP, so the strict
                        // decoder rejects a seek there. Decoding from the first frame is slower, still exact.
                        sequentialFrames = true;
                        status = "Open-GOP video: decoding frames from the start for exact seeking…";
                    }
                }
                return RequireOpaque(DecodeOne(video.DecodeArguments(width, height, index, true, true), index, width, height, token), index);
            }

            private byte[] DecodeOne(List<string> arguments, int index, int width, int height, CancellationToken token)
            {
                var frame = new byte[checked(width * height * 4)];
                bool complete = false;
                BackgroundProcess.Run(ffmpeg, arguments, pipe =>
                {
                    complete = BackgroundProcess.ReadFrame(pipe, frame);
                    if (pipe.ReadByte() != -1) throw new InvalidDataException("Decoder returned more than one frame.");
                }, token);
                if (!complete) throw new InvalidDataException("Could not decode frame " + index + "; the source may be truncated.");
                return frame;
            }

            /// <summary>
            /// Background removal creates alpha for opaque videos only. Existing transparency is never
            /// replaced or merged silently: such sources belong to the alpha video conversion.
            /// </summary>
            private static byte[] RequireOpaque(byte[] rgba, int frame)
            {
                for (int p = 3; p < rgba.Length; p += 4)
                    if (rgba[p] != 255) throw new AlphaSourceException(frame);
                return rgba;
            }

            private void Convert(BackgroundSourceVideo video, BackgroundAnalysis analysis, BackgroundResult result, CancellationToken token)
            {
                var session = new BackgroundMatteSession(video.Width, video.Height, options, analysis);
                long timescale = video.ExactTimescale;
                CreateWorkingDirectory();
                string intermediate = Path.Combine(workingDirectory, "matte-intermediate.mov");
                if (File.Exists(intermediate)) throw new IOException("Refusing to overwrite an existing temporary file.");
                ownedFiles.Add(intermediate);
                int minimumAlpha, maximumAlpha;
                List<byte[]> hashes = EncodeIntermediate(video, session, intermediate, timescale, token, out minimumAlpha, out maximumAlpha);
                if (minimumAlpha == 255)
                    throw new InvalidDataException("No background was removed: every pixel stayed opaque. Adjust the background color, threshold or protected regions.");
                VerifyIntermediate(video, intermediate, timescale, hashes, token);

                status = "Encoding final video…";
                var job = VideoConverter.Start(intermediate, workingDirectory, ffmpeg, ffprobe, format);
                converter = job;
                VideoConverter.Result conversion;
                using (token.Register(() => job.Cancel()))
                {
                    try { conversion = job.Completion.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); throw new InvalidOperationException("Final video encoding was cancelled unexpectedly."); }
                }
                token.ThrowIfCancellationRequested();
                // The verified result no longer needs the lossless intermediate.
                DeleteOwned(intermediate);
                if (conversion.Width != video.Width || conversion.Height != video.Height || conversion.Frames != video.Frames)
                    throw new InvalidDataException("Final video geometry or frame count differs from the source.");
                result.Conversion = conversion;
                if (maximumAlpha == 0 || conversion.MaximumAlpha == 0)
                    result.Warning = "The whole result is transparent: every pixel matched the background. Review the background color, threshold and protected regions before using this video.";
            }

            private List<byte[]> EncodeIntermediate(BackgroundSourceVideo video, BackgroundMatteSession session, string path, long timescale,
                CancellationToken token, out int minimumAlpha, out int maximumAlpha)
            {
                minimumAlpha = 255; maximumAlpha = 0;
                int frameBytes = checked(video.Width * video.Height * 4);
                var input = new byte[frameBytes]; var output = new byte[frameBytes];
                var hashes = new List<byte[]>(video.Frames);
                using (var decoder = BackgroundProcess.Start(ffmpeg, video.DecodeArguments(video.Width, video.Height, 0, false), false))
                using (var encoder = BackgroundProcess.Start(ffmpeg, EncoderArguments(video, path, timescale), true))
                {
                    var drain = Task.Run(() => encoder.Output.CopyTo(Stream.Null));
                    // Callback runs on the cancelling thread: only queue termination there.
                    using (token.Register(() => ThreadPool.QueueUserWorkItem(_ => { decoder.Kill(); encoder.Kill(); })))
                    {
                        try
                        {
                            var decoded = decoder.Output; var encoded = encoder.Input;
                            for (int frame = 0; frame < video.Frames; frame++)
                            {
                                if (!BackgroundProcess.ReadFrame(decoded, input))
                                {
                                    token.ThrowIfCancellationRequested();
                                    throw new InvalidDataException("Decoder produced " + frame + " of " + video.Frames + " frames; the source is truncated or has missing frames. " + decoder.Diagnostics());
                                }
                                token.ThrowIfCancellationRequested();
                                RequireOpaque(input, frame);
                                session.Process(input, output, token);
                                for (int p = 0; p < frameBytes; p += 4)
                                {
                                    byte a = output[p + 3];
                                    if (a < minimumAlpha) minimumAlpha = a;
                                    if (a > maximumAlpha) maximumAlpha = a;
                                    byte red = output[p]; output[p] = output[p + 2]; output[p + 2] = red; // RGBA -> BGRA
                                }
                                // Exactly the bytes written below; FFmpeg's framehash of the decoded file must match.
                                hashes.Add(BackgroundFrameHash.Compute(output, 0, frameBytes));
                                try { encoded.Write(output, 0, frameBytes); }
                                catch (IOException ex)
                                {
                                    token.ThrowIfCancellationRequested();
                                    throw new IOException("Intermediate encoder stopped: " + encoder.Diagnostics(), ex);
                                }
                                status = "Removing background: frame " + (frame + 1) + " of " + video.Frames + "…";
                            }
                            if (decoded.ReadByte() != -1)
                                throw new InvalidDataException("Decoder produced more frames than the source timeline lists.");
                            encoded.Close();
                            status = "Finishing lossless intermediate…";
                            decoder.Finish(token);
                            encoder.Finish(token);
                            drain.GetAwaiter().GetResult();
                        }
                        catch
                        {
                            decoder.Kill(); encoder.Kill();
                            throw;
                        }
                        finally { try { drain.Wait(); } catch { } }
                    }
                    return hashes;
                }
            }

            private static List<string> EncoderArguments(BackgroundSourceVideo video, string path, long timescale)
            {
                string size = video.Width.ToString(CultureInfo.InvariantCulture) + "x" + video.Height.ToString(CultureInfo.InvariantCulture);
                string clock = timescale.ToString(CultureInfo.InvariantCulture);
                // Straight BGRA code values, explicitly sRGB transfer, BT.709 primaries, full-range RGB.
                // QuickTime stores the exact integer clock and colr tags; Matroska would round to milliseconds.
                return new List<string> { "-hide_banner", "-v", "error", "-nostdin",
                    "-f", "rawvideo", "-pix_fmt", "bgra", "-s", size, "-framerate", video.Rate, "-i", "pipe:0",
                    "-map", "0:v:0", "-an", "-sn", "-dn", "-map_metadata", "-1",
                    "-vf", "setparams=range=full:color_primaries=bt709:color_trc=iec61966-2-1:colorspace=gbr",
                    "-c:v", "ffv1", "-level", "3", "-slicecrc", "1", "-g", "1", "-pix_fmt", "bgra",
                    "-color_range", "pc", "-color_primaries", "bt709", "-color_trc", "iec61966-2-1", "-colorspace", "rgb",
                    "-threads:v", BackgroundProcess.Threads, "-fps_mode", "passthrough", "-enc_time_base:v", "1/" + clock,
                    "-f", "mov", "-video_track_timescale", clock, "-movflags", "+write_colr", "-n", path };
            }

            private void VerifyIntermediate(BackgroundSourceVideo video, string path, long timescale, List<byte[]> hashes, CancellationToken token)
            {
                status = "Verifying lossless intermediate…";
                var stream = new Dictionary<string, string>(StringComparer.Ordinal); int streams = 0;
                BackgroundProcess.Run(ffprobe, new[] { "-v", "error", "-show_entries",
                    "stream=codec_type,codec_name,width,height,pix_fmt,time_base,color_range,color_space,color_transfer,color_primaries",
                    "-of", "compact=p=0:nk=0", "-i", path }, pipe => BackgroundProcess.Lines(pipe, line =>
                {
                    if (line.Length == 0) return;
                    streams++;
                    foreach (string part in line.Split('|'))
                    {
                        int equal = part.IndexOf('=');
                        if (equal > 0) stream[part.Substring(0, equal)] = part.Substring(equal + 1);
                    }
                }, token), token);
                var expected = new Dictionary<string, string> { { "codec_type", "video" }, { "codec_name", "ffv1" },
                    { "width", video.Width.ToString(CultureInfo.InvariantCulture) }, { "height", video.Height.ToString(CultureInfo.InvariantCulture) },
                    { "pix_fmt", "bgra" }, { "time_base", "1/" + timescale.ToString(CultureInfo.InvariantCulture) }, { "color_space", "gbr" },
                    { "color_transfer", "iec61966-2-1" }, { "color_primaries", "bt709" } };
                foreach (var pair in expected)
                {
                    string actual;
                    if (!stream.TryGetValue(pair.Key, out actual)) actual = "missing";
                    if (streams != 1 || actual != pair.Value)
                        throw new InvalidDataException("Intermediate stream metadata is wrong (" + pair.Key + "=" + actual + ").");
                }
                // BGRA code values are full range by definition; QuickTime reports no separate range for RGB.
                string range;
                if (!stream.TryGetValue("color_range", out range) || (range != "pc" && range != "unknown"))
                    throw new InvalidDataException("Intermediate stream metadata is wrong (color_range=" + (range ?? "missing") + ").");

                // Every frame sits exactly on the constant-rate grid of the integer clock, and within
                // 1.5 source ticks of its own source timestamp (both are roundings of the same rate).
                var ticks = new List<long>();
                BackgroundProcess.Run(ffprobe, new[] { "-v", "error", "-select_streams", "0", "-show_packets", "-show_entries",
                    "packet=pts", "-of", "csv=p=0", "-i", path }, pipe => BackgroundProcess.Lines(pipe, line =>
                {
                    long tick;
                    if (line.Length == 0) return;
                    if (!long.TryParse(line.Trim().TrimEnd(','), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out tick))
                        throw new InvalidDataException("Intermediate frame has no timestamp.");
                    ticks.Add(tick);
                }, token), token);
                if (ticks.Count != video.Frames) throw new InvalidDataException("Intermediate has " + ticks.Count + " frames; source has " + video.Frames + ".");
                ticks.Sort();
                double tolerance = video.Tick * 1.5 + 1e-9;
                for (int i = 0; i < ticks.Count; i++)
                {
                    if (ticks[i] - ticks[0] != video.ExactTicks(i, timescale))
                        throw new InvalidDataException("Intermediate frame " + i + " is not on the exact constant-rate clock.");
                    if (Math.Abs((double)(ticks[i] - ticks[0]) / timescale - video.RelativeSeconds(i)) > tolerance)
                        throw new InvalidDataException("Intermediate frame " + i + " timestamp differs from the source.");
                }

                // Decode the complete intermediate and compare every frame's hash with what was written.
                int frame = 0;
                string frameSize = checked(video.Width * video.Height * 4).ToString(CultureInfo.InvariantCulture);
                BackgroundProcess.Run(ffmpeg, new[] { "-hide_banner", "-v", "error", "-nostdin", "-xerror", "-threads", BackgroundProcess.Threads, "-i", path,
                    "-map", "0:v:0", "-c:v", "rawvideo", "-pix_fmt", "bgra", "-fps_mode", "passthrough", "-f", "framehash", "-hash", "murmur3", "pipe:1" },
                    pipe => BackgroundProcess.Lines(pipe, line =>
                {
                    if (line.Length == 0 || line[0] == '#') return;
                    string[] fields = line.Split(',');
                    if (fields.Length != 6) throw new InvalidDataException("Unexpected frame hash output from FFmpeg.");
                    if (frame >= hashes.Count) throw new InvalidDataException("Intermediate decodes to more frames than were processed.");
                    if (fields[4].Trim() != frameSize || !string.Equals(fields[5].Trim(), Hex(hashes[frame]), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Lossless intermediate frame " + frame + " does not decode to the processed pixels.");
                    frame++;
                }, token), token);
                if (frame != hashes.Count)
                    throw new InvalidDataException("Intermediate decodes to " + frame + " of " + hashes.Count + " processed frames.");
            }

            private void CreateWorkingDirectory()
            {
                string candidate = Path.Combine(temporaryDirectory, "video-to-unity-background-" + Guid.NewGuid().ToString("N"));
                // Reserve a sibling lease with CreateNew before creating the private directory.
                using (new FileStream(candidate + ".lease", FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                leasePath = candidate + ".lease";
                if (Directory.Exists(candidate) || File.Exists(candidate)) throw new IOException("Temporary workspace collision.");
                Directory.CreateDirectory(candidate);
                workingDirectory = candidate;
            }

            private void DeleteOwned(string path)
            {
                lock (cleanupLock)
                {
                    File.Delete(path);
                    ownedFiles.Remove(path);
                }
            }

            private void Cleanup(bool keepResult)
            {
                if (keepResult) return;
                lock (cleanupLock)
                {
                    // The converter deletes only its own files and directory, after its children exit.
                    var current = converter;
                    if (current != null) { current.Dispose(); if (!current.Completion.IsCompleted) return; }
                    for (int i = ownedFiles.Count - 1; i >= 0; i--)
                    {
                        try { File.Delete(ownedFiles[i]); ownedFiles.RemoveAt(i); }
                        catch (IOException ex) { status = "Temporary cleanup failed: " + ex.Message; }
                        catch (UnauthorizedAccessException ex) { status = "Temporary cleanup failed: " + ex.Message; }
                    }
                    if (workingDirectory != null)
                    {
                        try { Directory.Delete(workingDirectory, false); workingDirectory = null; } // Never recursive.
                        catch (IOException ex) { status = "Temporary cleanup failed: " + ex.Message; }
                        catch (UnauthorizedAccessException ex) { status = "Temporary cleanup failed: " + ex.Message; }
                    }
                    if (workingDirectory == null && leasePath != null)
                    {
                        try { File.Delete(leasePath); leasePath = null; }
                        catch (IOException ex) { status = "Temporary cleanup failed: " + ex.Message; }
                        catch (UnauthorizedAccessException ex) { status = "Temporary cleanup failed: " + ex.Message; }
                    }
                }
            }

            private static string Hex(byte[] bytes)
            {
                var text = new System.Text.StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        private sealed class AlphaSourceException : IOException
        {
            public AlphaSourceException(int frame) : base("Frame " + frame.ToString(CultureInfo.InvariantCulture) +
                " already contains transparent pixels. Background removal only adds alpha to opaque videos and never replaces existing alpha; " +
                "convert this video with Tools > Video to Unity > 영상 이펙트 만들기 instead.") { }
        }
    }
}

