using System.Diagnostics;
using System.Text.Json.Nodes;
using InfoPanel.CrashReporting;
using Sentry;
using Xunit;

namespace InfoPanel.Tests.CrashReports;

/// <summary>
/// The reports' lifecycle (docs/2.0/crash-reports.md, "Turning it off"): nothing queued is sent unless reporting was on
/// when it was queued and has been on since, in a build with a crash-report address or without one, though the old queue
/// couldn't be deleted or read, nothing but the settings could be written, or the old start wrote after it was stopped
/// (each consent's queue is its own folder); and stopping ends within the engine's deadline, a send that never ends
/// included, after which nothing more is sent. A fatal stop ends the session as crashed.
/// </summary>
[Collection(CrashReportsCollection.Name)]
public sealed class CrashReportsLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An event's id as the envelope writes it: found in what was sent whatever the event's text became.</summary>
    private static string IdOf(SentryId id) => id.ToString();

    private static int Sent(CrashReportsKit kit, string id) => kit.Transport.Envelopes.Count(e => e.Contains(id, StringComparison.Ordinal));

    [Fact]
    public async Task AQueueThatCouldntBeDropped_IsNeverSent_ThoughTheNextStartHasReportsOn()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Windows: a folder's read-only attribute doesn't stop a file in it being deleted.");
        using var kit = new CrashReportsKit();
        kit.Transport.Failing = true;
        var first = kit.Start()!;
        var queued = IdOf(SentrySdk.CaptureMessage("queued before the opt-out"));
        Assert.True(await kit.Transport.Until(t => t.Failed >= 1 && kit.Queued().Count >= 1));
        first.Dispose();

        // The next start has reports off, and the queue's folder can't be written: the drop fails.
        kit.Set(false);
        using (kit.ReadOnlyQueue())
        {
            kit.Start()!.Dispose();
            Assert.NotEmpty(kit.Queued());
        }

        // The folder is writable again and reports are on at the next start, the network back: the old queue never goes,
        // and the old folder goes at that start.
        kit.Set(true);
        kit.Transport.Failing = false;
        var next = kit.Start()!;
        next.Flush(TimeSpan.FromSeconds(5));
        await Task.Delay(500, Ct);
        Assert.Equal(0, Sent(kit, queued));
        Assert.Empty(kit.Queued());
        Assert.True(next.IsOn);
    }

    [Fact]
    public async Task TurnedOnAgain_WhileTheOldQueueCantBeDropped_ReportsStartAfresh_AndTheOldQueueIsNeverSent()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Windows: a folder's read-only attribute doesn't stop a file in it being deleted.");
        using var kit = new CrashReportsKit();
        kit.Transport.Failing = true;
        var reports = kit.Start()!;
        var queued = IdOf(SentrySdk.CaptureMessage("queued before the opt-out"));
        Assert.True(await kit.Transport.Until(t => t.Failed >= 1 && kit.Queued().Count >= 1));

        using (kit.ReadOnlyQueue())
        {
            // Turned off: the drop fails, which is harmless.
            kit.Set(false);
            Assert.NotEmpty(kit.Queued());
            kit.Transport.Failing = false;

            // Turned on again while the old queue still can't be dropped: reports start, and only what is new goes.
            kit.Set(true);
            Assert.True(reports.IsOn);
            var fresh = IdOf(SentrySdk.CaptureMessage("after it was turned on again"));
            reports.Flush(TimeSpan.FromSeconds(5));
            Assert.True(await kit.Transport.Until(_ => Sent(kit, fresh) == 1));
        }

        await Task.Delay(500, Ct);
        Assert.Equal(0, Sent(kit, queued));

        // A later change of the switch drops the old queue, now it can be.
        kit.Set(false);
        Assert.Empty(kit.Queued());
        kit.Set(true);
        reports.Flush(TimeSpan.FromSeconds(5));
        await Task.Delay(500, Ct);
        Assert.Equal(0, Sent(kit, queued));
    }

    [Fact]
    public async Task AnOldQueueThatCantBeRead_IsNeverSent_AfterReportsAreTurnedOffAndOnAgain()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Windows: a folder's Unix mode doesn't stop it being listed.");
        using var kit = new CrashReportsKit();
        kit.Transport.Failing = true;
        var release = kit.Start()!;
        var queued = IdOf(SentrySdk.CaptureMessage("queued before the opt-out"));
        Assert.True(await kit.Transport.Until(t => t.Failed >= 1 && kit.Queued().Count >= 1));
        release.Dispose();

        // Reports off; a build without an address starts while the old queue can't even be listed.
        kit.Set(false);
        using (CrashReportsKit.Mode(kit.SdkFolders(), UnixFileMode.None))
            kit.Start(dsn: null)?.Dispose();

        // The queue can be read again; reports on; a release build, the network back: the old queue never goes.
        kit.Set(true);
        kit.Transport.Failing = false;
        var next = kit.Start()!;
        Assert.True(next.IsOn);
        next.Flush(TimeSpan.FromSeconds(5));
        await Task.Delay(500, Ct);
        Assert.Equal(0, Sent(kit, queued));
    }

    [Fact]
    public async Task AnOldQueue_IsNeverSent_ThoughNothingButTheSettingsCouldBeWritten_WhenReportsWereTurnedOffAndOn()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Windows: a folder's read-only attribute doesn't stop a file in it being made or deleted.");
        using var kit = new CrashReportsKit();
        kit.Transport.Failing = true;

        // While the switch changes, every write but the settings' fails: once the settings are written, the data folder
        // and the queue's folders are made read-only (a handler before the reports'), the data folder writable again after.
        var faults = false;
        var locked = new List<IDisposable>();
        void Lock(InfoPanel.Settings.SettingsSnapshot _)
        {
            if (faults)
                locked.Add(CrashReportsKit.Mode([kit.Root, .. kit.QueueFolders()], UnixFileMode.UserRead | UnixFileMode.UserExecute));
        }

        void Unlock(InfoPanel.Settings.SettingsSnapshot _)
        {
            if (faults)
                File.SetUnixFileMode(kit.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        kit.Settings.Changed += Lock;
        var reports = kit.Start()!;
        kit.Settings.Changed += Unlock;
        var queued = IdOf(SentrySdk.CaptureMessage("queued before the opt-out"));
        Assert.True(await kit.Transport.Until(t => t.Failed >= 1 && kit.Queued().Count >= 1));

        faults = true;
        try
        {
            kit.Set(false);
            kit.Set(true);
            reports.Dispose();
        }
        finally
        {
            faults = false;
            kit.Settings.Changed -= Lock;
            kit.Settings.Changed -= Unlock;
            foreach (var each in locked)
                each.Dispose();
        }

        // A restart, the faults gone and the network back, reports on: nothing from before the opt-out goes.
        kit.Transport.Failing = false;
        var next = kit.Start()!;
        Assert.True(next.IsOn);
        next.Flush(TimeSpan.FromSeconds(5));
        await Task.Delay(500, Ct);
        Assert.Equal(0, Sent(kit, queued));
    }

    [Fact]
    public async Task ASessionAnOldStartWritesAfterReportsWereTurnedOffAndOn_IsNeverSent()
    {
        using var kit = new CrashReportsKit();
        var reports = kit.Start()!;
        Assert.True(await kit.Transport.Until(_ => kit.SessionFiles().Count == 1));
        var file = kit.SessionFiles()[0];
        var old = await File.ReadAllBytesAsync(file, Ct);
        var sid = SidOf(JsonNode.Parse(old));
        Assert.NotNull(sid);

        // Off, then on again; the run ends. Then the old start's capture, paused before, finishes: the library writes its
        // session where that start kept it (its own folder).
        kit.Set(false);
        kit.Set(true);
        Assert.True(reports.IsOn);
        reports.Dispose();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, old, Ct);

        // The next start, reports on: the old session (its start sent while it was on) never goes again, as recovered.
        var sentBefore = Sessions(kit).Count(s => s["sid"]?.GetValue<string>() == sid);
        var next = kit.Start()!;
        Assert.True(next.IsOn);
        next.Flush(TimeSpan.FromSeconds(5));
        await Task.Delay(500, Ct);
        Assert.Equal(sentBefore, Sessions(kit).Count(s => s["sid"]?.GetValue<string>() == sid));
        Assert.DoesNotContain(Sessions(kit), s => s["sid"]?.GetValue<string>() == sid && s["status"]?.GetValue<string>() is "abnormal" or "crashed");
    }

    /// <summary>The first <c>sid</c> in <paramref name="node"/>, at any depth.</summary>
    private static string? SidOf(JsonNode? node) => node switch
    {
        JsonObject o when o["sid"] is JsonValue v && v.TryGetValue<string>(out var sid) => sid,
        JsonObject o => o.Select(p => SidOf(p.Value)).FirstOrDefault(s => s != null),
        JsonArray a => a.Select(SidOf).FirstOrDefault(s => s != null),
        _ => null,
    };

    [Fact]
    public async Task TurningReportsOff_InABuildWithoutAnAddress_DropsWhatAReleaseBuildQueued()
    {
        using var kit = new CrashReportsKit();
        kit.Transport.Failing = true;
        var release = kit.Start()!;
        var queued = IdOf(SentrySdk.CaptureMessage("queued by a release build"));
        Assert.True(await kit.Transport.Until(t => t.Failed >= 1 && kit.Queued().Count >= 1));
        release.Dispose();

        // A build without an address (a build from source, or --offline): the switch turned off drops the queue all the same.
        var source = kit.Start(dsn: null);
        kit.Set(false);
        Assert.Empty(kit.Queued());
        kit.Set(true);
        source?.Dispose();

        // Back on a release build, reports on, the network there: nothing from before the opt-out goes.
        kit.Transport.Failing = false;
        var next = kit.Start()!;
        next.Flush(TimeSpan.FromSeconds(5));
        await Task.Delay(500, Ct);
        Assert.Equal(0, Sent(kit, queued));
    }

    [Fact]
    public async Task ABuildWithoutAnAddress_StartingWithReportsOff_DropsWhatAReleaseBuildQueued()
    {
        using var kit = new CrashReportsKit();
        kit.Transport.Failing = true;
        var release = kit.Start()!;
        SentrySdk.CaptureMessage("queued by a release build");
        Assert.True(await kit.Transport.Until(t => t.Failed >= 1 && kit.Queued().Count >= 1));
        release.Dispose();

        kit.Set(false);
        kit.Start(dsn: null)?.Dispose();

        Assert.Empty(kit.Queued());
    }

    [Fact]
    public async Task TurnedOff_WhileASendNeverEnds_ReturnsWithinTheDeadline_AndNothingQueuedThenIsEverSent()
    {
        using var kit = new CrashReportsKit();
        var deadline = TimeSpan.FromSeconds(1);
        var reports = kit.Start(stopDeadline: deadline)!;
        try
        {
            // The session's start goes first; then the connection hangs.
            Assert.True(await kit.Transport.Until(t => t.Items().Any(i => i.Type == "session")));
            kit.Transport.Hanging = true;
            var inFlight = IdOf(SentrySdk.CaptureMessage("in flight"));
            Assert.True(await kit.Transport.Until(t => Sent(kit, inFlight) == 1));
            var queued = IdOf(SentrySdk.CaptureMessage("queued behind it"));
            await Task.Delay(200, Ct);

            var watch = Stopwatch.StartNew();
            var off = Task.Run(() => kit.Set(false), Ct);
            Assert.True(off.Wait(TimeSpan.FromSeconds(10), Ct), "turning reports off never returned");
            Assert.True(watch.Elapsed < deadline + TimeSpan.FromSeconds(2), $"turning reports off took {watch.Elapsed}");
            Assert.False(reports.IsOn);

            // The connection ends, and reports are turned on again: neither report goes (again).
            var sent = kit.Transport.Envelopes.Count;
            kit.Transport.Hanging = false;
            kit.Transport.Release();
            await Task.Delay(500, Ct);
            Assert.Equal(sent, kit.Transport.Envelopes.Count);
            kit.Set(true);
            Assert.True(reports.IsOn);
            reports.Flush(TimeSpan.FromSeconds(5));
            await Task.Delay(500, Ct);
            Assert.Equal(1, Sent(kit, inFlight));
            Assert.Equal(0, Sent(kit, queued));
        }
        finally
        {
            kit.Transport.Release();
        }
    }

    [Fact]
    public async Task Stopped_WhileASendNeverEnds_ReturnsWithinTheDeadline()
    {
        using var kit = new CrashReportsKit();
        var deadline = TimeSpan.FromSeconds(1);
        var reports = kit.Start(stopDeadline: deadline)!;
        try
        {
            // The session's start goes first; then the connection hangs.
            Assert.True(await kit.Transport.Until(t => t.Items().Any(i => i.Type == "session")));
            kit.Transport.Hanging = true;
            var inFlight = IdOf(SentrySdk.CaptureMessage("in flight"));
            Assert.True(await kit.Transport.Until(t => Sent(kit, inFlight) == 1));

            var watch = Stopwatch.StartNew();
            var stop = Task.Run(reports.Dispose, Ct);
            Assert.True(stop.Wait(TimeSpan.FromSeconds(10), Ct), "stopping never returned");
            Assert.True(watch.Elapsed < deadline + TimeSpan.FromSeconds(2), $"stopping took {watch.Elapsed}");
            Assert.False(reports.IsOn);
        }
        finally
        {
            kit.Transport.Release();
        }
    }

    [Fact]
    public async Task AFatalStop_EndsTheSessionAsCrashed_NotExited()
    {
        using var kit = new CrashReportsKit();
        var reports = kit.Start()!;
        reports.Flush(TimeSpan.FromSeconds(5));

        // The engine's fatal catch (Kestrel couldn't bind, say): the run ends on an error.
        reports.Stop(fatal: true);

        Assert.True(await kit.Transport.Until(_ => Sessions(kit).Any(s => s["status"]?.GetValue<string>() is "crashed" or "exited")));
        Assert.Contains(Sessions(kit), s => s["status"]?.GetValue<string>() == "crashed");
        Assert.DoesNotContain(Sessions(kit), s => s["status"]?.GetValue<string>() == "exited");
    }

    /// <summary>A connection that never answers, whatever its cancellation says, counting the requests that reach it.</summary>
    private sealed class NeverAnswers : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return new TaskCompletionSource<HttpResponseMessage>().Task;
        }
    }

    [Fact]
    public async Task TheLibrarysOwnConnection_IsAbandonedWhenItsStartIsRetired_AndNoneIsMadeAfter()
    {
        // A release build's sending: the library's HTTP connection, innermost, behind the start's generation.
        var generation = new SendGeneration();
        var network = new NeverAnswers();
        using var client = new HttpClient(new GatedHandler(generation, network));

        var inFlight = client.PostAsync("https://o1.ingest.example.invalid/api/1/envelope/", new StringContent("x"), Ct);
        await Task.Delay(100, Ct);
        generation.Retire();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight.WaitAsync(TimeSpan.FromSeconds(5), Ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PostAsync("https://o1.ingest.example.invalid/api/1/envelope/", new StringContent("y"), Ct));
        Assert.Equal(1, network.Requests);
    }

    [Fact]
    public void ALinkedEpochFolder_IsNotStartedOn_ThisRun_AndNothingIsSent()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Windows: a symbolic link needs a privilege the tests don't hold.");
        using var kit = new CrashReportsKit();
        kit.Set(true);
        var epoch = kit.Settings.CrashReportsEpoch()!;
        // The epoch's folder is a link to a retired queue elsewhere.
        var elsewhere = Directory.CreateDirectory(Path.Combine(kit.Root, "elsewhere")).FullName;
        Directory.CreateDirectory(kit.ReportsFolder);
        Directory.CreateSymbolicLink(CrashReporting.CrashReports.EpochFolderOf(kit.Paths, epoch), elsewhere);

        var reports = kit.Start()!;
        Assert.False(reports.IsOn);
        Assert.False(SentrySdk.IsEnabled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        Assert.Empty(kit.Transport.Envelopes);
    }

    [Fact]
    public void ALinkedReportsFolder_IsNotStartedOn_ThisRun()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Windows: a symbolic link needs a privilege the tests don't hold.");
        using var kit = new CrashReportsKit();
        kit.Set(true);
        var elsewhere = Directory.CreateDirectory(Path.Combine(kit.Root, "elsewhere")).FullName;
        Directory.CreateSymbolicLink(kit.ReportsFolder, elsewhere);

        var reports = kit.Start()!;
        Assert.False(reports.IsOn);
        Assert.False(SentrySdk.IsEnabled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
    }

    [Fact]
    public void TheCurrentEpochsFolder_InAnotherCase_SurvivesTheSweep()
    {
        using var kit = new CrashReportsKit();
        kit.Set(true);
        var epoch = kit.Settings.CrashReportsEpoch()!;
        // A case-insensitive filesystem names the active folder as it was made, which may differ from the epoch's case.
        var folder = Path.Combine(kit.ReportsFolder, epoch.ToUpperInvariant());
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "kept.txt");
        File.WriteAllText(file, "x");

        kit.Start(dsn: null)!.Dispose();
        Assert.True(File.Exists(file));
    }

    private static List<JsonNode> Sessions(CrashReportsKit kit) => [.. kit.Transport.Items().Where(i => i.Type == "session").Select(i => i.Item)];
}
