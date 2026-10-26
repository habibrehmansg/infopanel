using InfoPanel.Images;
using InfoPanel.Sensors;
using InfoPanel.Utils;
using System;

namespace InfoPanel.Drawing
{
    /// <summary>
    /// Everything a render pass reads from outside the profile: sensors, history, images, assets,
    /// per-item animation state and time. Rendering never reads clocks or globals directly.
    /// </summary>
    public sealed class RenderContext
    {
        public required ISensorReader Sensors;
        public required ISensorHistory History;
        public required IImageSource Images;
        public required IAssetLocator Assets;
        public required ItemStateStore State;

        /// <summary>Frame counter. Animation state advances at most once per tick.</summary>
        public long Tick;

        /// <summary>Wall-clock time shown by clocks and calendars.</summary>
        public DateTimeOffset Now;

        /// <summary>Monotonic time since rendering started; drives marquees, gradients and animated images.</summary>
        public TimeSpan Elapsed;

        public RenderOptions Options = RenderOptions.Default;

        public FpsCounter? Fps;
    }
}
