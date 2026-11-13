using System.Reflection;
using System.Text.Json.Nodes;
using InfoPanel.CrashReporting;
using InfoPanel.Plugins;
using Sentry;
using Xunit;

namespace InfoPanel.Tests.CrashReports;

/// <summary>
/// When crash reports send (docs/2.0/crash-reports.md), through a capturing transport: no crash-report address, nothing,
/// whatever the setting; the setting off at start, the SDK never started and no id made; turned off and on at run time;
/// the install's id random, kept, and the one sessions count; a run that didn't shut down reported at the next start; a
/// plugin host's exit without its name; logged errors limited, plugin hosts' never; the log's breadcrumbs templates only.
/// </summary>
[Collection(CrashReportsCollection.Name)]
public sealed class CrashReportsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Text(JsonNode? node) => node?.ToJsonString() ?? "";

    /// <summary>The event's message, in whichever member the SDK writes it (the protocol's logentry, or message).</summary>
    private static string MessageOf(JsonNode? @event) => Text(@event?["logentry"]) + Text(@event?["message"]);

    [Fact]
    public void ABuildFromSource_CarriesNoCrashReportAddress()
    {
        // This test's engine is a build from source: no InfoPanelCrashReportsDsn.
        Assert.Null(CrashReporting.CrashReports.BuiltInDsn());
        Assert.Null(CrashReporting.CrashReports.BuiltInDsn(typeof(Program).Assembly));
        var targets = File.ReadAllText(Path.Combine(Path.GetDirectoryName(typeof(CrashReportsTests).Assembly.Location)!, "..", "..", "..", "..", "InfoPanel", "CrashReportsDsn.targets"));
        Assert.Contains("'$(InfoPanelCrashReportsDsn)' != ''", targets, StringComparison.Ordinal);
        Assert.Contains($"<_Parameter1>{CrashReporting.CrashReports.DsnMetadata}</_Parameter1>", targets, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoAddress_StartsNothing_WhateverTheSetting()
    {
        using var kit = new CrashReportsKit();

        // The reports follow the switch in every build (turned off, they drop a queue a release build left), but without an
        // address the SDK never starts.
        foreach (var none in new[] { kit.Start(dsn: null), kit.Start(dsn: " ") })
        {
            Assert.NotNull(none);
            Assert.False(none.HasAddress);
            Assert.False(none.IsOn);
            kit.Set(false);
            kit.Set(true);
            Assert.False(none.IsOn);
            none.Dispose();
        }

        kit.Start(dsn: null);
        Assert.False(SentrySdk.IsEnabled);
        kit.Logger("InfoPanel.Hosting.Test").Error(new InvalidOperationException("boom"), "Failed");
        SentrySdk.CaptureMessage("not sent");
        await Task.Delay(200, Ct);
        Assert.Empty(kit.Transport.Envelopes);
        Assert.False(File.Exists(CrashReporting.CrashReports.FileOf(kit.Paths)));
        Assert.False(Directory.Exists(Path.Combine(kit.Root, CrashReporting.CrashReports.FolderName)));
    }

    [Fact]
    public async Task OffAtStart_StartsNoSdk_MakesNoId_SendsNothing_ThenOnSends_AndOffAgainStops()
    {
        using var kit = new CrashReportsKit();
        kit.Set(false);

        var reports = kit.Start()!;

        Assert.False(reports.IsOn);
        Assert.False(SentrySdk.IsEnabled);
        Assert.Null(reports.InstallId);
        Assert.False(File.Exists(CrashReporting.CrashReports.FileOf(kit.Paths)));
        kit.Logger("InfoPanel.Hosting.Test").Error(new InvalidOperationException("while off"), "Failed while off");
        await Task.Delay(200, Ct);
        Assert.Empty(kit.Transport.Envelopes);

        // Turned on at run time (a PATCH): the SDK starts and reports.
        kit.Set(true);
        Assert.True(reports.IsOn);
        kit.Logger("InfoPanel.Hosting.Test").Error(new InvalidOperationException("while on"), "Failed while on");
        kit.Flush();
        Assert.True(await kit.Transport.Until(t => t.Events().Count == 1));
        Assert.Contains("Failed while on", Text(kit.Transport.Events()[0]), StringComparison.Ordinal);

        // Turned off: closed; nothing more is reported.
        kit.Set(false);
        Assert.False(reports.IsOn);
        Assert.False(SentrySdk.IsEnabled);
        var sent = kit.Transport.Envelopes.Count;
        kit.Logger("InfoPanel.Hosting.Test").Error(new ArgumentException("after off"), "Failed after off");
        SentrySdk.CaptureMessage("after off");
        await Task.Delay(300, Ct);
        Assert.Equal(sent, kit.Transport.Envelopes.Count);
    }

    [Fact]
    public async Task OnByDefault_TheInstallIdIsRandom_Kept_AndTheOneSessionsAndEventsCarry()
    {
        string first;
        using (var kit = new CrashReportsKit())
        {
            var reports = kit.Start()!;
            Assert.True(reports.IsOn);
            first = reports.InstallId!;
            Assert.True(Guid.TryParse(first, out _));
            Assert.Equal(first, JsonNode.Parse(File.ReadAllText(CrashReporting.CrashReports.FileOf(kit.Paths)))!["installId"]!.GetValue<string>());
            SentrySdk.CaptureMessage("one");
            kit.Flush();
            Assert.True(await kit.Transport.Until(t => t.Events().Count == 1 && t.Items().Any(i => i.Type == "session")));
            Assert.Equal(first, kit.Transport.Events()[0]["user"]!["id"]!.GetValue<string>());
            // The SDK counts sessions by its installation id, never the scope's user: seeded with ours, never the MAC address.
            Assert.All(kit.Transport.Items().Where(i => i.Type == "session"), session => Assert.Equal(first, session.Item["did"]!.GetValue<string>()));

            // The same install keeps its id across starts.
            reports.Dispose();
            Assert.Equal(first, kit.Start()!.InstallId);
        }

        using var other = new CrashReportsKit();
        Assert.NotEqual(first, other.Start()!.InstallId);
    }

    [Fact]
    public async Task ARunThatDidntShutDown_IsReportedAtTheNextStart_Once()
    {
        using var kit = new CrashReportsKit();
        File.WriteAllText(kit.Paths.RuntimeFile, """{"pid":999999,"port":5299,"url":"http://127.0.0.1:5299"}""");

        var reports = kit.Start()!;
        // Sent first: one still queued when reports are turned off is dropped, never sent later.
        reports.Flush(TimeSpan.FromSeconds(5));
        Assert.True(await kit.Transport.Until(t => t.Events().Count >= 1));
        kit.Set(false);
        kit.Set(true);
        reports.Flush(TimeSpan.FromSeconds(5));
        await Task.Delay(300, Ct);
        var unclean = Assert.Single(kit.Transport.Events());
        Assert.Contains(CrashReporting.CrashReports.UncleanExitMessage, MessageOf(unclean), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunThatShutDown_IsNotReported()
    {
        using var kit = new CrashReportsKit();
        var reports = kit.Start()!;
        reports.Flush(TimeSpan.FromSeconds(5));
        await Task.Delay(300, Ct);
        Assert.Empty(kit.Transport.Events());
    }

    [Fact]
    public async Task APluginHostsExit_IsOneWarning_WithItsExitCodeHowLongItRanAndItsRestarts_AndNoName()
    {
        using var kit = new CrashReportsKit();
        var reports = kit.Start()!;

        CrashReporting.CrashReports.PluginHostExited(139, TimeSpan.FromMinutes(5), 2);
        reports.Flush(TimeSpan.FromSeconds(5));

        Assert.True(await kit.Transport.Until(t => t.Events().Count == 1));
        var exit = kit.Transport.Events()[0];
        Assert.Equal("warning", exit["level"]!.GetValue<string>());
        Assert.Contains(CrashReporting.CrashReports.PluginHostExitMessage, MessageOf(exit), StringComparison.Ordinal);
        Assert.Equal(("139", "1 to 10 min", "2"), (exit["tags"]!["exit_code"]!.GetValue<string>(), exit["tags"]!["ran"]!.GetValue<string>(), exit["tags"]!["restarts"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ALoggedPluginHostException_OrAnErrorFromThePluginHosts_SendsNothing()
    {
        using var kit = new CrashReportsKit();
        var reports = kit.Start()!;

        kit.Logger("InfoPanel.Hosting.Test").Error(new PluginHostException("InfoPanel.Spotify: its last words"), "A plugin failed");
        kit.Logger("InfoPanel.Hosting.Test").Error(new InvalidOperationException("outer", new PluginHostException("inner")), "A plugin failed inside");
        kit.Logger("InfoPanel.Plugins.PluginHostConnection").Error(new InvalidOperationException("InfoPanel.Spotify says no"), "Its host failed");
        reports.Flush(TimeSpan.FromSeconds(5));
        await Task.Delay(300, Ct);

        Assert.Empty(kit.Transport.Events());
    }

    [Fact]
    public async Task LoggedErrors_AreOnePerTypeAndFrameAnHour_AndAtMost20ARun()
    {
        using var kit = new CrashReportsKit();
        var reports = kit.Start()!;
        var log = kit.Logger("InfoPanel.Hosting.Test");

        // The same failure every second: one report.
        for (var i = 0; i < 5; i++)
            log.Error(Thrown(() => throw new InvalidOperationException($"sensor read {i}")), "A sensor failed");
        // Thirty other exception types: at most 20 in all.
        foreach (var type in typeof(Exception).Assembly.GetTypes().Where(t => typeof(Exception).IsAssignableFrom(t) && t.IsPublic && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null).Take(30))
            log.Error((Exception)Activator.CreateInstance(type)!, "Failed with {Type}", type.Name);
        reports.Flush(TimeSpan.FromSeconds(5));
        await kit.Transport.Until(t => t.Events().Count >= CrashReporting.CrashReports.MaxLoggedPerRun);
        await Task.Delay(300, Ct);

        Assert.Equal(CrashReporting.CrashReports.MaxLoggedPerRun, kit.Transport.Events().Count);
        Assert.Single(kit.Transport.Events(), e => MessageOf(e).Contains("A sensor failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLogsBreadcrumbs_AreTemplatesOnly_InformationAndAbove_NeverAPropertysValue()
    {
        using var kit = new CrashReportsKit();
        var reports = kit.Start()!;
        var log = kit.Logger("InfoPanel.Documents.Workspace");

        log.Debug("Read {Path} for {User}", "/Users/habib/Documents/Rig.infopanel", "habib");
        log.Information("Loaded {Count} profiles from {Path} on {Computer}", 3, "/Users/habib/Documents/Rig.infopanel", "Habibs-MacBook-Pro");
        log.Warning("Sensor {Name} of {Source} at {Address} stopped: {Mac} {Email} {Token}", "CPU Package", "habibs-mac.tail1234.ts.net", "192.168.1.23", "a4:83:e7:12:34:56", "owner@example.com", "Tq3vX9kLm2Np8Rs5Wy7Zb4Cd6Fg1Hj0Kl3Mn5Pq7Rs9u");
        log.Error(Thrown(() => throw new InvalidOperationException("x")), "Profile {Name} failed", "Gaming Rig");
        reports.Flush(TimeSpan.FromSeconds(5));

        Assert.True(await kit.Transport.Until(t => t.Events().Count == 1));
        var crumbs = kit.Transport.Events()[0]["breadcrumbs"]!.AsArray().Select(b => b!["message"]!.GetValue<string>()).ToList();
        Assert.Contains("Loaded {Count} profiles from {Path} on {Computer}", crumbs);
        Assert.Contains("Sensor {Name} of {Source} at {Address} stopped: {Mac} {Email} {Token}", crumbs);
        Assert.DoesNotContain(crumbs, c => c.StartsWith("Read ", StringComparison.Ordinal));
        var text = kit.Transport.AllText();
        foreach (var value in new[] { "habib", "Habibs-MacBook-Pro", "CPU Package", "Gaming Rig", "tail1234", "192.168.1.23", "a4:83:e7", "owner@example.com", "Tq3vX9kLm2Np8Rs5" })
            Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AReportQueuedWhileTheNetworkFails_IsNeverSent_AfterReportsAreTurnedOff_ThoughTheNetworkReturnsAndReportsAreTurnedOnAgain()
    {
        using var kit = new CrashReportsKit();
        kit.Transport.Failing = true;
        var reports = kit.Start()!;
        // Found by its id: its message never leaves the engine.
        var queued = SentrySdk.CaptureMessage("queued while the network was down").ToString();
        Assert.True(await kit.Transport.Until(t => t.Failed >= 1 && kit.Queued().Count >= 1));

        // Turned off: the queue is dropped, kept nowhere to be sent later.
        kit.Set(false);
        Assert.Empty(kit.Queued());
        Assert.Empty(kit.SessionFiles());
        Assert.True(File.Exists(CrashReporting.CrashReports.FileOf(kit.Paths)));

        // The network returns, and reports are turned on again: nothing from before the opt-out goes.
        kit.Transport.Failing = false;
        kit.Set(true);
        Assert.True(reports.IsOn);
        reports.Flush(TimeSpan.FromSeconds(5));
        await Task.Delay(500, Ct);
        Assert.DoesNotContain(queued, kit.Transport.AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReportQueuedWhileTheNetworkFails_IsNeverSent_WhenTheNextStartHasReportsOff_EvenOnceTurnedOn()
    {
        using var kit = new CrashReportsKit();
        kit.Transport.Failing = true;
        var first = kit.Start()!;
        var queued = SentrySdk.CaptureMessage("queued before the restart").ToString();
        Assert.True(await kit.Transport.Until(t => t.Failed >= 1 && kit.Queued().Count >= 1));
        first.Dispose();
        Assert.NotEmpty(kit.Queued());

        // The next start, with reports off: what the last run left is dropped before anything could send it.
        kit.Set(false);
        var next = kit.Start()!;
        Assert.False(next.IsOn);
        Assert.Empty(kit.Queued());

        kit.Transport.Failing = false;
        kit.Set(true);
        next.Flush(TimeSpan.FromSeconds(5));
        await Task.Delay(500, Ct);
        Assert.DoesNotContain(queued, kit.Transport.AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderTheReportsCantWrite_AtStartOrWhenTurnedOn_LeavesTheEngineRunning_AndTheSettingAsStored()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Windows: a folder's read-only attribute doesn't stop a folder being made in it.");
        using var kit = new CrashReportsKit();
        // A kept id, and the reports' folder made read-only: the SDK's folder can't be made in it.
        var id = Guid.NewGuid().ToString("D");
        File.WriteAllText(CrashReporting.CrashReports.FileOf(kit.Paths), $$"""{"installId":"{{id}}"}""");
        Directory.CreateDirectory(kit.ReportsFolder);
        using (CrashReportsKit.Mode([kit.ReportsFolder], UnixFileMode.UserRead | UnixFileMode.UserExecute))
        {
            var reports = kit.Start()!;
            Assert.False(reports.IsOn);
            Assert.False(SentrySdk.IsEnabled);
            Assert.True(kit.Settings.Current.Settings.Telemetry.CrashReports);

            // Off, then on again (a PATCH): the change is stored, the engine runs on, nothing is thrown.
            kit.Set(false);
            kit.Set(true);
            Assert.True(kit.Settings.Current.Settings.Telemetry.CrashReports);
            Assert.False(reports.IsOn);
            Assert.False(SentrySdk.IsEnabled);
            kit.Logger("InfoPanel.Hosting.Test").Error(new InvalidOperationException("still running"), "Failed");
            reports.Dispose();
        }
    }

    [Fact]
    public async Task TheNamesTheReportsRemove_AreReadWhenAReportIsMade_AndOnesSeenThisRunStay_ThoughALaterReadFails()
    {
        using var kit = new CrashReportsKit();
        var names = new List<KnownValue>();
        var fails = false;
        var reports = kit.Start(names: () => fails ? throw new InvalidOperationException("can't read the profiles now") : [.. names]);
        var log = kit.Logger("InfoPanel.Hosting.Test");

        // Messages never leave the engine; the names are the second net over what does, a log template (here one a caller
        // wrote a name into, as an interpolated template would).
        // A profile made after the start: its name is removed from the very next report, not a minute later.
        names.Add(new("Secret Rig", "<profile>"));
        log.Error(new InvalidOperationException("x"), "Could not render Secret Rig now");
        // The read fails: what was known stays known.
        fails = true;
        log.Error(new ArgumentException("x"), "Secret Rig failed again");
        // Renamed: the old name, seen this run, is still removed.
        fails = false;
        names.Clear();
        names.Add(new("Other Rig", "<profile>"));
        log.Error(new FormatException("x"), "Secret Rig and Other Rig");
        reports!.Flush(TimeSpan.FromSeconds(5));

        Assert.True(await kit.Transport.Until(t => t.Events().Count == 3));
        var text = kit.Transport.AllText();
        Assert.DoesNotContain("Secret Rig", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Other Rig", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<profile>", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANamesReadThatTakesTooLong_IsKept_WhenItEnds_ForTheReportsAfter()
    {
        using var kit = new CrashReportsKit();
        var reads = 0;
        var reports = kit.Start(names: () =>
        {
            // The first read is slow (a busy workspace) and finds a new profile; every later one fails.
            if (Interlocked.Increment(ref reads) == 1)
            {
                Thread.Sleep(CrashReporting.CrashReports.NamesWait * 3);
                return [new("Late Rig", "<profile>")];
            }

            throw new InvalidOperationException("can't read the profiles now");
        })!;
        var log = kit.Logger("InfoPanel.Hosting.Test");

        log.Error(new InvalidOperationException("x"), "Render failed");
        await Task.Delay(CrashReporting.CrashReports.NamesWait * 4, Ct);
        log.Error(new ArgumentException("x"), "Could not render Late Rig");
        reports.Flush(TimeSpan.FromSeconds(5));

        Assert.True(await kit.Transport.Until(t => t.Events().Count == 2));
        Assert.DoesNotContain("Late Rig", kit.Transport.AllText(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Could not render <profile>", kit.Transport.AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnobservedTaskException_IsReported_AndTheSessionGoesOn_NotEndedAsCrashed()
    {
        using var kit = new CrashReportsKit();
        var reports = kit.Start()!;

        for (var i = 0; i < 50 && !kit.Transport.Events().Any(e => Text(e).Contains("UnobservedTaskException", StringComparison.Ordinal)); i++)
        {
            LeaveAFaultedTask();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            reports.Flush(TimeSpan.FromSeconds(1));
        }

        var unobserved = Assert.Single(kit.Transport.Events(), e => Text(e).Contains("UnobservedTaskException", StringComparison.Ordinal));
        Assert.False(unobserved["exception"]!["values"]!.AsArray().Last()!["mechanism"]!["handled"]!.GetValue<bool>());
        Assert.DoesNotContain(Sessions(kit), s => s["status"]?.GetValue<string>() == "crashed");
        // The engine runs on: its session is still open, and a clean stop ends it (as unhandled, not crashed).
        reports.Dispose();
        Assert.True(await kit.Transport.Until(_ => Sessions(kit).Any(s => s["status"]?.GetValue<string>() is "exited" or "unhandled")));
        Assert.DoesNotContain(Sessions(kit), s => s["status"]?.GetValue<string>() == "crashed");
    }

    [Fact]
    public async Task ACleanShutdown_EndsTheSessionAsExited_SoTheNextStartRecoversNoAbnormalOne()
    {
        using var kit = new CrashReportsKit();
        var reports = kit.Start()!;
        reports.Flush(TimeSpan.FromSeconds(5));

        reports.Dispose();

        Assert.True(await kit.Transport.Until(_ => Sessions(kit).Any(s => s["status"]?.GetValue<string>() == "exited")));
        Assert.Empty(kit.SessionFiles());
        var next = kit.Start()!;
        next.Flush(TimeSpan.FromSeconds(5));
        await Task.Delay(300, Ct);
        Assert.DoesNotContain(Sessions(kit), s => s["status"]?.GetValue<string>() is "abnormal" or "crashed");
    }

    private static List<JsonNode> Sessions(CrashReportsKit kit) => [.. kit.Transport.Items().Where(i => i.Type == "session").Select(i => i.Item)];

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void LeaveAFaultedTask()
    {
        var task = Task.Run(() => throw new InvalidOperationException("nobody waits for this"));
        ((IAsyncResult)task).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("2.0.0", "production")]
    [InlineData("2.0.0-beta.1", "beta")]
    [InlineData("2.0.1-dev.ci.42", "ci")]
    [InlineData("2.0.0-dev", "development")]
    public void TheEnvironment_IsTheVersions(string version, string environment)
    {
        Assert.Equal(environment, CrashReporting.CrashReports.EnvironmentOf(version));
    }

    private static Exception Thrown(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("nothing thrown");
    }
}
