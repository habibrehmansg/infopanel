using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using InfoPanel.Hosting;
using InfoPanel.Scenes;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// The PC's regional format on macOS comes from System Settings whatever the launch (spec/docs/api.md, "Session"): the
/// built engine started with <c>LANG=de_DE.UTF-8</c> (as a terminal sets it) and again with no locale variable at all (as
/// the Dock or Finder starts it) reports the same <c>session.locale</c>, the format CoreFoundation gives this test. Opt-in
/// with <c>INFOPANEL_REGION_TESTS=1</c> (macOS only), since what it reads is this Mac's settings; skipped otherwise.
/// </summary>
public sealed class RegionalLaunchTests
{
    private const string Token = "regional-launch-token";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool Enabled => OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("INFOPANEL_REGION_TESTS") == "1";

    private static readonly string[] LocaleVariables = ["LANG", "LANGUAGE", "LC_ALL", "LC_MESSAGES", "LC_NUMERIC", "LC_TIME", "LC_CTYPE"];

    [Fact]
    public async Task TheSessionReportsSystemSettingsFormat_WhateverTheLaunchEnvironment()
    {
        Assert.SkipUnless(Enabled, "Reads this Mac's regional settings: set INFOPANEL_REGION_TESTS=1 on macOS to run it.");
        var expected = PcRegionalFormat.Read();
        var fromTerminal = await SessionLocale(new Dictionary<string, string> { ["LANG"] = "de_DE.UTF-8" });
        var fromDock = await SessionLocale([]);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"CoreFoundation: {expected}; LANG=de_DE.UTF-8: {fromTerminal}; no locale variables: {fromDock}");
        var want = (expected.Tag, expected.DecimalSeparator, expected.GroupSeparator);
        Assert.Equal(want, fromTerminal);
        Assert.Equal(want, fromDock);
    }

    /// <summary>Starts the built engine with no locale variables but <paramref name="locale"/> and reads its session's <c>locale</c>.</summary>
    private static async Task<(string Tag, string Decimal, string Group)> SessionLocale(Dictionary<string, string> locale)
    {
        var root = Path.Combine(Path.GetTempPath(), "infopanel-regional-tests-" + Guid.NewGuid().ToString("N"));
        var port = EngineRuntime.FindFreePort();
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "InfoPanel"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "--port", port.ToString(), "--data-dir", Path.Combine(root, "data"), "--v1-dir", Path.Combine(root, "v1"), "--token", Token, "--no-browser", "--no-windows", "--no-tray", "--mock" })
            start.ArgumentList.Add(arg);
        foreach (var name in LocaleVariables) start.Environment.Remove(name);
        foreach (var (name, value) in locale) start.Environment[name] = value;

        using var engine = Process.Start(start)!;
        var output = engine.StandardOutput.ReadToEndAsync(Ct);
        var errors = engine.StandardError.ReadToEndAsync(Ct);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(2) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        try
        {
            var deadline = Environment.TickCount64 + 30_000;
            while (!engine.HasExited && Environment.TickCount64 < deadline)
            {
                try
                {
                    using var response = await http.GetAsync("/api/session", Ct);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        var l = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("locale");
                        return (l.GetProperty("tag").GetString()!, l.GetProperty("decimalSeparator").GetString()!, l.GetProperty("groupSeparator").GetString()!);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !Ct.IsCancellationRequested)
                {
                }
                await Task.Delay(100, Ct);
            }
        }
        finally
        {
            if (!engine.HasExited)
                engine.Kill(entireProcessTree: true);
            await engine.WaitForExitAsync(Ct);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Assert.Fail($"The engine didn't answer its session within 30 s:\n{await output}{await errors}");
        return default;
    }
}
