using InfoPanel.Models;
using InfoPanel.Scenes;
using InfoPanel.Service;
using InfoPanel.Settings;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors.Weather;

/// <summary>What the weather source is made with, beside the service; each has a default.</summary>
public sealed class WeatherSourceOptions
{
    /// <summary>The PC's regional format: its region chooses the units under <c>auto</c>, and its culture the time format of sunrise and sunset. <see cref="PcRegionalFormat.Current"/> by default.</summary>
    public RegionalFormat? Format { get; init; }

    /// <summary>A random number from 0 to 1 for each answer's jitter; <see cref="Random.Shared"/> by default.</summary>
    public Func<double>? Jitter { get; init; }

    /// <summary>The polled base's ages; <see cref="WeatherSource.DefaultOptions"/> by default.</summary>
    public PolledSensorSourceOptions? Polled { get; init; }

    public ILogger? Log { get; init; }
}

/// <summary>
/// The weather source, <c>infopanel.weather</c> ("Weather", docs/2.0/weather.md): the current weather at the place the
/// configured service finds for this computer's network address, or at the place set, from the service's answer, which
/// <see cref="WeatherFetcher"/> asks for on a task of its own (with the credential a client provides, through
/// <see cref="ServiceClient"/>'s rules) and holds. A <see cref="PolledSensorSource"/> polls the held answer every
/// second (<see cref="WeatherSampler"/>; no request): its readings (<see cref="Entries"/>, keys
/// <c>plugin:/infopanel.weather/{id}</c>) are all listed from the start, unavailable until the first answer; live while
/// the answer is at most <see cref="LiveFor"/> old and the service didn't call it stale, then stale (the last values
/// kept) until it is <see cref="KeepFor"/> old, then unavailable. An answer the service calls stale keeps the values
/// shown and their age, and reads them stale; a first one is shown stale. A value the service doesn't give reads unavailable.
/// <para>
/// Numbers are metric or imperial as <c>sources.weather.units</c> says (<c>auto</c>: the PC's region's); a change of
/// units applies to the answer held at once (no request): the readings whose unit changes start afresh, their old
/// statistics dropped. Off (<c>sources.weather.enabled</c>), the source asks nothing and its readings are unavailable.
/// The engine never answers the 1.x Weather plugin's keys; a 1.x import binds them to these (<see cref="RetiredPlugins"/>).
/// </para>
/// </summary>
public sealed class WeatherSource : ISensorSource, ISensorStatusSource, ISensorSourceState, IDisposable
{
    public const string SourceId = "infopanel.weather";

    /// <summary>The source's name, and its readings' group.</summary>
    public const string Name = "Weather";

    /// <summary>The poll's interval: the held answer is read again every second, so a change of units shows at once.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>An answer this old, or older, reads stale: a forecast hour older than that misleads.</summary>
    public static readonly TimeSpan LiveFor = TimeSpan.FromMinutes(90);

    /// <summary>An answer older than this reads unavailable.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromHours(6);

    /// <summary>The polled base's ages: the sampler decides freshness from the answer's age; these only bound a poll that stops.</summary>
    public static PolledSensorSourceOptions DefaultOptions => new()
    {
        Interval = Interval,
        StaleAfter = TimeSpan.FromMinutes(1),
        UnavailableAfter = KeepFor,
    };

    /// <summary>One reading: its id, name and kind, and its unit in metric and in imperial (empty for a text).</summary>
    public sealed record Entry(string Id, string Name, SensorKind Kind, string Metric = "", string Imperial = "");

