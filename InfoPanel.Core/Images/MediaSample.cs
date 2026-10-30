using System;
using System.Threading;
using SkiaSharp;

namespace InfoPanel.Images
{
    /// <summary>
    /// One decoded media frame that a holder can keep: a raster image or a vector picture, reference counted. Unlike
    /// <see cref="IRenderImage"/>, whose frame is only valid inside a callback, a sample stays valid for as long as its
    /// holder keeps a reference, so a tick snapshot can own the frames it draws at every scale and on every output.
    /// Every <see cref="Retain"/> (and the creator's reference) is matched by one <see cref="Release"/>; the frame is
    /// disposed when the last reference goes. Thread-safe.
    /// </summary>
    public sealed class MediaSample
    {
        private int _references = 1;

        private MediaSample(SKImage? image, SKPicture? picture, float width, float height, long bytes)
        {
            Image = image;
            Picture = picture;
            Width = width;
            Height = height;
            Bytes = bytes;
        }

        /// <summary>A raster frame; the sample takes ownership of <paramref name="image"/>.</summary>
        public static MediaSample FromImage(SKImage image) =>
            new(image, null, image.Width, image.Height, (long)image.Width * image.Height * 4);

        /// <summary>
        /// A raster frame decoded at its own pixel size for media whose natural size is <paramref name="width"/> ×
        /// <paramref name="height"/> (a video decoded smaller than its own size): laid out at the natural size, drawn
        /// from the image's pixels. The sample takes ownership of <paramref name="image"/>.
        /// </summary>
        public static MediaSample FromImage(SKImage image, float width, float height) =>
            new(image, null, width, height, (long)image.Width * image.Height * 4);

        /// <summary>A vector picture drawn at <paramref name="width"/> × <paramref name="height"/> natural size; the sample takes ownership.</summary>
        public static MediaSample FromPicture(SKPicture picture, float width, float height) =>
            new(null, picture, width, height, Math.Max(1024, picture.ApproximateBytesUsed));

        public SKImage? Image { get; }

        public SKPicture? Picture { get; }

        /// <summary>The natural size in pixels.</summary>
        public float Width { get; }

        public float Height { get; }

        /// <summary>What the decoded frame costs in memory, for cache budgets.</summary>
        public long Bytes { get; }

        public bool IsReleased => Volatile.Read(ref _references) == 0;

        /// <summary>Another reference to this sample, or null if it has already been released for good.</summary>
        public MediaSample? Retain()
        {
            while (true)
            {
                var current = Volatile.Read(ref _references);
                if (current == 0) return null;
                if (Interlocked.CompareExchange(ref _references, current + 1, current) == current) return this;
            }
        }

        public void Release()
        {
            var left = Interlocked.Decrement(ref _references);
            if (left == 0)
            {
                Image?.Dispose();
                Picture?.Dispose();
            }
            else if (left < 0)
            {
                Interlocked.Exchange(ref _references, 0);
            }
        }

        /// <summary>
        /// Draws the sample into <paramref name="dest"/>: a raster with <paramref name="sampling"/>, a picture scaled
        /// from its natural size. A released sample draws nothing.
        /// </summary>
        public void Draw(SKCanvas canvas, SKRect dest, SKSamplingOptions sampling, SKPaint? paint)
        {
            if (IsReleased) return;
            if (Image is { } image)
            {
                canvas.DrawImage(image, dest, sampling, paint);
            }
            else if (Picture is { } picture && Width > 0 && Height > 0)
            {
                var m = SKMatrix.CreateScaleTranslation(dest.Width / Width, dest.Height / Height, dest.Left, dest.Top);
                canvas.DrawPicture(picture, in m, paint);
            }
        }
    }
}
