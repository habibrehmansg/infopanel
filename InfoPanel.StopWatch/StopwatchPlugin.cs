using System.Globalization;
using InfoPanel.Plugins;

namespace InfoPanel.StopWatch;

public class StopwatchPlugin : BasePlugin
{
    private readonly object _sync = new();

    private bool _running;
    private TimeSpan _accumulated;
    private DateTime _segmentStartUtc;

    private readonly PluginText _elapsedFormatted = new("elapsed", "Elapsed", "00:00:00.00");
    private readonly PluginText _state = new("state", "State", "Stopped");

    private readonly PluginSensor _totalSeconds = new("seconds_total", "Elapsed (seconds)", 0f, "s");
    private readonly PluginSensor _hours = new("hours", "Elapsed hours", 0f, "h");
    private readonly PluginSensor _minutes = new("minutes", "Elapsed minute (0–59)", 0f, "m");
    private readonly PluginSensor _seconds = new("seconds", "Elapsed second (0–59)", 0f, "s");

    public StopwatchPlugin()
        : base("infopanel-stopwatch", "Stopwatch", "Elapsed time, state, and resettable timer for overlays.")
    {
    }

    public override TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(100);

    public override void Initialize()
    {
    }

    public override void Load(List<IPluginContainer> containers)
    {
        var container = new PluginContainer("stopwatch", "Stopwatch");
        container.Entries.Add(_elapsedFormatted);
        container.Entries.Add(_state);
        container.Entries.Add(_totalSeconds);
        container.Entries.Add(_hours);
        container.Entries.Add(_minutes);
        container.Entries.Add(_seconds);
        containers.Add(container);
    }

    public override Task UpdateAsync(CancellationToken cancellationToken)
    {
        TimeSpan elapsed;
        bool running;
        lock (_sync)
        {
            elapsed = _running ? _accumulated + (DateTime.UtcNow - _segmentStartUtc) : _accumulated;
            running = _running;
        }

        ApplyDisplay(elapsed, running);
        return Task.CompletedTask;
    }

    public override void Update()
    {
        throw new NotSupportedException();
    }

    public override void Close()
    {
    }

    [PluginAction("Start")]
    public void Start()
    {
        lock (_sync)
        {
            if (_running)
                return;
            _segmentStartUtc = DateTime.UtcNow;
            _running = true;
        }
    }

    [PluginAction("Stop")]
    public void Stop()
    {
        lock (_sync)
        {
            if (!_running)
                return;
            _accumulated += DateTime.UtcNow - _segmentStartUtc;
            _running = false;
        }
    }

    [PluginAction("Reset")]
    public void Reset()
    {
        lock (_sync)
        {
            _running = false;
            _accumulated = TimeSpan.Zero;
        }
    }

    private void ApplyDisplay(TimeSpan elapsed, bool running)
    {
        var totalHours = (int)Math.Floor(elapsed.TotalHours);
        var minuteComponent = (int)(elapsed.TotalMinutes % 60);
        var secondComponent = (int)(elapsed.TotalSeconds % 60);
        var centiseconds = elapsed.Milliseconds / 10;

        _elapsedFormatted.Value = string.Format(
            CultureInfo.InvariantCulture,
            "{0:D2}:{1:D2}:{2:D2}.{3:D2}",
            totalHours,
            minuteComponent,
            secondComponent,
            centiseconds);
        _state.Value = running ? "Running" : "Stopped";

        _totalSeconds.Value = (float)elapsed.TotalSeconds;
        _hours.Value = totalHours;
        _minutes.Value = minuteComponent;
        _seconds.Value = secondComponent;
    }
}
