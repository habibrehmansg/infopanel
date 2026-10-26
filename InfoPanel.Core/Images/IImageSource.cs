namespace InfoPanel.Images
{
    /// <summary>
    /// Supplies decoded images to the renderer.
    /// </summary>
    public interface IImageSource
    {
        /// <summary>
        /// Returns the image for <paramref name="source"/> (a resolved file path), or null if it is not
        /// available yet, failed to load or is not supported; the renderer then draws a placeholder.
        /// Must not block the render thread. <paramref name="persistent"/> keeps the image cached
        /// while unused (gauge frames).
        /// </summary>
        IRenderImage? Get(string source, bool persistent);
    }
}
