using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Images;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Weather;
using InfoPanel.Service;
using InfoPanel.Settings;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors;

/// <summary>
/// The weather source (<see cref="WeatherSource"/>) against a fake service on a loopback port (<see cref="LocalHttpServer"/>)
/// and a fake clock: no credential, no request; an answer's readings, kinds and units, metric and imperial, and a switch
/// of units applied to the answer held at once; the schedule at the answer's expiry within 10 to 60 minutes, the back-off,
/// a 401 that stops until a new credential, a 429's Retry-After; the credential only to the service's origin and never on
/// a redirect, nor in the log; bounded and malformed answers; <c>--offline</c>; live for 90 minutes, stale to 6 hours,
/// then unavailable; the states and reasons.
/// </summary>
public sealed class WeatherSourceTests : IAsyncDisposable
{
    private const string Credential = "weather-test.credential~A1b2+/==";
    private const string Credit = "Weather data from the test service";
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDirectory _temp = new();
    private readonly List<LogEvent> _events = [];
    private readonly Serilog.ILogger _log;
    private readonly ServiceCredentials _credentials;
    private readonly LocalHttpServer _service;
    private readonly List<IDisposable> _owned = [];
    private string? _address;
    private TimeProvider _clock = TimeProvider.System;

    /// <summary>What the fake service answers next: status, body and headers; by default an answer that expires in 30 minutes.</summary>
    private Func<ServedRequest, (int Status, byte[] Body, string[] Headers)> _answer;

