using System;
using System.Threading.Tasks;

namespace VideoToUnity.Editor
{
    public enum BackgroundJobKind { Analyze, Preview, Convert }

    [Serializable]
    public struct BackgroundKeepRegion
    {
        // Normalized source coordinates, origin at TOP LEFT. Fixed across the clip.
        public float X, Y, Width, Height;
        public BackgroundKeepRegion(float x, float y, float width, float height)
        { X = x; Y = y; Width = width; Height = height; }
    }

    [Serializable]
    public sealed class BackgroundOptions
    {
        public bool AutoDetect = true;
        public byte Red, Green, Blue;
        public float Threshold = .06f;
        public float Softness = .08f;
        public bool BorderConnected = true;
        public float TemporalSmoothing = .15f;
        public BackgroundKeepRegion[] ProtectedRegions = Array.Empty<BackgroundKeepRegion>();

        public BackgroundOptions Copy()
        {
            var copy = (BackgroundOptions)MemberwiseClone();
            copy.ProtectedRegions = ProtectedRegions == null ? Array.Empty<BackgroundKeepRegion>() : (BackgroundKeepRegion[])ProtectedRegions.Clone();
            return copy;
        }
    }

    public sealed class BackgroundAnalysis
    {
        public byte Red, Green, Blue;
        public double Confidence;
        public bool Reliable => Confidence >= .6;
        public string Message;
    }

    public sealed class BackgroundRequest
    {
        public string SourcePath;
        public string TemporaryDirectory;
        public string Ffmpeg = "ffmpeg", Ffprobe = "ffprobe";
        public BackgroundJobKind Kind;
        public BackgroundOptions Options = new BackgroundOptions();
        public double PreviewTimeSeconds;
        public int PreviewMaximumWidth = 640;
        public VideoConversionFormat Format = VideoConversionFormat.PackedH264;
    }

    public sealed class BackgroundPreview
    {
        // Top-to-bottom RGBA8 rows, straight RGB, sRGB display code values.
        public byte[] OriginalRgba, ResultRgba;
        public int Width, Height;
        public double TimeSeconds;
    }

    public sealed class BackgroundResult
    {
        public BackgroundAnalysis Analysis;
        public BackgroundPreview Preview;
        public VideoConverter.Result Conversion;
        public int Width, Height;
        public double DurationSeconds, FramesPerSecond;
        public string Warning;
    }

    public interface IBackgroundJob : IDisposable
    {
        Task<BackgroundResult> Completion { get; }
        string Status { get; }
        void Cancel();
    }
}
