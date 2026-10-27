using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace VideoToUnity.Editor
{
    /// <summary>
    /// Validated description of a local source video: one progressive SDR video stream with square,
    /// even-sized pixels and a verified constant frame rate. Every displayed frame timestamp is read
    /// (container packets, edit-list discards excluded) before any frame is processed.
    /// </summary>
    internal sealed class BackgroundSourceVideo
    {
        public string Path, Url, Codec, PixelFormat, ColorSpace = "", ColorRange = "", Transfer = "", Primaries = "";
        public int StreamIndex = -1, Width, Height;
        public long TimeBaseNumerator, TimeBaseDenominator, RateNumerator, RateDenominator, DeclaredFrames;
        public double StreamDuration = double.NaN;
        public long[] Timestamps; // Sorted presentation timestamps in stream ticks.
        public long[] KeyTimestamps = Array.Empty<long>(); // Sorted presentation timestamps of demuxer keyframes.
        public int Frames { get { return Timestamps.Length; } }
        public double Tick { get { return (double)TimeBaseNumerator / TimeBaseDenominator; } }
        public double FrameDuration { get { return (double)RateDenominator / RateNumerator; } }
        public double FramesPerSecond { get { return (double)RateNumerator / RateDenominator; } }
        public double DurationSeconds { get { return Frames * FrameDuration; } }
        public string Rate { get { return RateNumerator.ToString(CultureInfo.InvariantCulture) + "/" + RateDenominator.ToString(CultureInfo.InvariantCulture); } }
        public double RelativeSeconds(int frame) { return (Timestamps[frame] - Timestamps[0]) * Tick; }
        public double AbsoluteSeconds(int frame) { return Timestamps[frame] * Tick; }

        /// <summary>
        /// Integer clock for re-encoded frames: the source's own tick, doubled while coarser than
        /// 10 kHz, so every source tick maps to a whole number of output ticks and frame times never
        /// collapse onto a millisecond grid.
        /// </summary>
        public long ExactTimescale
        {
            get
            {
                long scale = TimeBaseDenominator;
                while (scale < 10000) scale *= 2;
                if (scale > int.MaxValue)
                    throw new InvalidDataException("Unsupported source time base " + TimeBaseNumerator.ToString(CultureInfo.InvariantCulture) + "/" +
                        TimeBaseDenominator.ToString(CultureInfo.InvariantCulture) + ". Re-export the video with a standard container.");
                return scale;
            }
        }

        /// <summary>Exact constant-rate presentation tick of a frame on <see cref="ExactTimescale"/>, as FFmpeg rounds it.</summary>
        public long ExactTicks(int frame, long timescale)
        {
            decimal ticks = (decimal)frame * RateDenominator * timescale / RateNumerator;
            return (long)decimal.Round(ticks, MidpointRounding.AwayFromZero);
        }

        /// <summary>Last frame whose presentation time is at or before the relative time.</summary>
        public int FrameAt(double seconds)
        {
            int low = 0, high = Frames - 1;
            double limit = seconds + Tick * .5 + 1e-9;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (RelativeSeconds(middle) <= limit) low = middle; else high = middle - 1;
            }
            return low;
        }

        public static BackgroundSourceVideo Inspect(string ffprobe, string path, CancellationToken token)
        {
            var video = Probe(ffprobe, path, token);
            video.Validate();
            video.ReadTimeline(ffprobe, token);
            return video;
        }

        private static BackgroundSourceVideo Probe(string ffprobe, string path, CancellationToken token)
        {
            var streams = new List<Dictionary<string, string>>();
            Dictionary<string, string> current = null;
            BackgroundProcess.Run(ffprobe, new[] { "-v", "error", "-protocol_whitelist", "file", "-show_entries",
                "stream=index,codec_type,codec_name,width,height,pix_fmt,time_base,r_frame_rate,sample_aspect_ratio,field_order,color_range,color_space,color_transfer,color_primaries,nb_frames,duration:stream_disposition=attached_pic:stream_side_data=side_data_type,rotation:stream_tags=rotate",
                "-of", "default", "-i", FileUrl(path) }, pipe => BackgroundProcess.Lines(pipe, line =>
            {
                if (line == "[STREAM]") { current = new Dictionary<string, string>(StringComparer.Ordinal); streams.Add(current); return; }
                if (line == "[/STREAM]") { current = null; return; }
                int separator = line.IndexOf('=');
                if (current == null || separator <= 0) return;
                string key = line.Substring(0, separator), value = line.Substring(separator + 1);
                if (key == "side_data_type" && value == "Display Matrix") current["display_matrix"] = "1";
                if ((key == "rotation" || key == "TAG:rotate") && value != "0") current["rotation"] = value;
                if (!current.ContainsKey(key)) current[key] = value;
            }, token), token);

            var videos = streams.FindAll(s => Get(s, "codec_type") == "video");
            if (videos.Count != 1)
                throw new InvalidDataException("Exactly one video stream is required; this file has " + videos.Count +
                    ". Remove additional video or cover-art streams before processing.");
            var v = videos[0];
            var result = new BackgroundSourceVideo { Path = path, Url = FileUrl(path) };
            if (!int.TryParse(Get(v, "index"), NumberStyles.None, CultureInfo.InvariantCulture, out result.StreamIndex) ||
                !int.TryParse(Get(v, "width"), NumberStyles.None, CultureInfo.InvariantCulture, out result.Width) ||
                !int.TryParse(Get(v, "height"), NumberStyles.None, CultureInfo.InvariantCulture, out result.Height) ||
                !Ratio(Get(v, "time_base"), out result.TimeBaseNumerator, out result.TimeBaseDenominator))
                throw new InvalidDataException("The video stream has no valid dimensions or time base.");
            result.Codec = Get(v, "codec_name"); result.PixelFormat = Get(v, "pix_fmt");
            result.ColorSpace = Get(v, "color_space"); result.ColorRange = Get(v, "color_range");
            result.Transfer = Get(v, "color_transfer"); result.Primaries = Get(v, "color_primaries");
            Ratio(Get(v, "r_frame_rate"), out result.RateNumerator, out result.RateDenominator);
            long.TryParse(Get(v, "nb_frames"), NumberStyles.None, CultureInfo.InvariantCulture, out result.DeclaredFrames);
            double duration;
            if (double.TryParse(Get(v, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out duration) && duration > 0 && !double.IsInfinity(duration))
                result.StreamDuration = duration;
            string sar = Get(v, "sample_aspect_ratio");
            if (sar.Length != 0 && sar != "N/A" && sar != "0:1" && sar != "1:1")
                throw new InvalidDataException("Non-square source pixels (" + sar + ") are unsupported. Export square pixels first.");
            double rotation;
            if (Get(v, "display_matrix") == "1" ||
                (double.TryParse(Get(v, "rotation"), NumberStyles.Float, CultureInfo.InvariantCulture, out rotation) && Math.Abs(rotation) > .001))
                throw new InvalidDataException("Rotation/display-transform metadata is unsupported. Bake the rotation into the source pixels first.");
            string field = Get(v, "field_order");
            if (field.Length != 0 && field != "unknown" && field != "progressive")
                throw new InvalidDataException("Interlaced video (" + field + ") is unsupported. Export progressive frames.");
            if (Get(v, "DISPOSITION:attached_pic") == "1")
                throw new InvalidDataException("The only video stream is a still cover image, not a video.");
            return result;
        }

        private void Validate()
        {
            try { BackgroundMatteSession.CheckedPixels(Width, Height); }
            catch (ArgumentOutOfRangeException) { throw new InvalidDataException("Video is " + Width + "x" + Height + ". Background processing supports at most 8192 pixels per side and 16 megapixels."); }
            if (Width < 2 || Height < 2 || (Width & 1) != 0 || (Height & 1) != 0)
                throw new InvalidDataException("Video is " + Width + "x" + Height + ". An even width and height are required; no pixels are resized, padded or cropped.");
            // HDR/wide gamut values are never relabeled as sRGB.
            if (Transfer == "smpte2084" || Transfer == "arib-std-b67" || Transfer == "smpte428" || Transfer == "bt2020-10" || Transfer == "bt2020-12")
                throw new InvalidDataException("HDR or BT.2020 transfer (" + Transfer + ") is unsupported. Export SDR Rec.709/sRGB video first.");
            if (Primaries.Length != 0 && Primaries != "unknown" && Primaries != "bt709")
                throw new InvalidDataException("Color primaries '" + Primaries + "' are unsupported. Export SDR Rec.709/sRGB video first; colors are not reinterpreted.");
            if (ColorSpace == "bt2020nc" || ColorSpace == "bt2020c" || ColorSpace == "ictcp" || ColorSpace == "chroma-derived-nc" || ColorSpace == "chroma-derived-c")
                throw new InvalidDataException("Wide-gamut/HDR matrix '" + ColorSpace + "' is unsupported. Export SDR Rec.709/sRGB video first.");
        }

        private void ReadTimeline(string ffprobe, CancellationToken token)
        {
            var stamps = new List<long>();
            var keys = new List<long>();
            long lastDuration = 0, lastPts = long.MinValue;
            BackgroundProcess.Run(ffprobe, new[] { "-v", "error", "-protocol_whitelist", "file", "-select_streams",
                StreamIndex.ToString(CultureInfo.InvariantCulture), "-show_packets", "-show_entries", "packet=pts,duration,flags",
                "-of", "compact=p=0:nk=0", "-i", Url }, pipe => BackgroundProcess.Lines(pipe, line =>
            {
                if (line.IndexOf("pts=", StringComparison.Ordinal) < 0) return;
                string pts = null, duration = null, flags = "";
                foreach (string part in line.Split('|'))
                {
                    int equal = part.IndexOf('='); if (equal < 0) continue;
                    string key = part.Substring(0, equal), value = part.Substring(equal + 1);
                    if (key == "pts") pts = value; else if (key == "duration") duration = value; else if (key == "flags") flags = value;
                }
                if (flags.IndexOf('D') >= 0) return; // Edit-list discarded packets are never displayed.
                long timestamp;
                if (!long.TryParse(pts, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out timestamp))
                    throw new InvalidDataException("A video frame has no usable presentation timestamp. Re-export the video with a standard container.");
                long length;
                if (!long.TryParse(duration, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out length)) length = 0;
                stamps.Add(timestamp);
                if (flags.IndexOf('K') >= 0) keys.Add(timestamp);
                if (timestamp > lastPts) { lastPts = timestamp; lastDuration = length; }
                if ((stamps.Count & 1023) == 0) token.ThrowIfCancellationRequested();
            }, token), token);
            if (stamps.Count == 0) throw new InvalidDataException("The video stream contains no frames.");
            stamps.Sort();
            for (int i = 1; i < stamps.Count; i++)
                if (stamps[i] == stamps[i - 1]) throw new InvalidDataException("Two frames share the same timestamp; the timeline is damaged.");
            if (DeclaredFrames > 0 && DeclaredFrames != stamps.Count)
                throw new InvalidDataException("The container declares " + DeclaredFrames + " frames but " + stamps.Count + " are present; the file may be truncated.");
            Timestamps = stamps.ToArray();
            keys.Sort();
            KeyTimestamps = keys.ToArray();
            ResolveConstantRate(lastDuration);
            if (!double.IsNaN(StreamDuration) && Math.Abs(StreamDuration - DurationSeconds) > FrameDuration * .5 + Tick * 2 + 1e-6)
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "Stream duration {0:0.######}s disagrees with {1} frames at {2:0.###} fps ({3:0.######}s); frames may be missing or truncated.",
                    StreamDuration, Frames, FramesPerSecond, DurationSeconds));
        }

        private void ResolveConstantRate(long lastDuration)
        {
            int n = Timestamps.Length;
            double tolerance = 1 + 1e-6; // One container tick: clocks round each timestamp independently.
            if (n == 1)
            {
                if (lastDuration <= 0) throw new InvalidDataException("A single-frame video needs a known frame duration.");
                Reduce(TimeBaseDenominator, checked(TimeBaseNumerator * lastDuration));
                return;
            }
            double average = (double)(Timestamps[n - 1] - Timestamps[0]) / (n - 1);
            var intervals = new long[n - 1];
            for (int i = 1; i < n; i++) intervals[i - 1] = Timestamps[i] - Timestamps[i - 1];
            Array.Sort(intervals);
            long typical = intervals[(n - 1) / 2];
            for (int i = 1; i < n; i++)
            {
                double drift = Timestamps[i] - Timestamps[0] - i * average;
                if (Math.Abs(drift) > tolerance)
                {
                    // Report the first irregular interval when one exists; otherwise the accumulated drift.
                    int at = i; long interval = Timestamps[i] - Timestamps[i - 1];
                    for (int j = 1; j < n; j++)
                        if (Math.Abs(Timestamps[j] - Timestamps[j - 1] - typical) > 2) { at = j; interval = Timestamps[j] - Timestamps[j - 1]; break; }
                    throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                        "Variable frame timing detected: frame {0} follows the previous frame after {1:0.###} ms instead of {2:0.###} ms. Re-export the video at a constant frame rate; timing is never changed silently.",
                        at, interval * Tick * 1000, typical * Tick * 1000));
                }
            }
            // Prefer the stream's declared rational rate when it reproduces every timestamp.
            bool declared = RateNumerator > 0 && RateDenominator > 0;
            if (declared)
            {
                double ticks = (double)RateDenominator * TimeBaseDenominator / ((double)RateNumerator * TimeBaseNumerator);
                for (int i = 1; i < n && declared; i++)
                    declared = Math.Abs(Timestamps[i] - Timestamps[0] - i * ticks) <= tolerance;
            }
            if (!declared)
            {
                long rounded = (long)Math.Round(average);
                if (rounded <= 0 || Math.Abs(average - rounded) > 1e-6)
                    throw new InvalidDataException("Cannot determine an exact constant frame rate from the timestamps. Re-export the video at a standard constant frame rate.");
                Reduce(TimeBaseDenominator, checked(TimeBaseNumerator * rounded));
            }
            else Reduce(RateNumerator, RateDenominator);
            double frameTicks = FrameDuration / Tick;
            if (lastDuration > 0 && Math.Abs(lastDuration - frameTicks) > tolerance)
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "The final frame lasts {0:0.###} ms instead of {1:0.###} ms. Re-export the video at a constant frame rate.",
                    lastDuration * Tick * 1000, FrameDuration * 1000));
        }

        private void Reduce(long numerator, long denominator)
        {
            long a = numerator, b = denominator;
            while (b != 0) { long t = a % b; a = b; b = t; }
            RateNumerator = numerator / a; RateDenominator = denominator / a;
            if (RateNumerator <= 0 || RateDenominator <= 0 || FramesPerSecond > 1000 || FramesPerSecond < .01)
                throw new InvalidDataException("Unsupported frame rate " + Rate + ".");
        }

        /// <summary>
        /// Decode to straight full-range RGBA at the requested size. Tagged YUV matrices/ranges are
        /// honored; an untagged matrix is read as BT.709 (ordinary sRGB display values).
        /// Every decode is strict: any decoder diagnostic fails. A single frame is selected by its
        /// exact presentation index. Unless <paramref name="sequential"/> is set, decoding starts at the
        /// keyframe that opens the target's GOP, so no reorder depth or seek heuristic can skip or
        /// substitute a frame. Open-GOP leading pictures make such a strict seek fail; callers then
        /// decode sequentially from frame 0, which is always exact.
        /// </summary>
        public List<string> DecodeArguments(int width, int height, int frame, bool single, bool sequential = false)
        {
            var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-xerror", "-err_detect", "explode",
                "-threads", BackgroundProcess.Threads, "-noautorotate", "-protocol_whitelist", "file" };
            int start = single && !sequential ? GopStart(frame) : 0;
            if (start > 0)
            {
                // Absolute input seek to half a frame before the keyframe; accurate seeking drops every
                // earlier frame, so output frame n is source frame start + n.
                double seconds = AbsoluteSeconds(start) - FrameDuration * .5;
                args.AddRange(new[] { "-seek_timestamp", "1", "-ss", seconds.ToString("0.#########", CultureInfo.InvariantCulture) });
            }
            if (Codec == "vp8") args.AddRange(new[] { "-c:v", "libvpx" });
            if (Codec == "vp9") args.AddRange(new[] { "-c:v", "libvpx-vp9" });
            string matrix = ColorSpace.Length == 0 || ColorSpace == "unknown" || ColorSpace == "reserved" ? "bt709" : "auto";
            string size = width == Width && height == Height ? "" :
                string.Format(CultureInfo.InvariantCulture, "w={0}:h={1}:flags=area+accurate_rnd:", width, height);
            string select = single ? "select=eq(n\\," + (frame - start).ToString(CultureInfo.InvariantCulture) + ")," : "";
            args.AddRange(new[] { "-i", Url, "-map", "0:" + StreamIndex.ToString(CultureInfo.InvariantCulture), "-an", "-sn", "-dn",
                "-filter_threads", BackgroundProcess.Threads, "-vf", select + "scale=" + size + "in_color_matrix=" + matrix + ":out_range=full,format=rgba,setsar=1",
                "-c:v", "rawvideo", "-pix_fmt", "rgba", "-fps_mode", "passthrough" });
            if (single) args.AddRange(new[] { "-frames:v", "1" });
            args.AddRange(new[] { "-f", "rawvideo", "pipe:1" });
            return args;
        }

        /// <summary>Index of the frame whose keyframe opens the GOP containing <paramref name="frame"/> (0 when unknown).</summary>
        public int GopStart(int frame)
        {
            int key = Array.BinarySearch(KeyTimestamps, Timestamps[frame]);
            if (key < 0) key = ~key - 1;
            if (key < 0) return 0;
            int index = Array.BinarySearch(Timestamps, KeyTimestamps[key]);
            return index < 0 ? 0 : index;
        }

        // The file: protocol prevents names such as "concat:..." from being parsed as other protocols.
        internal static string FileUrl(string path) { return "file:" + path; }

        private static string Get(Dictionary<string, string> values, string key)
        { string value; return values.TryGetValue(key, out value) ? value : ""; }

        private static bool Ratio(string value, out long numerator, out long denominator)
        {
            numerator = denominator = 0; var parts = (value ?? "").Split('/');
            return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out numerator) && numerator > 0 &&
                long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out denominator) && denominator > 0;
        }
    }
}

