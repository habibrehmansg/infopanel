using InfoPanel.Plugins.Ipc;

namespace InfoPanel.Plugins.Host
{
    /// <summary>
    /// Everything the host sends the engine, held to one budget (<see cref="IpcLimits"/>: bytes and messages, each a rate
    /// with a burst) so that what it sends in any <see cref="IpcLimits.StallSeconds"/> fits what the engine holds while it
    /// handles nothing; within it, the messages of readings to a budget of their own. A message is counted by the host's
    /// estimate of its bytes, an upper bound. A message of readings, or a picture (<see cref="SensorSnapshotManager"/>),
    /// waits for room and holds it through its send; a report that finds none is dropped. Thread-safe.
    /// </summary>
    public sealed class WireBudget
    {
        private readonly object _lock = new();
        private readonly RateBucket _bytes;
        private readonly RateBucket _calls;
        private readonly RateBucket _readings;

        public WireBudget(TimeProvider? time = null)
        {
            time ??= TimeProvider.System;
            _bytes = new RateBucket(IpcLimits.WireBurstBytes, IpcLimits.WireBytesPerSecond, time);
            _calls = new RateBucket(IpcLimits.CallsBurst, IpcLimits.CallsPerSecond, time);
            _readings = new RateBucket(IpcLimits.ReadingsBurstBytes, IpcLimits.ReadingsBytesPerSecond, time);
        }

        /// <summary>Takes one message of <paramref name="bytes"/> when the wire has room for it now; else false, and it waits or is dropped.</summary>
        public bool TryTake(long bytes)
        {
            lock (_lock)
            {
                if (!_bytes.CanTake(bytes) || !_calls.CanTake(1))
                    return false;
                _bytes.Take(bytes);
                _calls.Take(1);
                return true;
            }
        }

        /// <summary>Takes a message of readings of <paramref name="bytes"/> when both the wire and the readings' budget have room for it now.</summary>
        public bool TryTakeReadings(long bytes)
        {
            lock (_lock)
            {
                if (!_readings.CanTake(bytes) || !_bytes.CanTake(bytes) || !_calls.CanTake(1))
                    return false;
                _readings.Take(bytes);
                _bytes.Take(bytes);
                _calls.Take(1);
                return true;
            }
        }

        /// <summary>
        /// Takes one message of <paramref name="bytes"/> whether or not there is room (the catalogue, which the host must
        /// send once, first: the budget is full then, and the message within its burst).
        /// </summary>
        public void Take(long bytes)
        {
            lock (_lock)
            {
                _bytes.CanTake(bytes);
                _calls.CanTake(1);
                _bytes.Take(bytes);
                _calls.Take(1);
            }
        }

        /// <summary>A send that took room has ended: nothing filled the budget while it lasted (a stalled write holds its room).</summary>
        public void Hold()
        {
            lock (_lock)
            {
                _bytes.Hold();
                _calls.Hold();
                _readings.Hold();
            }
        }
    }
}
