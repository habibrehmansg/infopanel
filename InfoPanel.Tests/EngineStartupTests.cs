using System.Net;
using System.Net.Sockets;
using InfoPanel.Hosting;
using InfoPanel.Models;
using InfoPanel.Sensors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests;

public class EngineStartupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LogFile_HasTheSignInLinkRedacted_AndFoldersAreOwnerOnly()
    {
        using var factory = new EngineFactory();
        using (var client = factory.CreateApiClient())
        using (var response = await client.GetAsync("/api/session", Ct))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var logs = Path.Combine(factory.DataDir, "logs");
        var text = "";
        await WaitUntil(() =>
        {
            text = string.Join("\n", Directory.GetFiles(logs).Select(ReadShared));
            return text.Contains("Open the web client");
        });

        Assert.Contains("/auth/<token>", text);
        Assert.DoesNotContain(EngineFactory.Token, text);
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(ownerOnly, File.GetUnixFileMode(factory.DataDir));
            Assert.Equal(ownerOnly, File.GetUnixFileMode(logs));
        }
    }

    [Fact]
    public async Task ThumbnailWhoseItemsFailToDraw_StillRenders()
    {
        using var engine = new EngineFactory();
        using var factory = engine.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ISensorReader>(new ThrowingReader())));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new("Bearer", EngineFactory.Token);

        using var list = await client.GetAsync("/api/profiles", Ct);
        var id = System.Text.Json.JsonDocument.Parse(await list.Content.ReadAsStringAsync(Ct))
            .RootElement[0].GetProperty("id").GetString();

        // Every sensor-bound item throws while drawing: each is skipped, the frame still renders.
        using var response = await client.GetAsync($"/api/profiles/{id}/thumbnail", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

        // The document's lock was released: the profile still reads.
        using var doc = await client.GetAsync($"/api/profiles/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, doc.StatusCode);
    }

    [Fact]
    public async Task ConfiguredKestrelEndpointsAndUrls_AreIgnored_OnlyLoopbackIsBound()
    {
        var port = EngineRuntime.FindFreePort();
        var lanPort = EngineRuntime.FindFreePort();
        var urlsPort = EngineRuntime.FindFreePort();
        using var factory = new KestrelEngineFactory(port, new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Lan:Url"] = $"http://0.0.0.0:{lanPort}",
            ["urls"] = $"http://0.0.0.0:{urlsPort}",
            ["http_ports"] = urlsPort.ToString(),
        });
        factory.UseKestrel();
        factory.StartServer();

        var addresses = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Assert.Equal([$"http://127.0.0.1:{port}"], addresses);

        Assert.True(await CanConnect(port));
        Assert.False(await CanConnect(lanPort));
        Assert.False(await CanConnect(urlsPort));
    }

    private static async Task<bool> CanConnect(int port)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port, Ct);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail("Timed out waiting for the condition.");
            await Task.Delay(20, Ct);
        }
    }

    private sealed class ThrowingReader : ISensorReader
    {
        public bool TryRead(SensorKey key, out SensorReading reading) => throw new InvalidOperationException("sensor failure");
    }

    /// <summary>The engine on real Kestrel, with extra (hostile) configuration layered on.</summary>
    private sealed class KestrelEngineFactory(int port, IDictionary<string, string?> extra) : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "infopanel-kestrel-tests-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Engine:DataDir", Path.Combine(_root, "data"));
            builder.UseSetting("Engine:Token", EngineFactory.Token);
            builder.UseSetting("Engine:Port", port.ToString());
            builder.UseSetting("Engine:NoBrowser", "true");
            builder.UseSetting("Engine:MockSeed", "42");
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(extra));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
