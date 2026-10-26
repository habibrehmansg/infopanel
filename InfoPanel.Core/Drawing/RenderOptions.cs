namespace InfoPanel.Drawing
{
    /// <param name="Smoothing">Animate bar and donut values towards their target (off for goldens and thumbnails).</param>
    /// <param name="TargetFrameRate">Frame rate the smoothing is tuned for.</param>
    public sealed record RenderOptions(bool Smoothing = true, int TargetFrameRate = 15)
    {
        public static readonly RenderOptions Default = new();
    }
}
