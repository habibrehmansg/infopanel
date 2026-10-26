namespace InfoPanel.Core.Tests.Editing;

/// <summary>A clock that moves only on <see cref="Advance"/> (no timers).</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => Start.AddTicks(GetTimestamp());

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public void Advance(int milliseconds) => Advance(TimeSpan.FromMilliseconds(milliseconds));
}
