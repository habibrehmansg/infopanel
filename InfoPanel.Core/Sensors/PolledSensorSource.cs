using InfoPanel.Enums;
using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors;

public sealed class PolledSensorSourceOptions
{
    /// <summary>Time between polls.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A sensor whose last good sample is older than this (or whose latest read failed) is stale.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>A sensor whose last good sample is older than this is unavailable.</summary>
    public TimeSpan UnavailableAfter { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// An <see cref="ISensorSource"/> over an <see cref="ISensorSampler"/>: <see cref="StartAsync"/> discovers the sensors
/// (off the caller's thread) and polls them every <see cref="PolledSensorSourceOptions.Interval"/> on the thread pool,
/// keeping now/min/max/avg per sensor. Keys are <c>plugin:{KeyPrefix}{definition id}</c>; the source handles every
/// key under its prefix (unknown ones are unavailable). Status per key (<see cref="ISensorStatusSource"/>):
/// <list type="bullet">
/// <item><see cref="SensorStatus.PermissionDenied"/> while the latest read was refused;</item>
/// <item><see cref="SensorStatus.Unavailable"/> with no good sample yet, or the last one older than
/// <see cref="PolledSensorSourceOptions.UnavailableAfter"/>;</item>
/// <item><see cref="SensorStatus.Stale"/> when the latest read failed or the last good sample is older than
/// <see cref="PolledSensorSourceOptions.StaleAfter"/> (the last value is still read);</item>
/// <item><see cref="SensorStatus.Live"/> otherwise.</item>
/// </list>
/// Reads take a short lock and allocate nothing.
/// </summary>
public sealed class PolledSensorSource : ISensorSource, ISensorStatusSource, IDisposable
{
    private struct Slot
    {
        public double Now;
        public double Min;
        public double Max;
        public double Avg;
        public long Count;
        public long LastGood;
        public SampleOutcome Latest;
    }

    private readonly Func<ISensorSampler> _createSampler;
    private readonly PolledSensorSourceOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private ISensorSampler? _sampler;
    private Dictionary<SensorKey, int> _index = [];
    private SensorInfo[] _sensors = [];
    private string[] _units = [];
    private Slot[] _slots = [];
    private SensorSample[] _buffer = [];
    private int[] _slotOf = [];
    private long _lastPoll;
    private bool _polled;
    private volatile bool _disposed;
    // Orders Dispose against a start publishing its loop: either the start sees the flag, or Dispose sees the loop.
    private readonly Lock _disposeLock = new();
    private bool _failedOnce;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    /// <param name="id">The source id (<c>infopanel.macos</c>); keys are under <c>/{id}/</c>.</param>
    /// <param name="createSampler">Creates the sampler on <see cref="StartAsync"/>.</param>
    public PolledSensorSource(string id, Func<ISensorSampler> createSampler, PolledSensorSourceOptions? options = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        Id = id;
        KeyPrefix = "/" + id + "/";
        _createSampler = createSampler;
        _options = options ?? new PolledSensorSourceOptions();
        _time = time ?? TimeProvider.System;
        _log = Log.ForContext<PolledSensorSource>();
    }

    public string Id { get; }

    public string KeyPrefix { get; }

    public PolledSensorSourceOptions Options => _options;

    /// <summary>Polls taken since start. For tests and diagnostics.</summary>
    public long Polls { get; private set; }

    public SensorKey KeyOf(string definitionId) => SensorKey.Plugin(KeyPrefix + definitionId);

    public bool Handles(SensorKey key) =>
        key.Type == SensorType.Plugin && key.Id.StartsWith(KeyPrefix, StringComparison.Ordinal);

    /// <summary>The discovered sensors (empty before <see cref="StartAsync"/>).</summary>
    public IReadOnlyList<SensorInfo> GetSensors() => Volatile.Read(ref _sensors);

    public bool TryRead(SensorKey key, out SensorReading reading)
    {
        var state = ReadState(key);
        reading = state.Reading;
        return state.HasReading;
    }

    public SensorStatus GetStatus(SensorKey key) => ReadState(key).Status;

    /// <summary>Every sensor a sampler reads is a number, so a refused one is too.</summary>
    public SensorState ReadState(SensorKey key)
    {
        lock (_lock)
        {
            if (!_index.TryGetValue(key, out var i))
                return SensorState.Without(SensorStatus.Unavailable);

            ref var slot = ref _slots[i];
            var status = StatusOf(in slot, _time.GetTimestamp());
            return status is SensorStatus.Live or SensorStatus.Stale
                ? new SensorState(status, SensorKind.Number, new SensorReading(slot.Min, slot.Max, slot.Avg, slot.Now, _units[i]))
                : SensorState.Without(status);
        }
    }

    private SensorStatus StatusOf(in Slot slot, long now)
    {
        if (slot.Latest == SampleOutcome.PermissionDenied)
            return SensorStatus.PermissionDenied;
        if (slot.Count == 0)
            return SensorStatus.Unavailable;

        var age = _time.GetElapsedTime(slot.LastGood, now);
        if (age > _options.UnavailableAfter)
            return SensorStatus.Unavailable;
        if (slot.Latest == SampleOutcome.Failed || age > _options.StaleAfter)
            return SensorStatus.Stale;
        return SensorStatus.Live;
    }

    /// <summary>Whether the poll loop is running. For tests.</summary>
    internal bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Discovers the sensors and starts polling (a no-op when running). The loop closes the sampler when it ends.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A loop whose stop timed out may have ended since: reap it so this start runs a new one.
            if (_loop is { IsCompleted: true })
                ReapLoop();
            if (_loop != null)
                return;

            // Discovery does blocking OS calls; keep it off the caller's thread.
            await Task.Run(Open, ct).ConfigureAwait(false);
            lock (_disposeLock)
            {
                if (!_disposed)
                {
                    _loopCts = new CancellationTokenSource();
                    _loop = RunAsync(new PeriodicTimer(_options.Interval, _time), _loopCts.Token);
                    return;
                }
            }

            // Disposed while discovering: Dispose left the sampler to us.
            CloseSampler();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Stops the poll loop and waits for it to end (it closes the sampler). Start and stop are serialised; if
    /// <paramref name="ct"/> ends the wait first, the next start reaps the loop once it has ended.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loop == null)
                return;

            var loop = _loop;
            _loopCts!.Cancel();
            try
            {
                await loop.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                if (loop.IsCompleted)
                    ReapLoop();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void ReapLoop()
    {
        _loopCts?.Dispose();
        _loop = null;
        _loopCts = null;
    }

    /// <summary>
    /// Cancels the poll loop (which closes the sampler as it ends) and refuses later starts. Doesn't wait: a start in
    /// progress sees the flag after discovery and closes the sampler itself.
    /// </summary>
    public void Dispose()
    {
        lock (_disposeLock)
        {
            _disposed = true;
            try
            {
                _loopCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        // Only when no start is running and no loop owns the sampler.
        if (_lifecycle.Wait(0))
        {
            try
            {
                if (_loop == null)
                    CloseSampler();
            }
            finally
            {
                _lifecycle.Release();
            }
        }
    }

    /// <summary>Creates the sampler and discovers its sensors, then takes the first poll. Called by <see cref="StartAsync"/>; tests call it directly.</summary>
    internal void Open()
    {
        if (_sampler != null)
            return;

        IReadOnlyList<SensorDefinition> definitions;
        try
        {
            _sampler = _createSampler();
            definitions = _sampler.Discover();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Sensor source {Source} found no sensors: discovery failed", Id);
            CloseSampler();
            definitions = [];
        }

        // One slot per distinct key; map[i] is definition i's slot (-1 for a duplicate, whose samples are ignored).
        var index = new Dictionary<SensorKey, int>(definitions.Count);
        var infos = new List<SensorInfo>(definitions.Count);
        var map = new int[definitions.Count];
        for (var i = 0; i < definitions.Count; i++)
        {
            var d = definitions[i];
            var key = KeyOf(d.Id);
            if (index.TryAdd(key, infos.Count))
            {
                map[i] = infos.Count;
                infos.Add(new SensorInfo(key, Id, d.Group, d.Name, d.Unit));
            }
            else
            {
                map[i] = -1;
                _log.Warning("Sensor source {Source} skipped a duplicate sensor {Key}", Id, key);
            }
        }

        lock (_lock)
        {
            _index = index;
            _units = infos.Select(i => i.Unit).ToArray();
            _slots = new Slot[infos.Count];
            _buffer = new SensorSample[definitions.Count];
            _slotOf = map;
        }

        Volatile.Write(ref _sensors, infos.ToArray());
        _polled = false;
        Poll();
    }

    /// <summary>Takes one sample of every sensor. The poll loop calls this every interval; tests call it directly.</summary>
    internal void Poll()
    {
        var sampler = _sampler;
        if (sampler == null || _buffer.Length == 0)
            return;

        var now = _time.GetTimestamp();
        var elapsed = _polled ? _time.GetElapsedTime(_lastPoll, now) : TimeSpan.Zero;
        _lastPoll = now;
        _polled = true;
        Array.Clear(_buffer);
        try
        {
            sampler.Sample(_buffer, elapsed);
            _failedOnce = false;
        }
        catch (Exception ex)
        {
            // A sampler bug fails this round; the sensors go stale, then unavailable, and the next round retries.
            if (!_failedOnce)
                _log.Warning(ex, "Sensor source {Source}: a poll failed", Id);
            _failedOnce = true;
            for (var i = 0; i < _buffer.Length; i++)
                _buffer[i].Outcome = SampleOutcome.Failed;
        }

        var sampled = _time.GetTimestamp();
        lock (_lock)
        {
            for (var i = 0; i < _buffer.Length; i++)
            {
                var s = _slotOf[i];
                if (s < 0)
                    continue;

                ref var slot = ref _slots[s];
                var sample = _buffer[i];
                switch (sample.Outcome)
                {
                    case SampleOutcome.Ok when double.IsFinite(sample.Value):
                        slot.Count++;
                        slot.Now = sample.Value;
                        if (slot.Count == 1)
                        {
                            slot.Min = slot.Max = slot.Avg = sample.Value;
                        }
                        else
                        {
                            slot.Min = Math.Min(slot.Min, sample.Value);
                            slot.Max = Math.Max(slot.Max, sample.Value);
                            slot.Avg += (sample.Value - slot.Avg) / slot.Count;
                        }

                        slot.LastGood = sampled;
                        slot.Latest = SampleOutcome.Ok;
                        break;
                    case SampleOutcome.Ok:
                    case SampleOutcome.Failed:
                        slot.Latest = SampleOutcome.Failed;
                        break;
                    case SampleOutcome.PermissionDenied:
                        slot.Latest = SampleOutcome.PermissionDenied;
                        break;
                }
            }
        }

        Polls++;
    }

    private async Task RunAsync(PeriodicTimer timer, CancellationToken ct)
    {
        using var _ = timer;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                Poll();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            CloseSampler();
        }
    }

    private void CloseSampler()
    {
        var sampler = Interlocked.Exchange(ref _sampler, null);
        try
        {
            sampler?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Sensor source {Source}: closing the sampler failed", Id);
        }
    }
}