    /// <summary>The readings, in the order the source lists them.</summary>
    public static IReadOnlyList<Entry> Entries { get; } =
    [
        new("place", "Place", SensorKind.Text),
        new("condition", "Condition", SensorKind.Text),
        new("symbol", "Symbol", SensorKind.Text),
        new("temperature", "Temperature", SensorKind.Number, "°C", "°F"),
        new("feels_like", "Feels like", SensorKind.Number, "°C", "°F"),
        new("high", "High (next 24 h)", SensorKind.Number, "°C", "°F"),
        new("low", "Low (next 24 h)", SensorKind.Number, "°C", "°F"),
        new("dew_point", "Dew point", SensorKind.Number, "°C", "°F"),
        new("humidity", "Humidity", SensorKind.Number, "%", "%"),
        new("cloud_cover", "Cloud cover", SensorKind.Number, "%", "%"),
        new("pressure", "Pressure (sea level)", SensorKind.Number, "hPa", "inHg"),
        new("wind_speed", "Wind speed", SensorKind.Number, "m/s", "mph"),
        new("wind_gust", "Wind gust", SensorKind.Number, "m/s", "mph"),
        new("wind_direction", "Wind direction (from)", SensorKind.Number, "°", "°"),
        new("precipitation", "Precipitation (next hour)", SensorKind.Number, "mm", "in"),
        new("uv_index", "UV index (clear sky)", SensorKind.Number),
        new("sunrise", "Sunrise", SensorKind.Text),
        new("sunset", "Sunset", SensorKind.Text),
        new("attribution", "Credit", SensorKind.Text),
    ];

    private static readonly SensorKeySpace Space = SensorKeySpace.Plugin(SourceId);

    /// <summary>The key of the reading <paramref name="id"/>: <c>plugin:/infopanel.weather/{id}</c>.</summary>
    public static SensorKey KeyOf(string id) => Space.KeyOf(id);

    /// <summary>The entry <paramref name="id"/>, or null.</summary>
    public static Entry? EntryOf(string id)
    {
        foreach (var entry in Entries)
        {
            if (entry.Id == id)
                return entry;
        }

        return null;
    }

    private readonly PolledSensorSource _polled;
    private readonly WeatherFetcher _fetcher;
    private readonly bool _regionImperial;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private volatile int _units = (int)WeatherUnits.Auto;
    private Action? _unfollow;

    /// <param name="client">How the source asks the configured service; null: it has none (<c>noService</c>).</param>
    /// <param name="credentials">The service credential's state, followed so a new one asks at once.</param>
    public WeatherSource(ServiceClient? client, ServiceCredentials? credentials, TimeProvider? time = null, WeatherSourceOptions? options = null)
    {
        options ??= new WeatherSourceOptions();
        var clock = time ?? TimeProvider.System;
        var format = options.Format ?? PcRegionalFormat.Current;
        Culture = format.Culture;
        _regionImperial = UsesImperial(format.Culture);
        _fetcher = new WeatherFetcher(client, credentials, clock, options.Jitter, options.Log);
        _polled = new PolledSensorSource(SourceId, () => new WeatherSampler(this, clock), options.Polled ?? DefaultOptions, clock, log: options.Log);
    }

    /// <summary>
    /// The source over the engine's configured service (<paramref name="client"/>; null: none); with
    /// <paramref name="settings"/>, it follows their <c>sources.weather</c>.
    /// </summary>
    public static WeatherSource CreateSystem(ServiceClient? client, ServiceCredentials? credentials, TimeProvider? time, RegionalFormat? format, SettingsStore? settings)
    {
        var source = new WeatherSource(client, credentials, time, new WeatherSourceOptions { Format = format });
        return settings == null ? source : source.FollowSettings(settings);
    }

    public string Id => SourceId;

    /// <summary>The culture sunrise and sunset are written in (its short time pattern).</summary>
    internal CultureInfo Culture { get; }

    /// <summary>Whether the readings are imperial now: the setting, or under <c>auto</c> the PC's region.</summary>
    public bool Imperial => (WeatherUnits)_units switch
    {
        WeatherUnits.Imperial => true,
        WeatherUnits.Metric => false,
        _ => _regionImperial,
    };

