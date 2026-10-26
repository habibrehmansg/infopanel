using SkiaSharp;
using System;

namespace InfoPanel.Images
{
    /// <summary>
    /// A decoded image the renderer can draw: a raster (possibly animated) or a vector picture.
    /// </summary>
    public interface IRenderImage
    {
        int Width { get; }

        int Height { get; }

        bool IsVector { get; }

        /// <summary>
        /// Calls <paramref name="draw"/> with the frame at <paramref name="elapsed"/>, resized to
        /// <paramref name="width"/>×<paramref name="height"/>. The image is only valid during the call.
        /// May skip the call if the frame is unavailable.
        /// </summary>
        void AccessRaster(int width, int height, TimeSpan elapsed, Action<SKImage> draw);

        /// <summary>
        /// Calls <paramref name="draw"/> with the vector picture. May skip the call if unavailable.
        /// </summary>
        void AccessVector(Action<SKPicture> draw);
    }
}
