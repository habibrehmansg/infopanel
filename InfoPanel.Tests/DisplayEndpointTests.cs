using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Desktop;
using InfoPanel.Hosting;
using InfoPanel.Tests.Desktop;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static InfoPanel.Tests.ReadEndpointTests;

namespace InfoPanel.Tests;

/// <summary>
/// The computer's displays (spec/docs/api.md, "Displays"): <c>GET /api/displays</c>, for every token, read on the desktop
/// thread per request, the primary first, each in the shape a profile's <c>window.display</c> takes. On a
/// <see cref="FakeDesktopPlatform"/>, so no test needs a real display: "Built-in" (1512×982 points at 2×, the primary),
/// "External" (2560×1440 at 1×, above and to the left) and "Side" (1280×800 points at 2×, to the right, with a label of
/// its own).
/// </summary>
public sealed class DisplayEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DisplayInfo BuiltIn = new("Built-in", new PointRect(0, 0, 1512, 982), 2);
    private static readonly DisplayInfo External = new("External", new PointRect(-408, -1440, 2560, 1440), 1);
    private static readonly DisplayInfo Side = new(@"\\.\DISPLAY3", new PointRect(1512, 0, 1280, 800), 2, "Office Monitor");

    /// <summary>The engine with <paramref name="platform"/> as its desktop, optionally a clock of the test's.</summary>
    private static WebApplicationFactory<Program> WithPlatform(EngineFactory engine, IDesktopPlatform platform, TimeProvider? time = null,
        bool noWindows = false) =>
        engine.WithWebHostBuilder(builder =>
        {
            if (noWindows)
                builder.UseSetting("Engine:NoWindows", "true");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(platform);
                if (time != null)
                    services.AddSingleton(time);
            });
        });

    private static HttpClient Client(WebApplicationFactory<Program> factory, string? token = EngineFactory.Token)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        if (token != null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body, string? MediaType)> List(HttpClient client)
    {
        using var response = await client.GetAsync("/api/displays", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default, response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<JsonElement> ListOk(HttpClient client)
    {
        var (status, body, mediaType) = await List(client);
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        Assert.Equal("application/json", mediaType);
        Assert.True(Valid(body, "displayList"));
        return body;
    }

    /// <summary>The entry's <c>displayMatch</c> members, as a profile's <c>window.display</c> takes them.</summary>
    private static (string Name, int Width, int Height, int X, int Y) MatchOf(JsonElement entry) => (
        entry.GetProperty("name").GetString()!, entry.GetProperty("width").GetInt32(), entry.GetProperty("height").GetInt32(),
        entry.GetProperty("x").GetInt32(), entry.GetProperty("y").GetInt32());

    private static (string Name, int Width, int Height, int X, int Y) TargetOf(DisplayInfo display)
    {
        var target = DisplayMatcher.TargetOf(display);
        return (target.Name, target.Width, target.Height, target.X!.Value, target.Y!.Value);
    }

    [Fact]
    public async Task TheDisplays_PrimaryFirst_EachExactlyTheMatchAWindowTakes_WithItsLabelAndScale()
    {
        using var engine = new EngineFactory();
        using var platform = new FakeDesktopPlatform(BuiltIn, External, Side);
        using var factory = WithPlatform(engine, platform);
        using var api = Client(factory);

        var body = await ListOk(api);

        Assert.True(body.GetProperty("supported").GetBoolean());
        var displays = body.GetProperty("displays").EnumerateArray().ToList();
        Assert.Equal([TargetOf(BuiltIn), TargetOf(External), TargetOf(Side)], displays.Select(MatchOf));
        // The match is in the display's own pixels (Built-in: 1512 points at 2× is 3024 pixels) and the desktop's points.
        Assert.Equal(("Built-in", 3024, 1964, 0, 0), MatchOf(displays[0]));
        Assert.Equal(("External", 2560, 1440, -408, -1440), MatchOf(displays[1]));
        Assert.Equal([true, false, false], displays.Select(d => d.GetProperty("primary").GetBoolean()));
        Assert.Equal(["Built-in", "External", "Office Monitor"], displays.Select(d => d.GetProperty("label").GetString()));
        Assert.Equal([2.0, 1.0, 2.0], displays.Select(d => d.GetProperty("scale").GetDouble()));
    }

    /// <summary>
    /// A name over 256 characters is answered cut, and that entry still places a window on its display: beside a primary
    /// of the same size, where a name compared uncut would match nothing and fall to the primary, or, strict, hide it.
    /// </summary>
    [Fact]
    public async Task ADisplaysNameAndLabel_AreCutTo256Characters_AndTheEntryStillPlacesAWindowThere()
    {
        using var engine = new EngineFactory();
        var primary = new DisplayInfo("Built-in", new PointRect(0, 0, 800, 600), 1);
        var longName = new DisplayInfo(new string('n', 300), new PointRect(800, 0, 800, 600), 1, new string('l', 300));
        using var platform = new FakeDesktopPlatform(primary, longName);
        using var factory = WithPlatform(engine, platform);
        using var api = Client(factory);
        var entry = (await ListOk(api)).GetProperty("displays")[1];

        Assert.Equal(TargetOf(longName), MatchOf(entry));
        Assert.Equal(new string('n', 256), entry.GetProperty("name").GetString());
        Assert.Equal(new string('l', 256), entry.GetProperty("label").GetString());

        await AssertPlacesAWindowOn(api, factory, platform, entry);
    }

    /// <summary>
    /// A name of 255 characters and an emoji (a surrogate pair) is 256 Unicode characters, the most the document takes, so
    /// it is answered whole, not cut at 256 UTF-16 units into a lone high surrogate (which JSON writes as U+FFFD and
    /// matching keeps); the entry, as listed, still places a window on its display.
    /// </summary>
    [Fact]
    public async Task ANameOf256UnicodeCharactersEndingInASurrogatePair_IsAnsweredWhole_AndTheListedEntryStillPlacesAWindowThere()
    {
        using var engine = new EngineFactory();
        var primary = new DisplayInfo("Built-in", new PointRect(0, 0, 800, 600), 1);
        var name = new string('a', 255) + "\U0001F600";
        var emoji = new DisplayInfo(name, new PointRect(800, 0, 800, 600), 1, name);
        using var platform = new FakeDesktopPlatform(primary, emoji);
        using var factory = WithPlatform(engine, platform);
        using var api = Client(factory);

        var entry = (await ListOk(api)).GetProperty("displays")[1];

        Assert.Equal(TargetOf(emoji), MatchOf(entry));
        Assert.Equal(name, entry.GetProperty("name").GetString());
        Assert.Equal(name, entry.GetProperty("label").GetString());

        await AssertPlacesAWindowOn(api, factory, platform, entry);
    }

    /// <summary>A window whose <c>window.display</c> is the listed <paramref name="entry"/>, strict and not, lands on the second display.</summary>
    private static async Task AssertPlacesAWindowOn(HttpClient api, WebApplicationFactory<Program> factory, FakeDesktopPlatform platform, JsonElement entry)
    {
        var manager = factory.Services.GetRequiredService<DisplayWindowManager>();
        foreach (var strict in new[] { false, true })
        {
            var document = TestDocs.Doc("Long " + strict, 400, 300, TestDocs.Scene("a", 10, 10, TestDocs.Rect("#00ff00", 40, 20)));
            document["profile"]!["window"] = new JsonObject { ["enabled"] = true, ["topmost"] = true };
            var id = await TestDocs.CreateAsync(api, document, Ct);
            await platform.IdleAsync();

            var (name, width, height, x, y) = MatchOf(entry);
            var set = new JsonObject
            {
                ["window"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["topmost"] = true,
                    ["strict"] = strict,
                    ["x"] = 0,
                    ["y"] = 0,
                    ["display"] = new JsonObject { ["name"] = name, ["width"] = width, ["height"] = height, ["x"] = x, ["y"] = y },
                },
            };
            using (var committed = await TestDocs.CommitAsync(api, id, Ct, TestDocs.Profile(set)))
                Assert.True(committed.StatusCode == HttpStatusCode.OK, await committed.Content.ReadAsStringAsync(Ct));
            await platform.IdleAsync();

            var window = await platform.InvokeAsync(() => manager.WindowOf(id) as FakeWindow) ?? throw new Xunit.Sdk.XunitException("No window");
            var frame = await platform.InvokeAsync(() => window.Frame);
            Assert.True(DisplayMatcher.SameFrame(new PointRect(800, 0, 400, 300), frame), $"strict {strict}: the window is at {frame}");
            Assert.True(await platform.InvokeAsync(() => window.Visible), $"strict {strict}: the window is hidden");
        }
    }

    [Fact]
    public async Task ADisplayAddedOrRemoved_ShowsInTheNextAnswer()
    {
        using var engine = new EngineFactory();
        using var platform = new FakeDesktopPlatform(BuiltIn);
        using var factory = WithPlatform(engine, platform);
        using var api = Client(factory);

        Assert.Equal([TargetOf(BuiltIn)], (await ListOk(api)).GetProperty("displays").EnumerateArray().Select(MatchOf));

        await platform.ChangeDisplaysAsync(BuiltIn, External);
        Assert.Equal([TargetOf(BuiltIn), TargetOf(External)], (await ListOk(api)).GetProperty("displays").EnumerateArray().Select(MatchOf));

        // The external display becomes the primary and the built-in one is closed.
        var primary = External with { Frame = new PointRect(0, 0, 2560, 1440) };
        await platform.ChangeDisplaysAsync(primary);
        var displays = (await ListOk(api)).GetProperty("displays").EnumerateArray().ToList();
        Assert.Equal([TargetOf(primary)], displays.Select(MatchOf));
        Assert.True(displays[0].GetProperty("primary").GetBoolean());
    }

    [Fact]
    public async Task AnEngineThatShowsNoDesktopWindows_IsUnsupported_AndListsNone()
    {
        // The test engine's own platform: no desktop windows off the main thread (and on Windows and Linux for now).
        using (var engine = new EngineFactory())
        using (var api = engine.CreateApiClient())
        {
            Assert.IsType<NullDesktopPlatform>(engine.Services.GetRequiredService<IDesktopPlatform>());
            var body = await ListOk(api);
            Assert.False(body.GetProperty("supported").GetBoolean());
            Assert.Equal(0, body.GetProperty("displays").GetArrayLength());
        }

        // --no-windows, whatever the platform could show.
        using (var engine = new EngineFactory())
        using (var platform = new FakeDesktopPlatform(BuiltIn, External))
        using (var factory = WithPlatform(engine, platform, noWindows: true))
        using (var api = Client(factory))
        {
            var body = await ListOk(api);
            Assert.False(body.GetProperty("supported").GetBoolean());
            Assert.Equal(0, body.GetProperty("displays").GetArrayLength());
        }
    }

    [Fact]
    public async Task EveryToken_ListsTheDisplays_AndNoTokenIs401()
    {
        using var engine = new EngineFactory();
        using var platform = new FakeDesktopPlatform(BuiltIn, External);
        using var factory = WithPlatform(engine, platform);
        using var session = Client(factory);
        foreach (var scopes in new[] { new[] { "read" }, new[] { "edit" } })
        {
            using var created = await session.PostAsJsonAsync("/api/tokens", new { name = "displays-" + scopes[0], scopes }, Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var token = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString();
            using var client = Client(factory, token);

            var body = await ListOk(client);
            Assert.Equal(2, body.GetProperty("displays").GetArrayLength());
        }

        using var anonymous = Client(factory, token: null);
        var (status, problem, mediaType) = await List(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("application/problem+json", mediaType);
        Assert.True(Valid(problem, "problem"));
    }

    [Fact]
    public async Task ADesktopThreadThatDoesntAnswer_Is500After5Seconds_AndIsLogged()
    {
        using var engine = new EngineFactory();
        using var platform = new FakeDesktopPlatform(BuiltIn);
        var clock = new ManualTime(new DateTimeOffset(2026, 10, 30, 9, 30, 0, TimeSpan.Zero));
        using var factory = WithPlatform(engine, platform, clock);
        using var api = Client(factory);
        await ListOk(api);

        // The clock moved on, so the only timer due 5 s from now is the request's.
        clock.Advance(TimeSpan.FromHours(1));
        using var stall = new ManualResetEventSlim();
        platform.Post(() => stall.Wait(TimeSpan.FromSeconds(60)));
        try
        {
            var answer = List(api);
            var due = TimeSpan.FromHours(1) + TimeSpan.FromSeconds(5);
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!clock.HasTimerDueAt(due))
            {
                Assert.False(answer.IsCompleted, "answered before the desktop thread did, without waiting");
                Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "the request set no 5 s wait");
                await Task.Delay(10, Ct);
            }

            clock.Advance(TimeSpan.FromSeconds(5));
            var (status, body, mediaType) = await answer.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            Assert.Equal(HttpStatusCode.InternalServerError, status);
            Assert.Equal("application/problem+json", mediaType);
            Assert.True(Valid(body, "problem"));
            var logged = factory.Services.GetRequiredService<RecentLog>().After(0);
            Assert.Contains(logged, e => e.Level == "error" && e.Message.Contains("desktop thread", StringComparison.Ordinal));
        }
        finally
        {
            stall.Set();
        }

        // Once the thread answers again, so do the displays.
        await platform.IdleAsync();
        await ListOk(api);
    }

    /// <summary>
    /// Requests while the desktop thread is stalled share one listing: each is answered 500 after its own 5 s (one that came
    /// 3 s later too), and once the thread recovers the system is asked at most once, so a stall leaves no listing per
    /// request queued behind it.
    /// </summary>
    [Fact]
    public async Task RequestsDuringAStall_EachWaitItsOwn5Seconds_AndLeaveAtMostOneListingBehind()
    {
        using var engine = new EngineFactory();
        using var platform = new FakeDesktopPlatform(BuiltIn);
        var clock = new ManualTime(new DateTimeOffset(2026, 10, 30, 9, 30, 0, TimeSpan.Zero));
        using var factory = WithPlatform(engine, platform, clock);
        using var api = Client(factory);
        await ListOk(api);

        // The clock moved on, so the only timers due 5 s after each request are the requests'.
        var start = TimeSpan.FromHours(1);
        clock.Advance(start);
        using var stall = new ManualResetEventSlim();
        platform.Post(() => stall.Wait(TimeSpan.FromSeconds(60)));
        try
        {
            var first = Enumerable.Range(0, 3).Select(_ => List(api)).ToList();
            await ArmedAsync(clock, start + TimeSpan.FromSeconds(5), first.Count);
            clock.Advance(TimeSpan.FromSeconds(3));
            var later = Enumerable.Range(0, 2).Select(_ => List(api)).ToList();
            await ArmedAsync(clock, start + TimeSpan.FromSeconds(8), later.Count);

            clock.Advance(TimeSpan.FromSeconds(2));
            foreach (var answer in first)
                Assert.Equal(HttpStatusCode.InternalServerError, (await answer.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Status);
            await Task.WhenAny(Task.WhenAll(later), Task.Delay(200, Ct));
            Assert.All(later, answer => Assert.False(answer.IsCompleted, "a later request was answered before its own 5 s"));

            clock.Advance(TimeSpan.FromSeconds(3));
            foreach (var answer in later)
                Assert.Equal(HttpStatusCode.InternalServerError, (await answer.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Status);

            var listed = platform.DisplaysListed;
            stall.Set();
            await platform.IdleAsync();
            var after = platform.DisplaysListed - listed;
            Assert.True(after <= 1, $"the system was asked for its displays {after} times after the stall");
        }
        finally
        {
            stall.Set();
        }

        await ListOk(api);
    }

    /// <summary>Waits until <paramref name="count"/> timers are due at <paramref name="due"/> (the requests are waiting).</summary>
    private static async Task ArmedAsync(ManualTime clock, TimeSpan due, int count)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (clock.TimersDueAt(due) < count)
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), $"{clock.TimersDueAt(due)} of {count} requests set a 5 s wait");
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task ASystemThatFailsToListItsDisplays_Is500_AndTheErrorIsLogged()
    {
        using var engine = new EngineFactory();
        using var platform = new FakeDesktopPlatform(BuiltIn);
        using var factory = WithPlatform(engine, platform);
        using var api = Client(factory);
        await ListOk(api);

        platform.DisplaysFailure = new InvalidOperationException("no displays to be had");
        var (status, body, mediaType) = await List(api);

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("application/problem+json", mediaType);
        Assert.True(Valid(body, "problem"));
        var logged = factory.Services.GetRequiredService<RecentLog>().After(0);
        Assert.Contains(logged, e => e.Level == "error" && e.Exception?.Contains("no displays to be had") == true);
    }

    /// <summary>
    /// Fill screen: one <c>profile</c> commit built from a display's entry (the profile's size its <c>width</c> and
    /// <c>height</c>, <c>window.x</c> and <c>window.y</c> 0, <c>window.display</c> its match) puts the window exactly over that
    /// display, on the primary and on displays away from the desktop's origin at either scale.
    /// </summary>
    [Fact]
    public async Task FillScreen_ACommitBuiltFromAnEntry_GivesTheWindowThatDisplaysFrame()
    {
        using var engine = new EngineFactory();
        using var platform = new FakeDesktopPlatform(BuiltIn, External, Side);
        using var factory = WithPlatform(engine, platform);
        using var api = Client(factory);
        var manager = factory.Services.GetRequiredService<DisplayWindowManager>();

        var document = TestDocs.Doc("Fill", 400, 300, TestDocs.Scene("a", 10, 10, TestDocs.Rect("#00ff00", 40, 20)));
        document["profile"]!["window"] = new JsonObject { ["enabled"] = true, ["topmost"] = true };
        var id = await TestDocs.CreateAsync(api, document, Ct);
        await platform.IdleAsync();

        var entries = (await ListOk(api)).GetProperty("displays").EnumerateArray().ToList();
        foreach (var (entry, display) in entries.Zip([BuiltIn, External, Side]))
        {
            var (name, width, height, x, y) = MatchOf(entry);
            var set = new JsonObject
            {
                ["width"] = width,
                ["height"] = height,
                ["window"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["topmost"] = true,
                    ["x"] = 0,
                    ["y"] = 0,
                    ["display"] = new JsonObject { ["name"] = name, ["width"] = width, ["height"] = height, ["x"] = x, ["y"] = y },
                },
            };
            using (var committed = await TestDocs.CommitAsync(api, id, Ct, TestDocs.Profile(set)))
                Assert.True(committed.StatusCode == HttpStatusCode.OK, await committed.Content.ReadAsStringAsync(Ct));
            await platform.IdleAsync();

            var window = await platform.InvokeAsync(() => manager.WindowOf(id) as FakeWindow) ?? throw new Xunit.Sdk.XunitException("No window");
            var frame = await platform.InvokeAsync(() => window.Frame);
            Assert.True(DisplayMatcher.SameFrame(display.Frame, frame), $"{display.Name}: the window is at {frame}, the display at {display.Frame}");
            Assert.True(await platform.InvokeAsync(() => window.Visible));
        }
    }
}