    /// <summary>Whether a culture's region uses imperial units (the United States, Liberia, Myanmar); a culture without a region is metric.</summary>
    public static bool UsesImperial(CultureInfo culture)
    {
        if (culture.IsNeutralCulture || string.IsNullOrEmpty(culture.Name))
            return false;
        try
        {
            return !new RegionInfo(culture.Name).IsMetric;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The task that asks the service. For tests.</summary>
    internal WeatherFetcher Fetcher => _fetcher;

    /// <summary>The polled source underneath. For tests.</summary>
    internal PolledSensorSource Polled => _polled;

    /// <summary>Applies the settings at once: on or off, the units (the answer held is read in them from the next poll) and the place.</summary>
    public void Apply(WeatherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _units = (int)settings.Units;
        _fetcher.Configure(settings.Enabled, settings.Place);
    }

    /// <summary>Follows <paramref name="settings"/>' <c>sources.weather</c>: applied now, and at once on every change, until the source is disposed.</summary>
    public WeatherSource FollowSettings(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        void Follow(SettingsSnapshot snapshot) => Apply(snapshot.Settings.Weather);
        settings.Changed += Follow;
        _unfollow = () => settings.Changed -= Follow;
        Apply(settings.Current.Settings.Weather);
        return this;
    }

    /// <summary>
    /// <see cref="SourceState.Starting"/> until the readings are listed and the first request has come back; then what the
    /// fetch task found (<see cref="WeatherFetcher.State"/>).
    /// </summary>
    public SourceState State => _polled.State.Kind == SourceStateKind.Starting && _fetcher.State.Kind != SourceStateKind.Off
        ? SourceState.Starting
        : _fetcher.State;

    public bool Handles(SensorKey key) => _polled.Handles(key);

    public IReadOnlyList<SensorInfo> GetSensors() => _polled.GetSensors();

    public bool TryRead(SensorKey key, out SensorReading reading) => _polled.TryRead(key, out reading);

    public SensorState ReadState(SensorKey key) => _polled.ReadState(key);

    public SensorStatus GetStatus(SensorKey key) => _polled.GetStatus(key);

    /// <summary>Lists the readings and starts polling them, then starts the fetch task.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _polled.StartAsync(ct).ConfigureAwait(false);
            _fetcher.Start();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Stops the fetch task (a request under way cancelled) and the polling, and waits for both.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _fetcher.StopAsync(ct).ConfigureAwait(false);
            await _polled.StopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Dispose()
    {
        _unfollow?.Invoke();
        _fetcher.Dispose();
        _polled.Dispose();
    }

    /// <summary>°F from °C.</summary>
    public static double Fahrenheit(double celsius) => celsius * 9 / 5 + 32;

    /// <summary>Miles an hour from metres a second.</summary>
    public static double MilesPerHour(double metresPerSecond) => metresPerSecond * 2.236936;

    /// <summary>Inches of mercury from hectopascals.</summary>
    public static double InchesOfMercury(double hectopascals) => hectopascals * 0.0295300;

    /// <summary>Inches from millimetres.</summary>
    public static double Inches(double millimetres) => millimetres / 25.4;
}

/// <summary>
/// Reads the weather source's held answer into its readings (<see cref="PolledSensorSource"/>'s sampler): the catalogue
/// is fixed but for its units, which follow <see cref="WeatherSource.Imperial"/>. When they change, a round still reads in
/// the catalogue's units, then the catalogue changes and the readings whose unit changed start afresh (withdrawn), so a
/// value is never read under another unit than its own. The values of an answer are worked out once per answer and
/// units, and handed back as the same objects while they stand. Stale ones are given every round as values kept from
/// before (<see cref="SensorSample.MarkStale"/>), so they read stale with no live reading first: a first answer the
/// service calls stale, and the kept values in their new units after a switch.
/// </summary>
internal sealed class WeatherSampler(WeatherSource source, TimeProvider time) : ISensorSampler
{
    private bool _imperial;
    private long _version;
    private string[] _withdrawn = [];
    private (WeatherAnswer Answer, string? Place, bool Imperial, double?[] Numbers, string?[] Texts)? _values;

    public long CatalogueVersion => _version;

    public IReadOnlyList<SensorDefinition> Discover()
    {
        _imperial = source.Imperial;
        var definitions = new SensorDefinition[WeatherSource.Entries.Count];
        for (var i = 0; i < definitions.Length; i++)
        {
            var e = WeatherSource.Entries[i];
            definitions[i] = new SensorDefinition(e.Id, WeatherSource.Name, e.Name, _imperial ? e.Imperial : e.Metric, e.Kind);
        }

        return definitions;
    }

    public IReadOnlyCollection<string> TakeWithdrawn()
    {
        var withdrawn = _withdrawn;
        _withdrawn = [];
        return withdrawn;
    }

    public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
    {
        // Units changed: this round reads in the catalogue's, then the catalogue follows.
        if (source.Imperial != _imperial)
        {
            _version++;
            var changed = new List<string>();
            foreach (var e in WeatherSource.Entries)
            {
                if (e.Metric != e.Imperial)
                    changed.Add(e.Id);
            }

            _withdrawn = [.. changed];
        }

        var held = source.Fetcher.Latest;
        var age = held is null ? TimeSpan.MaxValue : time.GetElapsedTime(held.FetchedAt);
        if (held is null || age > WeatherSource.KeepFor)
        {
            for (var i = 0; i < samples.Length; i++)
                samples[i].SetUnavailable();
            return;
        }

        // Stale values are given as kept ones, so they read stale in the units of the moment, a first answer's too.
        var stale = held.Stale || age > WeatherSource.LiveFor;
        var (_, _, _, numbers, texts) = ValuesOf(held);
        for (var i = 0; i < samples.Length; i++)
        {
            if (WeatherSource.Entries[i].Kind == SensorKind.Text)
            {
                if (texts[i] is { } text)
                    samples[i].SetText(text);
                else
                    samples[i].SetUnavailable();
            }
            else if (numbers[i] is { } number)
            {
                samples[i].Set(number);
            }
            else
            {
                samples[i].SetUnavailable();
            }

            if (stale && samples[i].Outcome == SampleOutcome.Ok)
                samples[i].MarkStale();
        }
    }

    private (WeatherAnswer Answer, string? Place, bool Imperial, double?[] Numbers, string?[] Texts) ValuesOf(WeatherHeld held)
    {
        if (_values is { } values && ReferenceEquals(values.Answer, held.Answer) && values.Place == held.Place && values.Imperial == _imperial)
            return values;

        var a = held.Answer;
        var count = WeatherSource.Entries.Count;
        var numbers = new double?[count];
        var texts = new string?[count];
        for (var i = 0; i < count; i++)
        {
            var e = WeatherSource.Entries[i];
            switch (e.Id)
            {
                case "place": texts[i] = a.PlaceName ?? (a.PlaceFrom == "given" ? held.Place : null); break;
                case "condition": texts[i] = a.Condition; break;
                case "symbol": texts[i] = a.Symbol; break;
                case "temperature": numbers[i] = Temperature(a.Temperature); break;
                case "feels_like": numbers[i] = Temperature(a.FeelsLike); break;
                case "high": numbers[i] = Temperature(a.High); break;
                case "low": numbers[i] = Temperature(a.Low); break;
                case "dew_point": numbers[i] = Temperature(a.DewPoint); break;
                case "humidity": numbers[i] = a.Humidity; break;
                case "cloud_cover": numbers[i] = a.CloudCover; break;
                case "pressure": numbers[i] = a.Pressure is { } p && _imperial ? WeatherSource.InchesOfMercury(p) : a.Pressure; break;
                case "wind_speed": numbers[i] = Speed(a.WindSpeed); break;
                case "wind_gust": numbers[i] = Speed(a.WindGust); break;
                case "wind_direction": numbers[i] = a.WindDirection; break;
                case "precipitation": numbers[i] = a.Precipitation is { } mm && _imperial ? WeatherSource.Inches(mm) : a.Precipitation; break;
                case "uv_index": numbers[i] = a.UvIndex; break;
                case "sunrise": texts[i] = TimeOfDay(a.Sunrise); break;
                case "sunset": texts[i] = TimeOfDay(a.Sunset); break;
                case "attribution": texts[i] = a.Credit; break;
            }
        }

        _values = (held.Answer, held.Place, _imperial, numbers, texts);
        return _values.Value;
    }

    private double? Temperature(double? celsius) => celsius is { } c && _imperial ? WeatherSource.Fahrenheit(c) : celsius;

    private double? Speed(double? metresPerSecond) => metresPerSecond is { } v && _imperial ? WeatherSource.MilesPerHour(v) : metresPerSecond;

    /// <summary>A time as this PC shows a time of day: in its time zone, in its regional format's short time pattern.</summary>
    private string? TimeOfDay(DateTimeOffset? at) =>
        at is { } t ? TimeZoneInfo.ConvertTime(t, time.LocalTimeZone).ToString("t", source.Culture) : null;

    public void Dispose()
    {
    }
}
