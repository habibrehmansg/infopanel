using System.Diagnostics;
using System.Text.Json.Nodes;
using InfoPanel.Hosting;
using InfoPanel.Tests.Shell;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Hosting;

/// <summary>
/// A second launch: the user starts InfoPanel again on a data folder an engine holds. The first engine runs in process
/// (holding the lock and the folder's local channel, its browser launcher recording), the second is the built engine as
/// a child process on the same folder: it asks the first to open the editor, says so, and exits 3; with
/// <c>--no-browser</c> it asks nothing. The first engine serves no web client, so the editor it opens is its one cross
/// origin's pairing link.
/// </summary>
public sealed class SecondLaunchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<Program> First(EngineFactory engine, RecordingBrowserLauncher browser) =>
        engine.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Engine:WebRoot", "");
            builder.ConfigureTestServices(services => services.AddSingleton<BrowserLauncher>(browser));
        });

    [Fact]
    public async Task ASecondLaunch_OpensTheEditorInTheRunningEngine_AndExits3()
    {
        using var engine = new EngineFactory();
        var browser = new RecordingBrowserLauncher();
        using var first = First(engine, browser);
        await WaitForChannel(first);

        var (exit, said) = await Launch(engine.DataDir);

        Assert.Equal(3, exit);
        Assert.Contains("InfoPanel is already running; opened the editor", said);
        Assert.DoesNotContain(EngineFactory.Token, said);
        Assert.Equal([new EditorLink($"{EngineFactory.CrossOrigin}/#engine=http://127.0.0.1:{EngineFactory.Port}&token={EngineFactory.Token}", WebClient: true)],
            browser.Links);
    }

    [Fact]
    public async Task ASecondLaunch_WithNoBrowser_AsksNothing_AndExits3()
    {
        using var engine = new EngineFactory();
        var browser = new RecordingBrowserLauncher();
        using var first = First(engine, browser);
        await WaitForChannel(first);

        var (exit, said) = await Launch(engine.DataDir, "--no-browser");

        Assert.Equal(3, exit);
        Assert.DoesNotContain("opened the editor", said);
        Assert.Empty(browser.Links);
    }

    [Fact]
    public async Task TheChannel_OpensTheEditorOnlyForALaunch_NotForAnAiAppsFront()
    {
        using var engine = new EngineFactory();
        var browser = new RecordingBrowserLauncher();
        using var first = First(engine, browser);
        var endpoint = await WaitForChannel(first);

        await using (var front = await LocalChannelClient.TryConnectAsync(endpoint, TimeSpan.FromSeconds(5), Ct))
        {
            var answer = await front!.SendAsync(new JsonObject { ["op"] = "open-editor" }, Ct);
            Assert.NotNull(answer["error"]);
        }
        await using (var launch = await LocalChannelClient.TryConnectAsync(endpoint, TimeSpan.FromSeconds(5), Ct, client: LocalChannel.LaunchClient))
        {
            var list = await launch!.SendAsync(new JsonObject { ["op"] = "list" }, Ct);
            Assert.NotNull(list["error"]);
            Assert.Empty(browser.Links);
            var answer = await launch.SendAsync(new JsonObject { ["op"] = "open-editor" }, Ct);
            Assert.True(answer["opened"]!.GetValue<bool>());
        }

        Assert.Single(browser.Links);
    }

    private static async Task<LocalChannelEndpoint> WaitForChannel(WebApplicationFactory<Program> first)
    {
        var channel = first.Services.GetRequiredService<LocalChannelService>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (channel.Server == null)
        {
            Assert.True(DateTime.UtcNow < deadline, "The first engine's channel never opened.");
            await Task.Delay(20, Ct);
        }

        return channel.Server.Endpoint;
    }

    /// <summary>The built engine on <paramref name="data"/> as a second launch: its exit code and everything it said.</summary>
    private static async Task<(int Exit, string Said)> Launch(string data, params string[] extra)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "InfoPanel.exe" : "InfoPanel"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "--data-dir", data, "--v1-dir", data + "-v1", "--port", EngineRuntime.FindFreePort().ToString(), "--no-windows", "--no-tray", "--mock" }.Concat(extra))
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var errors = process.StandardError.ReadToEndAsync(Ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }

        return (process.ExitCode, await output + await errors);
    }
}
