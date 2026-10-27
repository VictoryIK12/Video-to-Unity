using System;

namespace VideoToUnity.Editor
{
    internal readonly struct UiRect
    {
        internal readonly float X, Y, Width, Height;
        internal UiRect(float x, float y, float width, float height)
        {
            X = x; Y = y; Width = width; Height = height;
        }
        internal bool Contains(float x, float y) =>
            x >= X && y >= Y && x <= X + Width && y <= Y + Height;
    }

    internal readonly struct UiPoint
    {
        internal readonly float X, Y;
        internal UiPoint(float x, float y) { X = x; Y = y; }
    }

    internal readonly struct NormalizedRegion
    {
        internal readonly float X, Y, Width, Height;
        internal NormalizedRegion(float x, float y, float width, float height)
        {
            X = x; Y = y; Width = width; Height = height;
        }
        internal bool IsValid => BackgroundRemovalUiModel.ValidateRegion(X, Y, Width, Height);
    }

    // Pure geometry and validation used by the editor window and its focused checks.
    internal static class BackgroundRemovalUiModel
    {
        internal const double ReliableConfidence = .6;
        internal const float MinimumDragSize = .01f;
        internal const int MaximumRegions = 64;

        internal static bool CanAddRegion(int count) => count >= 0 && count < MaximumRegions;

        // Preview buffers use top-to-bottom rows, unlike Texture2D's bottom-to-top rows.
        internal static bool TrySampleOriginal(byte[] rgba, int width, int height, UiRect image,
            float localX, float localY, out byte red, out byte green, out byte blue)
        {
            red = green = blue = 0;
            if (width <= 0 || height <= 0 || rgba == null ||
                (long)width * height > int.MaxValue / 4 ||
                (long)width * height * 4 != rgba.LongLength ||
                !TrySourcePoint(image, localX, localY, out UiPoint point)) return false;
            int x = Math.Min(width - 1, (int)(point.X * width));
            int y = Math.Min(height - 1, (int)(point.Y * height));
            int index = (y * width + x) * 4;
            red = rgba[index]; green = rgba[index + 1]; blue = rgba[index + 2];
            return true;
        }

        internal static bool SourceReady(string path) =>
            !string.IsNullOrWhiteSpace(path) &&
            string.Equals(System.IO.Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase) &&
            System.IO.File.Exists(path);
        internal static bool ToolReady(string path) => !string.IsNullOrWhiteSpace(path);
        internal static bool AutoDetectReliable(double confidence) =>
            !double.IsNaN(confidence) && !double.IsInfinity(confidence) && confidence >= ReliableConfidence;

        internal static bool ValidateRegion(float x, float y, float width, float height)
        {
            if (!Finite(x) || !Finite(y) || !Finite(width) || !Finite(height)) return false;
            return x >= 0 && y >= 0 && width > 0 && height > 0 &&
                x + width <= 1.000001f && y + height <= 1.000001f;
        }

        internal static NormalizedRegion ClampRegion(float x, float y, float width, float height)
        {
            width = Clamp(width, .001f, 1f);
            height = Clamp(height, .001f, 1f);
            x = Clamp(x, 0, 1f - width);
            y = Clamp(y, 0, 1f - height);
            return new NormalizedRegion(x, y, width, height);
        }

        internal static UiRect Fit(float sourceWidth, float sourceHeight, float containerWidth, float containerHeight)
        {
            if (!Positive(sourceWidth) || !Positive(sourceHeight) || !Positive(containerWidth) || !Positive(containerHeight))
                return new UiRect(0, 0, 0, 0);
            float scale = Math.Min(containerWidth / sourceWidth, containerHeight / sourceHeight);
            float width = sourceWidth * scale;
            float height = sourceHeight * scale;
            return new UiRect((containerWidth - width) * .5f, (containerHeight - height) * .5f, width, height);
        }

        internal static UiRect FitInContent(float sourceWidth, float sourceHeight, UiRect content)
        {
            var fitted = Fit(sourceWidth, sourceHeight, content.Width, content.Height);
            return new UiRect(content.X + fitted.X, content.Y + fitted.Y, fitted.Width, fitted.Height);
        }

        internal static UiRect RegionPixels(NormalizedRegion region, UiRect image)
        {
            if (!region.IsValid) return new UiRect(0, 0, 0, 0);
            return new UiRect(image.X + region.X * image.Width, image.Y + region.Y * image.Height,
                region.Width * image.Width, region.Height * image.Height);
        }

        internal static bool TrySourcePoint(UiRect image, float localX, float localY, out UiPoint point)
        {
            point = new UiPoint(0, 0);
            if (image.Width <= 0 || image.Height <= 0 || !image.Contains(localX, localY)) return false;
            point = new UiPoint((localX - image.X) / image.Width, (localY - image.Y) / image.Height);
            return true;
        }

        internal static UiPoint ClampSourcePoint(UiRect image, float localX, float localY)
        {
            if (image.Width <= 0 || image.Height <= 0) return new UiPoint(0, 0);
            return new UiPoint(Clamp((localX - image.X) / image.Width, 0, 1),
                Clamp((localY - image.Y) / image.Height, 0, 1));
        }

        internal static NormalizedRegion DragRegion(UiPoint start, UiPoint current)
        {
            float x = Math.Min(start.X, current.X);
            float y = Math.Min(start.Y, current.Y);
            return ClampRegion(x, y, Math.Abs(current.X - start.X), Math.Abs(current.Y - start.Y));
        }

        internal static NormalizedRegion MoveRegion(NormalizedRegion region, float deltaX, float deltaY)
        {
            return ClampRegion(region.X + deltaX, region.Y + deltaY, region.Width, region.Height);
        }

        internal static bool IsSmallDrag(NormalizedRegion region) =>
            region.Width < MinimumDragSize || region.Height < MinimumDragSize;

        internal static float Clamp(float value, float minimum, float maximum) =>
            float.IsNaN(value) ? minimum : Math.Max(minimum, Math.Min(maximum, value));

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Positive(float value) => Finite(value) && value > 0;
    }
}
