using SkiaSharp;

namespace InfoPanel.Drawing
{
    /// <summary>
    /// Size of the "no image" placeholder drawn when an image or gauge has no image to show.
    /// Rendering and bounds measuring both use it, so the placeholder is where the item can be selected.
    /// </summary>
    public static class ImagePlaceholder
    {
        /// <summary>Width and height, in profile pixels, used for a dimension the item leaves at 0 (native size).</summary>
        public const float DefaultSize = 100;

        /// <summary>
        /// The placeholder size for an item whose evaluated size is <paramref name="size"/>:
        /// each dimension that is 0 (native size, unknown without the image) becomes <see cref="DefaultSize"/>.
        /// </summary>
        public static SKSize Resolve(SKSize size)
        {
            return new SKSize(
                size.Width > 0 ? size.Width : DefaultSize,
                size.Height > 0 ? size.Height : DefaultSize);
        }
    }
}
