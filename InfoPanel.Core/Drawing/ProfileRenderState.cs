using InfoPanel.Models;
using InfoPanel.Sensors;
using InfoPanel.Utils;
using System;
using System.Collections.Generic;
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
        private TickInputs? _inputs;

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

        /// <summary>
        /// The sensors and history of <paramref name="tick"/>, frozen like its time: the first read of a key at a tick
        /// is what every later <see cref="ProfileRenderer"/> call at that tick (another scale, the bounds, a thumbnail)
        /// reads, so one tick's frames and bounds all show the same readings. Replaced on the next tick (or for other
        /// sources), so it holds at most the keys one tick read.
        /// </summary>
        internal TickInputs GetTickInputs(long tick, ISensorReader sensors, ISensorHistory history)
        {
            lock (_timeLock)
            {
                if (_inputs is { } inputs && inputs.Tick == tick && inputs.Uses(sensors, history))
                {
                    return inputs;
                }

                return _inputs = new TickInputs(tick, sensors, history);
            }
        }
    }

    /// <summary>One tick's memo of sensor readings and history (see <see cref="ProfileRenderState.GetTickInputs"/>).</summary>
    internal sealed class TickInputs(long tick, ISensorReader sensors, ISensorHistory history) : ISensorReader, ISensorHistory
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<SensorKey, (bool Found, SensorReading Reading)> _readings = [];
        private readonly Dictionary<SensorKey, IReadOnlyList<double>> _samples = [];

        public long Tick { get; } = tick;

        public bool Uses(ISensorReader sensorReader, ISensorHistory sensorHistory) =>
            ReferenceEquals(sensors, sensorReader) && ReferenceEquals(history, sensorHistory);

        public bool TryRead(SensorKey key, out SensorReading reading)
        {
            lock (_lock)
            {
                if (!_readings.TryGetValue(key, out var memo))
                {
                    var found = sensors.TryRead(key, out var read);
                    memo = (found, read);
                    _readings[key] = memo;
                }

                reading = memo.Reading;
                return memo.Found;
            }
        }

        public IReadOnlyList<double> GetSamples(SensorKey key)
        {
            lock (_lock)
            {
                if (!_samples.TryGetValue(key, out var samples))
                {
                    samples = history.GetSamples(key);
                    _samples[key] = samples;
                }

                return samples;
            }
        }
    }
}
