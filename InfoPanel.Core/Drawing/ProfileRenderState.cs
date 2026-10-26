using InfoPanel.Utils;
using System;
using System.Threading;

namespace InfoPanel.Drawing
{
    /// <summary>
    /// Render state of one profile: per-item animation state, the tick counter and the FPS counter.
    /// The render loop advances <see cref="Tick"/> once per frame; animations advance at most once per tick.
    /// </summary>
    public sealed class ProfileRenderState
    {
        public ItemStateStore Items { get; } = new();

        public long Tick;

        public FpsCounter Fps { get; } = new();

        private readonly Lock _timeLock = new();
        private long? _startTimestamp;
        private (long Tick, DateTimeOffset Now, TimeSpan Elapsed)? _frameTime;

        /// <summary>
        /// The time of <paramref name="tick"/>: the first <see cref="ProfileRenderer"/> call at a tick samples
        /// <paramref name="time"/> and every later call at the same tick gets the same values, so every frame,
        /// preview and measurement of one tick agrees. <see cref="RenderContext.Elapsed"/> counts from the
        /// timestamp of the first call that uses this state.
        /// </summary>
        internal (DateTimeOffset Now, TimeSpan Elapsed) GetFrameTime(long tick, TimeProvider time)
        {
            lock (_timeLock)
            {
                if (_frameTime is { } frame && frame.Tick == tick)
                {
                    return (frame.Now, frame.Elapsed);
                }

                var timestamp = time.GetTimestamp();
                _startTimestamp ??= timestamp;

                var now = time.GetLocalNow();
                var elapsed = time.GetElapsedTime(_startTimestamp.Value, timestamp);
                _frameTime = (tick, now, elapsed);
                return (now, elapsed);
            }
        }
    }
}