    public WeatherSourceTests()
    {
        _log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_events)).CreateLogger();
        _answer = _ => (200, Body(Answer()), []);
        _service = new LocalHttpServer((request, stream, _) =>
        {
            var (status, body, headers) = _answer(request);
            return LocalHttpServer.Answer(stream, status, body, status == 200 ? "application/json" : "application/problem+json", headers);
        });
        _address = $"http://127.0.0.1:{_service.Port}";
        _credentials = new ServiceCredentials(FileServiceCredentialStore.ForPrivateFile(_temp.Path, _log), () => _address, _log);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in _owned)
            owned.Dispose();
        await _service.DisposeAsync();
        _temp.Dispose();
    }

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    // ---- The fake service's answers ----

    /// <summary>An answer as the service gives it: <paramref name="change"/> edits the default one (Testville, 21.5 °C, …).</summary>
    private JsonObject Answer(Action<JsonObject>? change = null, TimeSpan? expiresIn = null)
    {
        var now = _clock.GetUtcNow();
        var answer = new JsonObject
        {
            ["format"] = "infopanel.weather/1",
            ["place"] = new JsonObject { ["name"] = "Testville", ["country"] = "TV", ["from"] = "network" },
            ["time"] = "2026-11-01T12:00:00Z",
            ["updated"] = "2026-11-01T11:40:00Z",
            ["expires"] = (now + (expiresIn ?? TimeSpan.FromMinutes(30))).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["stale"] = false,
            ["current"] = new JsonObject
            {
                ["temperature"] = 21.5, ["feelsLike"] = 20, ["humidity"] = 60, ["dewPoint"] = 13.4, ["pressure"] = 1013,
                ["windSpeed"] = 10, ["windGust"] = 15, ["windDirection"] = 210, ["cloudCover"] = 88, ["uvIndex"] = 0.4,
                ["precipitation"] = 25.4, ["symbol"] = "lightrain", ["condition"] = "Light rain",
            },
            ["next24h"] = new JsonObject { ["high"] = 25, ["low"] = 18 },
            ["sun"] = new JsonObject { ["sunrise"] = "2026-11-01T06:58:00Z", ["sunset"] = "2026-11-01T17:31:00Z" },
            ["attribution"] = new JsonObject
            {
                ["text"] = Credit, ["url"] = "https://service.example/credit", ["licence"] = "Test terms",
                ["licenceUrl"] = "https://service.example/terms", ["changes"] = "Feels-like is computed",
            },
        };
        change?.Invoke(answer);
        return answer;
    }

    private static byte[] Body(JsonNode answer) => Encoding.UTF8.GetBytes(answer.ToJsonString());

    private static byte[] Problem(int status, string code) =>
        Encoding.UTF8.GetBytes($$"""{"type":"about:blank","status":{{status}},"title":"t","code":"{{code}}"}""");

    // ---- Making one ----

    private ServiceClient Client(bool offline = false)
    {
        var client = new ServiceClient(_credentials, loopbackOnly: offline, _clock);
        _owned.Add(client);
        return client;
    }

    private WeatherSource Source(TimeProvider clock, WeatherSettings? settings = null, string region = "en-GB", bool offline = false)
    {
        _clock = clock;
        var source = new WeatherSource(Client(offline), _credentials, clock, new WeatherSourceOptions
        {
            Format = RegionalFormat.Of(CultureInfo.GetCultureInfo(region)),
            Jitter = () => 0,
            Log = _log,
        });
        _owned.Add(source);
        if (settings != null)
            source.Apply(settings);
        return source;
    }

    private WeatherFetcher Fetcher(TimerTimeProvider clock, double jitter = 0)
    {
        _clock = clock;
        var fetcher = new WeatherFetcher(Client(), _credentials, clock, () => jitter, _log);
        _owned.Add(fetcher);
        return fetcher;
    }

    private static ManualTimeProvider Manual(TimeZoneInfo? zone = null) => new(Start, zone);

    private static TimerTimeProvider Timed() => new(Start);

    /// <summary>A fetch, then a poll (the first opens the source: its readings listed and polled once).</summary>
    private static async Task FetchAndPoll(WeatherSource source)
    {
        await source.Fetcher.FetchOnceAsync(Ct);
        Poll(source);
    }

    private static void Poll(WeatherSource source)
    {
        if (source.GetSensors().Count == 0)
            source.Polled.Open();
        else
            source.Polled.Poll();
    }

    private static SensorState Read(WeatherSource source, string id) => source.ReadState(WeatherSource.KeyOf(id));

    private static void AssertNumber(WeatherSource source, string id, double value, string unit)
    {
        var state = Read(source, id);
        Assert.Equal(SensorStatus.Live, state.Status);
        Assert.Equal(SensorKind.Number, state.Type);
        Assert.Equal(value, state.Reading.ValueNow, 0.01);
        Assert.Equal(unit, state.Reading.Unit);
        Assert.Equal(unit, source.GetSensors().Single(s => s.Key == WeatherSource.KeyOf(id)).Unit);
    }

    private static void AssertText(WeatherSource source, string id, string text)
    {
        var state = Read(source, id);
        Assert.Equal((SensorStatus.Live, SensorKind.Text, text), (state.Status, state.Type, state.Reading.ValueText));
    }

    /// <summary>Waits (on the wall clock, at most 10 s) until <paramref name="condition"/> holds.</summary>
    private static async Task Until(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Timed out waiting for {what}");
            await Task.Delay(5, Ct);
        }
    }

    /// <summary>Waits until the fetch task has tried <paramref name="attempts"/> requests and waits, so nothing more is under way.</summary>
    private static Task Idle(WeatherFetcher fetcher, long attempts) =>
        Until(() => fetcher.Attempts == attempts && fetcher.IsWaiting, $"the task to wait after {attempts} attempts");

    private static void AssertState(SourceState state, SourceStateKind kind, string? code)
    {
        Assert.Equal(kind, state.Kind);
        Assert.Equal(code, state.Reason?.Code);
    }

    // ---- No credential, no service ----

    [Fact]
    public async Task NoCredential_NoRequest_EverAndOffNoCredential_AndTheReadingsListedUnavailable()
    {
        var clock = Timed();
        var source = Source(clock);

        await source.StartAsync(Ct);
        await Idle(source.Fetcher, 1);
        clock.Advance(TimeSpan.FromHours(3));
        await Idle(source.Fetcher, 1);

        Assert.Empty(_service.Requests);
        Assert.Equal(0, source.Fetcher.Requests);
        AssertState(source.State, SourceStateKind.Off, "noCredential");
        Assert.Equal("Waiting for a client to provide the service credential", source.State.Reason!.Message);
        Assert.Equal(WeatherSource.Entries.Count, source.GetSensors().Count);
        Assert.All(source.GetSensors(), s => Assert.Equal(SensorStatus.Unavailable, source.GetStatus(s.Key)));
        await source.StopAsync(Ct);
    }

    [Fact]
    public async Task NoServiceAddress_NoRequest_AndOffNoService()
    {
        _address = null;
        var source = Source(Manual());

        await FetchAndPoll(source);

        Assert.Empty(_service.Requests);
        AssertState(source.State, SourceStateKind.Off, "noService");
    }

    [Fact]
    public async Task WithoutAServiceClient_TheSourceHasNoService()
    {
        using var source = new WeatherSource(null, null, Manual());

        await source.Fetcher.FetchOnceAsync(Ct);
        source.Polled.Open();

        AssertState(source.State, SourceStateKind.Off, "noService");
        Assert.All(source.GetSensors(), s => Assert.Equal(SensorStatus.Unavailable, source.GetStatus(s.Key)));
    }

    [Fact]
    public async Task TheStates_AreTheHubsSourceStates()
    {
        var source = Source(Manual());
        var hub = new SensorHub([source]);

        source.Polled.Open();
        await source.Fetcher.FetchOnceAsync(Ct);

        var entry = Assert.Single(hub.SourceStates());
        Assert.Equal(("infopanel.weather", SourceStateKind.Off, "noCredential", 19), (entry.Id, entry.State.Kind, entry.State.Reason!.Code, entry.Sensors));
        _credentials.Put(Credential);
        await source.Fetcher.FetchOnceAsync(Ct);
        Assert.Equal(SourceState.Running, hub.SourceStates()[0].State);
    }

    // ---- An answer's readings ----

    [Fact]
    public async Task TheCatalogue_EveryReading_InOrder_UnderItsKey_GroupKindAndMetricUnit()
    {
        var source = Source(Manual());
        source.Polled.Open();

        string[] ids = ["place", "condition", "symbol", "temperature", "feels_like", "high", "low", "dew_point", "humidity", "cloud_cover",
            "pressure", "wind_speed", "wind_gust", "wind_direction", "precipitation", "uv_index", "sunrise", "sunset", "attribution"];
        string[] units = ["", "", "", "°C", "°C", "°C", "°C", "°C", "%", "%", "hPa", "m/s", "m/s", "°", "mm", "", "", "", ""];
        Assert.Equal(ids.Select(id => $"plugin:/infopanel.weather/{id}"), source.GetSensors().Select(s => s.Key.ToString()));
        Assert.Equal(units, source.GetSensors().Select(s => s.Unit));
        Assert.All(source.GetSensors(), s => Assert.Equal(("infopanel.weather", "Weather"), (s.SourceId, s.Group)));
        Assert.Equal(["Place", "Condition", "Symbol", "Temperature", "Feels like", "High (next 24 h)", "Low (next 24 h)", "Dew point", "Humidity",
            "Cloud cover", "Pressure (sea level)", "Wind speed", "Wind gust", "Wind direction (from)", "Precipitation (next hour)",
            "UV index (clear sky)", "Sunrise", "Sunset", "Credit"], source.GetSensors().Select(s => s.Name));
        string[] texts = ["place", "condition", "symbol", "sunrise", "sunset", "attribution"];
        Assert.All(ids, id => Assert.Equal(texts.Contains(id) ? SensorKind.Text : SensorKind.Number, Read(source, id).Type));
    }

    [Fact]
    public async Task AnAnswer_GivesEveryReading_Metric_ByDefault()
    {
        _credentials.Put(Credential);
        var source = Source(Manual());

        await FetchAndPoll(source);

        AssertState(source.State, SourceStateKind.Running, null);
        AssertText(source, "place", "Testville");
        AssertText(source, "condition", "Light rain");
        AssertText(source, "symbol", "lightrain");
        AssertNumber(source, "temperature", 21.5, "°C");
        AssertNumber(source, "feels_like", 20, "°C");
        AssertNumber(source, "high", 25, "°C");
        AssertNumber(source, "low", 18, "°C");
        AssertNumber(source, "dew_point", 13.4, "°C");
        AssertNumber(source, "humidity", 60, "%");
        AssertNumber(source, "cloud_cover", 88, "%");
        AssertNumber(source, "pressure", 1013, "hPa");
        AssertNumber(source, "wind_speed", 10, "m/s");
        AssertNumber(source, "wind_gust", 15, "m/s");
        AssertNumber(source, "wind_direction", 210, "°");
        AssertNumber(source, "precipitation", 25.4, "mm");
        AssertNumber(source, "uv_index", 0.4, "");
        AssertText(source, "sunrise", "06:58");
        AssertText(source, "sunset", "17:31");
        AssertText(source, "attribution", Credit);
        var request = Assert.Single(_service.Requests);
        Assert.Equal(("GET", "/svc/v1/weather"), (request.Method, request.Path));
    }

    [Fact]
    public async Task Imperial_ConvertsToFahrenheitInchesOfMercuryMilesAnHourAndInches()
    {
        _credentials.Put(Credential);
        var source = Source(Manual(), WeatherSettings.Default with { Units = WeatherUnits.Imperial });

        await FetchAndPoll(source);

        AssertNumber(source, "temperature", 70.7, "°F");
        AssertNumber(source, "feels_like", 68, "°F");
        AssertNumber(source, "high", 77, "°F");
        AssertNumber(source, "low", 64.4, "°F");
        AssertNumber(source, "dew_point", 56.12, "°F");
        AssertNumber(source, "pressure", 29.91, "inHg");
        AssertNumber(source, "wind_speed", 22.37, "mph");
        AssertNumber(source, "wind_gust", 33.55, "mph");
        AssertNumber(source, "precipitation", 1, "in");
        AssertNumber(source, "humidity", 60, "%");
        AssertNumber(source, "cloud_cover", 88, "%");
        AssertNumber(source, "wind_direction", 210, "°");
        AssertNumber(source, "uv_index", 0.4, "");
    }

    [Fact]
    public void ImperialUnits_ShowBeforeTheFirstAnswer()
    {
        var source = Source(Manual(), WeatherSettings.Default with { Units = WeatherUnits.Imperial });

        source.Polled.Open();

        var units = source.GetSensors().ToDictionary(s => s.Key.Id[(s.Key.Id.LastIndexOf('/') + 1)..], s => s.Unit);
        Assert.Equal(("°F", "inHg", "mph", "in"), (units["temperature"], units["pressure"], units["wind_speed"], units["precipitation"]));
    }

    [Theory]
    [InlineData("en-US", true)]
    [InlineData("en-GB", false)]
    [InlineData("de-DE", false)]
    [InlineData("en", false)]
    public void Auto_IsThePcsRegions(string region, bool imperial)
    {
        var source = Source(Manual(), region: region);

        source.Polled.Open();

        Assert.Equal(imperial, source.Imperial);
        Assert.Equal(imperial ? "°F" : "°C", source.GetSensors().Single(s => s.Key == WeatherSource.KeyOf("temperature")).Unit);
    }

    [Fact]
    public async Task ASwitchOfUnits_AppliesToTheAnswerHeld_AtOnce_WithNoRequest_AndRestartsTheStatistics()
    {
        _credentials.Put(Credential);
        var source = Source(Manual());
        await FetchAndPoll(source);
        AssertNumber(source, "temperature", 21.5, "°C");

        source.Apply(WeatherSettings.Default with { Units = WeatherUnits.Imperial });
        Poll(source);
        Poll(source);

        AssertNumber(source, "temperature", 70.7, "°F");
        AssertNumber(source, "pressure", 29.91, "inHg");
        AssertNumber(source, "wind_speed", 22.37, "mph");
        AssertNumber(source, "precipitation", 1, "in");
        var temperature = Read(source, "temperature").Reading;
        Assert.Equal(70.7, temperature.ValueMin, 0.01);
        Assert.Equal(70.7, temperature.ValueMax, 0.01);
        // Units that don't change keep their reading.
        AssertNumber(source, "humidity", 60, "%");

        source.Apply(WeatherSettings.Default with { Units = WeatherUnits.Metric });
        Poll(source);
        Poll(source);

        AssertNumber(source, "temperature", 21.5, "°C");
        AssertNumber(source, "pressure", 1013, "hPa");
        AssertNumber(source, "wind_speed", 10, "m/s");
        AssertNumber(source, "precipitation", 25.4, "mm");
        Assert.Single(_service.Requests);
    }

    [Fact]
    public async Task AValueTheServiceDoesntGive_ReadsUnavailable_AndTheRestLive()
    {
        _credentials.Put(Credential);
        _answer = _ => (200, Body(Answer(a =>
        {
            a["current"]!["windGust"] = null;
            a["current"]!["uvIndex"] = null;
            a["current"]!["temperature"] = "warm";
            a["sun"]!["sunrise"] = null;
        })), []);
        var source = Source(Manual());

        await FetchAndPoll(source);

        foreach (var id in new[] { "wind_gust", "uv_index", "temperature", "sunrise" })
            Assert.Equal(SensorStatus.Unavailable, Read(source, id).Status);
        AssertNumber(source, "wind_speed", 10, "m/s");
        AssertText(source, "sunset", "17:31");
    }

    [Theory]
    [InlineData("en-GB", 8, "14:58", "01:31")]
    [InlineData("en-US", -5, "1:58 AM", "12:31 PM")]
    public async Task SunriseAndSunset_InThePcsTimeZone_AndItsTimeFormat(string region, int offset, string sunrise, string sunset)
    {
        _credentials.Put(Credential);
        var zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(offset), "test", "test");
        var source = Source(Manual(zone), region: region);

        await FetchAndPoll(source);

        // ICU may write a narrow no-break space before AM and PM.
        Assert.Equal((sunrise, sunset), (Read(source, "sunrise").Reading.ValueText!.Replace('\u202F', ' '), Read(source, "sunset").Reading.ValueText!.Replace('\u202F', ' ')));
    }

    [Fact]
    public async Task APlaceGivenAsCoordinates_ReadsAsGiven_WhenTheServiceNamesNone()
    {
        _credentials.Put(Credential);
        _answer = _ => (200, Body(Answer(a => a["place"] = new JsonObject { ["name"] = null, ["country"] = null, ["from"] = "given" })), []);
        var source = Source(Manual(), WeatherSettings.Default with { Place = "59.91, 10.75" });

        await FetchAndPoll(source);

        AssertText(source, "place", "59.91, 10.75");
        Assert.Equal("/svc/v1/weather?place=59.91%2C%2010.75", Assert.Single(_service.Requests).Path);
    }

    // ---- Freshness ----

    [Fact]
    public async Task Live_For90Minutes_ThenStale_KeepingTheValues_ThenUnavailableAfter6Hours()
    {
        _credentials.Put(Credential);
        var clock = Manual();
        var source = Source(clock);
        await FetchAndPoll(source);

        clock.Advance(TimeSpan.FromMinutes(89));
        Poll(source);
        AssertNumber(source, "temperature", 21.5, "°C");

        clock.Advance(TimeSpan.FromMinutes(2));
        Poll(source);
        Assert.Equal((SensorStatus.Stale, 21.5), (Read(source, "temperature").Status, Read(source, "temperature").Reading.ValueNow));
        Assert.Equal((SensorStatus.Stale, "Testville"), (Read(source, "place").Status, Read(source, "place").Reading.ValueText));

        clock.Advance(TimeSpan.FromMinutes(4 * 60 + 28));
        Poll(source);
        Assert.Equal(SensorStatus.Stale, Read(source, "temperature").Status);

        clock.Advance(TimeSpan.FromMinutes(2));
        Poll(source);
        Assert.All(source.GetSensors(), s => Assert.Equal(SensorStatus.Unavailable, source.GetStatus(s.Key)));
    }

    [Fact]
    public async Task AnAnswerTheServiceCallsStale_ReadsStale_KeepingTheValuesBefore()
    {
        _credentials.Put(Credential);
        var source = Source(Manual());
        await FetchAndPoll(source);

        _answer = _ => (200, Body(Answer(a => { a["stale"] = true; a["current"]!["temperature"] = 3; })), []);
        await FetchAndPoll(source);

        Assert.Equal((SensorStatus.Stale, 21.5), (Read(source, "temperature").Status, Read(source, "temperature").Reading.ValueNow));
    }

    [Fact]
    public async Task AFirstAnswerTheServiceCallsStale_ReadsStale_WithItsValues_AgedFromItsArrival()
    {
        _credentials.Put(Credential);
        _answer = _ => (200, Body(Answer(a => { a["stale"] = true; a["current"]!["temperature"] = 3; })), []);
        var clock = Manual();
        var source = Source(clock);

        await FetchAndPoll(source);

        Assert.Equal((SensorStatus.Stale, 3.0), (Read(source, "temperature").Status, Read(source, "temperature").Reading.ValueNow));
        Assert.Equal((SensorStatus.Stale, "Testville"), (Read(source, "place").Status, Read(source, "place").Reading.ValueText));
        Assert.Equal(SourceState.Running, source.State);

        // Stale answers after it change nothing, and never renew its age.
        _answer = _ => (200, Body(Answer(a => { a["stale"] = true; a["current"]!["temperature"] = 4; })), []);
        clock.Advance(TimeSpan.FromHours(6));
        await FetchAndPoll(source);
        Assert.Equal((SensorStatus.Stale, 3.0), (Read(source, "temperature").Status, Read(source, "temperature").Reading.ValueNow));
        clock.Advance(TimeSpan.FromMinutes(1));
        await FetchAndPoll(source);
        Assert.All(source.GetSensors(), s => Assert.Equal(SensorStatus.Unavailable, source.GetStatus(s.Key)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASwitchOfUnits_WhileStale_ReadsTheValuesKept_Converted_StillStale_TheirStatisticsAfresh(bool staleAnswer)
    {
        _credentials.Put(Credential);
        var clock = Manual();
        var source = Source(clock);
        await FetchAndPoll(source);
        if (staleAnswer)
        {
            // The service calls its answer stale.
            _answer = _ => (200, Body(Answer(a => a["stale"] = true)), []);
            await FetchAndPoll(source);
        }
        else
        {
            // No answer for over 90 minutes.
            clock.Advance(TimeSpan.FromMinutes(91));
            Poll(source);
        }

        Assert.Equal(SensorStatus.Stale, Read(source, "temperature").Status);

        source.Apply(WeatherSettings.Default with { Units = WeatherUnits.Imperial });
        Poll(source);
        Poll(source);

        var temperature = Read(source, "temperature");
        Assert.Equal((SensorStatus.Stale, "°F"), (temperature.Status, temperature.Reading.Unit));
        Assert.Equal(70.7, temperature.Reading.ValueNow, 0.01);
        Assert.Equal(70.7, temperature.Reading.ValueMin, 0.01);
        Assert.Equal(70.7, temperature.Reading.ValueMax, 0.01);
        var pressure = Read(source, "pressure");
        Assert.Equal((SensorStatus.Stale, "inHg"), (pressure.Status, pressure.Reading.Unit));
        Assert.Equal(29.91, pressure.Reading.ValueNow, 0.01);
        Assert.Equal((SensorStatus.Stale, 60.0), (Read(source, "humidity").Status, Read(source, "humidity").Reading.ValueNow));
        Assert.Equal((SensorStatus.Stale, "Testville"), (Read(source, "place").Status, Read(source, "place").Reading.ValueText));
        Assert.Equal(staleAnswer ? 2 : 1, _service.Requests.Count);
    }

    [Fact]
    public async Task StaleAnswers_NeverRenewTheValuesAge_SoTheyAreUnavailable6HoursAfterTheyWereLive()
    {
        _credentials.Put(Credential);
        var clock = Manual();
        var source = Source(clock);
        await FetchAndPoll(source);

        // Live at 12:00, polled to 12:29; from 12:30 the service answers stale every 30 minutes.
        clock.Advance(TimeSpan.FromMinutes(29));
        Poll(source);
        AssertNumber(source, "temperature", 21.5, "°C");
        _answer = _ => (200, Body(Answer(a => a["stale"] = true)), []);
        clock.Advance(TimeSpan.FromMinutes(1));
        for (var answer = 1; answer <= 12; answer++)
        {
            if (answer > 1)
                clock.Advance(TimeSpan.FromMinutes(30));
            await FetchAndPoll(source);
            Assert.Equal((SensorStatus.Stale, 21.5), (Read(source, "temperature").Status, Read(source, "temperature").Reading.ValueNow));
        }

        // 18:10, ten minutes after the last stale answer: the values shown are 6 h 10 min old.
        clock.Advance(TimeSpan.FromMinutes(10));
        Poll(source);
        Assert.Equal(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(10), clock.GetUtcNow() - Start);
        Assert.All(source.GetSensors(), s => Assert.Equal(SensorStatus.Unavailable, source.GetStatus(s.Key)));
    }

    // ---- The schedule ----

    [Theory]
    [InlineData(30, 0, 30)]
    [InlineData(30, 1, 32)]
    [InlineData(2, 0.5, 10)]
    [InlineData(-5, 0, 10)]
    [InlineData(180, 0, 60)]
    [InlineData(59, 1, 60)]
    public void AfterAnAnswer_TheNextIsAtItsExpiryPlusJitter_Within10To60Minutes(int expiresInMinutes, double jitter, int waitMinutes)
    {
        var wait = new WeatherSchedule().Answered(Start, Start.AddMinutes(expiresInMinutes), jitter);

        Assert.Equal(TimeSpan.FromMinutes(waitMinutes), wait);
    }

    [Fact]
    public void TheBackoff_Is1_2_4_8_16_Then30Minutes_AndAnAnswerStartsItAgain()
    {
        var schedule = new WeatherSchedule();

        var waits = Enumerable.Range(0, 8).Select(_ => schedule.Failed().TotalMinutes).ToArray();
        schedule.Answered(Start, Start.AddMinutes(30), 0);

        Assert.Equal([1, 2, 4, 8, 16, 30, 30, 30], waits);
        Assert.Equal(TimeSpan.FromMinutes(1), schedule.Failed());
        Assert.Equal(TimeSpan.FromMinutes(5), new WeatherSchedule().Failed(TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(30, 1)]
    [InlineData(300, 5)]
    [InlineData(5 * 3600, 5 * 60)]
    [InlineData(86400, 24 * 60)]
    [InlineData(10 * 86400, 24 * 60)]
    public void AFailure_WaitsItsRetryAfter_WhenLongerThanTheBackoff_UpTo24Hours(int? retryAfterSeconds, int waitMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(waitMinutes), new WeatherSchedule().Failed(retryAfterSeconds is { } s ? TimeSpan.FromSeconds(s) : null));

    [Theory]
    [InlineData(1200, 20)]
    [InlineData(30, 10)]
    [InlineData(null, 10)]
    [InlineData(5 * 3600, 5 * 60)]
    [InlineData(86400, 24 * 60)]
    [InlineData(10 * 86400, 24 * 60)]
    public void A429_WaitsItsRetryAfter_AtLeast10Minutes_UpTo24Hours(int? retryAfterSeconds, int waitMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(waitMinutes), WeatherSchedule.Limited(retryAfterSeconds is { } s ? TimeSpan.FromSeconds(s) : null));

    [Theory]
    [InlineData(429, "rateLimited")]
    [InlineData(503, "serviceUnavailable")]
    public async Task ARetryAfterOfADay_IsWaited_NotCutTo60Minutes(int status, string reason)
    {
        _credentials.Put(Credential);
        _answer = _ => (status, Problem(status, "busy"), ["Retry-After: 86400"]);
        var clock = Timed();
        var fetcher = Fetcher(clock);

        fetcher.Start();
        await Idle(fetcher, 1);
        AssertState(fetcher.State, SourceStateKind.Unavailable, reason);
        clock.Advance(TimeSpan.FromHours(1));
        await Idle(fetcher, 1);
        clock.Advance(TimeSpan.FromHours(23) - TimeSpan.FromSeconds(1));
        await Idle(fetcher, 1);
        Assert.Single(_service.Requests);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Idle(fetcher, 2);
        Assert.Equal(2, _service.Requests.Count);
        await fetcher.StopAsync(Ct);
    }

    [Fact]
    public async Task TheTask_AsksAtOnce_ThenAtTheAnswersExpiry_NotSooner()
    {
        _credentials.Put(Credential);
        var clock = Timed();
        var fetcher = Fetcher(clock);

        fetcher.Start();
        await Idle(fetcher, 1);
        Assert.Single(_service.Requests);

        // Polling every minute would have asked by now.
        foreach (var _ in Enumerable.Range(0, 29))
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await Idle(fetcher, 1);
        }

        Assert.Single(_service.Requests);
        clock.Advance(TimeSpan.FromSeconds(61));
        await Idle(fetcher, 2);
        Assert.Equal(2, _service.Requests.Count);
        await fetcher.StopAsync(Ct);
    }

    [Fact]
    public async Task TheTask_UsesTheJitter_AndNeverWaitsPast60Minutes()
    {
        _credentials.Put(Credential);
        _answer = _ => (200, Body(Answer(expiresIn: TimeSpan.FromHours(3))), []);
        var clock = Timed();
        var fetcher = Fetcher(clock, jitter: 1);

        fetcher.Start();
        await Idle(fetcher, 1);
        clock.Advance(TimeSpan.FromMinutes(59));
        await Idle(fetcher, 1);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Idle(fetcher, 2);

        Assert.Equal(2, _service.Requests.Count);
        await fetcher.StopAsync(Ct);
    }

    [Fact]
    public async Task Failures_BackOff_1_2_4_8_16_30Minutes()
    {
        _credentials.Put(Credential);
        _answer = _ => (500, [], []);
        var clock = Timed();
        var fetcher = Fetcher(clock);

        fetcher.Start();
        await Idle(fetcher, 1);
        AssertState(fetcher.State, SourceStateKind.Unavailable, "serviceUnavailable");

        var attempts = 1;
        foreach (var minutes in new[] { 1, 2, 4, 8, 16, 30, 30 })
        {
            clock.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));
            await Idle(fetcher, attempts);
            Assert.Equal(attempts, _service.Requests.Count);
            clock.Advance(TimeSpan.FromSeconds(1));
            await Idle(fetcher, ++attempts);
            Assert.Equal(attempts, _service.Requests.Count);
        }

        await fetcher.StopAsync(Ct);
    }

    [Fact]
    public async Task A401_StopsRequests_UntilANewCredential_WhichAsksAtOnce()
    {
        _credentials.Put(Credential);
        _answer = _ => (401, Problem(401, "unauthorized"), ["WWW-Authenticate: Bearer"]);
        var clock = Timed();
        var fetcher = Fetcher(clock);

        fetcher.Start();
        // The rejection is a change of the credential: the task looks again, and is refused before sending anything.
        await Idle(fetcher, 2);
        AssertState(fetcher.State, SourceStateKind.Unavailable, "credentialRejected");
        Assert.Equal(ServiceCredentialState.Rejected, _credentials.Status.Credential);

        clock.Advance(TimeSpan.FromHours(3));
        await Idle(fetcher, 2);
        Assert.Single(_service.Requests);

        _answer = _ => (200, Body(Answer()), []);
        _credentials.Put("another-credential");
        await Idle(fetcher, 3);
        Assert.Equal(2, _service.Requests.Count);
        Assert.Equal("Bearer another-credential", _service.Requests[1].Header("authorization"));
        Assert.Equal(SourceState.Running, fetcher.State);
        await fetcher.StopAsync(Ct);
    }

    [Fact]
    public async Task A429_WaitsItsRetryAfter()
    {
        _credentials.Put(Credential);
        _answer = _ => (429, Problem(429, "rateLimited"), ["Retry-After: 1200"]);
        var clock = Timed();
        var fetcher = Fetcher(clock);

        fetcher.Start();
        await Idle(fetcher, 1);
        AssertState(fetcher.State, SourceStateKind.Unavailable, "rateLimited");
        clock.Advance(TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(1));
        await Idle(fetcher, 1);
        Assert.Single(_service.Requests);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Idle(fetcher, 2);
        Assert.Equal(2, _service.Requests.Count);
        await fetcher.StopAsync(Ct);
    }

    [Fact]
    public async Task ANewPlace_AsksAtOnce_ForItAlone_AndForgetsTheAnswerForTheOldOne()
    {
        _credentials.Put(Credential);
        var clock = Timed();
        var source = Source(clock);
        source.Polled.Open();
        source.Fetcher.Start();
        await Idle(source.Fetcher, 1);
        Assert.NotNull(source.Fetcher.Latest);

        _answer = _ => (500, [], []);
        source.Apply(WeatherSettings.Default with { Place = "São Paulo" });
        Assert.Null(source.Fetcher.Latest);
        await Idle(source.Fetcher, 2);

        Assert.Equal("/svc/v1/weather?place=S%C3%A3o%20Paulo", _service.Requests[1].Path);
        source.Polled.Poll();
        Assert.Equal(SensorStatus.Unavailable, Read(source, "temperature").Status);
        await source.Fetcher.StopAsync(Ct);
    }

    [Fact]
    public async Task APlaceChange_AsTheTaskTakesARequest_AsksForTheNewPlaceOnce()
    {
        _credentials.Put(Credential);
        var clock = Timed();
        var fetcher = Fetcher(clock);
        var taken = 0;
        // The place changes between the task taking the request due and making it.
        fetcher.AfterTakeDue = () =>
        {
            if (Interlocked.Increment(ref taken) == 1)
                fetcher.Configure(true, "Elsewhere");
        };

        fetcher.Start();
        await Until(() => fetcher.IsWaiting, "the task to wait");

        Assert.Equal(["/svc/v1/weather?place=Elsewhere"], _service.Requests.Select(r => r.Path));
        Assert.Equal("Elsewhere", fetcher.Latest?.Place);
        Assert.Equal(SourceState.Running, fetcher.State);
        await fetcher.StopAsync(Ct);
    }

    [Fact]
    public async Task TurnedOff_AsksNothing_ForgetsTheAnswer_AndIsOffDisabled_ThenOnAsksAtOnce()
    {
        _credentials.Put(Credential);
        var clock = Timed();
        var source = Source(clock);
        source.Polled.Open();
        source.Fetcher.Start();
        await Idle(source.Fetcher, 1);

        source.Apply(WeatherSettings.Default with { Enabled = false });
        source.Polled.Poll();
        clock.Advance(TimeSpan.FromHours(2));
        await Until(() => source.Fetcher.IsWaiting, "the task to wait");

        Assert.Single(_service.Requests);
        AssertState(source.State, SourceStateKind.Off, "disabled");
        Assert.All(source.GetSensors(), s => Assert.Equal(SensorStatus.Unavailable, source.GetStatus(s.Key)));

        source.Apply(WeatherSettings.Default);
        await Idle(source.Fetcher, 2);
        Assert.Equal(2, _service.Requests.Count);
        Assert.Equal(SourceState.Running, source.State);
        await source.Fetcher.StopAsync(Ct);
    }

    [Fact]
    public async Task ARequestUnderWay_WhenTurnedOff_IsCancelled_AndWhatItBringsDropped()
    {
        var release = new TaskCompletionSource();
        var received = new TaskCompletionSource();
        await using var slow = new LocalHttpServer(async (_, stream, ct) =>
        {
            received.TrySetResult();
            await release.Task.WaitAsync(ct);
            await LocalHttpServer.Answer(stream, 200, Body(Answer()), "application/json");
        });
        _address = $"http://127.0.0.1:{slow.Port}";
        _credentials.Put(Credential);
        var source = Source(Manual());

        var fetch = source.Fetcher.FetchOnceAsync(Ct);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        source.Apply(WeatherSettings.Default with { Enabled = false });
        await fetch.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        release.TrySetResult();

        Assert.Null(source.Fetcher.Latest);
        AssertState(source.State, SourceStateKind.Off, "disabled");
    }

    // ---- What reaches the service ----

    [Fact]
    public async Task TheCredential_GoesToTheServicesOrigin_AndNotOnARedirect_WhichIsntFollowed()
    {
        _credentials.Put(Credential);
        await using var elsewhere = new LocalHttpServer((_, stream, _) => LocalHttpServer.Answer(stream, 200, Body(Answer()), "application/json"));
        _answer = _ => (302, [], [$"Location: http://127.0.0.1:{elsewhere.Port}/svc/v1/weather"]);
        var source = Source(Manual());

        await FetchAndPoll(source);

        var request = Assert.Single(_service.Requests);
        Assert.Equal($"Bearer {Credential}", request.Header("authorization"));
        Assert.Empty(elsewhere.Requests);
        Assert.Null(source.Fetcher.Latest);
        AssertState(source.State, SourceStateKind.Unavailable, "serviceUnavailable");
    }

    [Fact]
    public async Task TheCredentialAndThePlace_AreNeverLogged()
    {
        _credentials.Put(Credential);
        var source = Source(Manual(), WeatherSettings.Default with { Place = "Secretplace" });
        foreach (var answer in new Func<ServedRequest, (int, byte[], string[])>[]
        {
            _ => (200, Body(Answer()), []),
            _ => (302, [], ["Location: http://127.0.0.1:1/"]),
            _ => (422, Problem(422, "placeNotFound"), []),
            _ => (500, [], []),
            _ => (401, Problem(401, "unauthorized"), []),
        })
        {
            _answer = answer;
            await FetchAndPoll(source);
        }

        Assert.NotEmpty(_events);
        foreach (var e in _events)
        {
            var text = e.RenderMessage() + string.Join(" ", e.Properties.Values) + e.Exception;
            Assert.DoesNotContain(Credential, text, StringComparison.Ordinal);
            Assert.DoesNotContain("Secretplace", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(WeatherAnswer.MaxBytes, true)]
    [InlineData(WeatherAnswer.MaxBytes + 1, false)]
    public async Task AnAnswerOver64KiB_Fails(int size, bool kept)
    {
        _credentials.Put(Credential);
        var body = Body(Answer());
        _answer = _ => (200, [.. body, .. Enumerable.Repeat((byte)' ', size - body.Length)], []);
        var source = Source(Manual());

        await FetchAndPoll(source);

        Assert.Equal(kept, source.Fetcher.Latest != null);
        Assert.Equal(kept ? SourceStateKind.Running : SourceStateKind.Unavailable, source.State.Kind);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("<html>Sign-in page</html>")]
    [InlineData("[]")]
    [InlineData("""{"format":"infopanel.weather/2","place":{},"expires":"2026-11-01T12:30:00Z","stale":false,"current":{}}""")]
    [InlineData("""{"format":"infopanel.weather/1","place":{},"expires":"soon","stale":false,"current":{}}""")]
    [InlineData("""{"format":"infopanel.weather/1","place":{},"expires":"2026-11-01T12:30:00Z","current":{}}""")]
    [InlineData("""{"format":"infopanel.weather/1","place":{},"expires":"2026-11-01T12:30:00Z","stale":false}""")]
    [InlineData("""{"format":"infopanel.weather/1","place":{},"expires":"2026-11-01T12:30:00Z","stale":false,"current":{},"x":[[[[[[[[[[[[[[[[1]]]]]]]]]]]]]]]]}""")]
    public async Task AMalformedAnswer_Fails_AndBacksOff(string body)
    {
        _credentials.Put(Credential);
        _answer = _ => (200, Encoding.UTF8.GetBytes(body), []);
        var source = Source(Manual());

        await FetchAndPoll(source);

        Assert.Null(source.Fetcher.Latest);
        AssertState(source.State, SourceStateKind.Unavailable, "serviceUnavailable");
    }

    [Fact]
    public void AnAnswer_15LevelsDeep_IsRead()
    {
        var deep = """{"format":"infopanel.weather/1","place":{},"expires":"2026-11-01T12:30:00Z","stale":false,"current":{},"x":[[[[[[[[[[[[[[1]]]]]]]]]]]]]]}""";
        Assert.NotNull(WeatherAnswer.Parse(Encoding.UTF8.GetBytes(deep)));
    }

    [Theory]
    [InlineData(422, "placeNotFound", "placeNotFound")]
    [InlineData(422, "locationUnknown", "locationUnknown")]
    [InlineData(422, "other", "serviceUnavailable")]
    [InlineData(400, "badRequest", "placeNotFound")]
    [InlineData(503, "upstreamUnavailable", "serviceUnavailable")]
    public async Task TheServicesRefusals_AreTheSourcesReasons(int status, string code, string reason)
    {
        _credentials.Put(Credential);
        _answer = _ => (status, Problem(status, code), []);
        var source = Source(Manual());

        await FetchAndPoll(source);

        AssertState(source.State, SourceStateKind.Unavailable, reason);
    }

    [Fact]
    public async Task Offline_AServiceElsewhere_GetsNoRequest_AndIsUnavailableOffline()
    {
        _address = "https://weather.example";
        _credentials.Put(Credential);
        var source = Source(Manual(), offline: true);

        await FetchAndPoll(source);

        Assert.Empty(_service.Requests);
        AssertState(source.State, SourceStateKind.Unavailable, "offline");
    }

    [Fact]
    public async Task Offline_AServiceOnThisComputer_IsAsked()
    {
        _credentials.Put(Credential);
        var source = Source(Manual(), offline: true);

        await FetchAndPoll(source);

        Assert.Single(_service.Requests);
        Assert.Equal(SourceState.Running, source.State);
    }
}
