using System;

namespace InfoPanel.Drawing
{
    /// <summary>
    /// Gauge frame animation state, kept in <see cref="RenderContext.State"/>.
    /// </summary>
    internal sealed class GaugeState
    {
        public long LastTick = long.MinValue;
        public double CurrentImageIndex;
        public TimeSpan? LastUpdate;
    }

    /// <summary>
    /// Chart smoothing and auto-range state, kept in <see cref="RenderContext.State"/>.
    /// The Prev* values are the state at the start of the current tick, so drawing the same
    /// chart several times in one tick gives the same result and advances it only once.
    /// </summary>
    internal sealed class ChartState
    {
        public long LastTick = long.MinValue;

        public double PrevSmoothedValue;
        public double SmoothedValue;

        public (double min, double max)? PrevAutoRange;
        public (double min, double max)? AutoRange;

        public void BeginTick(long tick)
        {
            if (LastTick == tick)
            {
                return;
            }

            LastTick = tick;
            PrevSmoothedValue = SmoothedValue;
            PrevAutoRange = AutoRange;
        }
    }
}
