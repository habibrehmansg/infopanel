using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace InfoPanel.Tests.Mcp;

/// <summary>The launch record (<see cref="LaunchRecord"/>): what it keeps of a start, and what it gives back.</summary>
public sealed class LaunchRecordTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipl-" + Guid.NewGuid().ToString("N")[..8]);

    public LaunchRecordTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Record => Path.Combine(_root, "0123456789abcdef.launch.json");

    private EngineOptions Options(params string[] args)
    {
        var parsed = EngineCommandLine.Parse(args);
        Assert.Null(parsed.Error);
        var options = EngineOptions.Load(new ConfigurationBuilder().AddInMemoryCollection(parsed.Settings).Build(), out var errors);
        Assert.True(options != null, string.Join("; ", errors));
        return options!;
    }

    [Fact]
    public void OnlyAllowlistedFlagsAreWritten_NeverTheTokenOrFresh_OwnerOnly()
    {
        var web = Directory.CreateDirectory(Path.Combine(_root, "web")).FullName;
        var text = Directory.CreateDirectory(Path.Combine(_root, "text")).FullName;
        var data = Path.Combine(_root, "data");
        var options = Options("--data-dir", data, "--port", "5300", "--web-root", web, "--mcp-text", text, "--token", "secret-token-value",
            "--fresh", "--no-browser", "--no-windows", "--mock-seed", "7", "--v1-dir", Path.Combine(_root, "v1"),
            "--dev-origin", "http://localhost:5180", "--allow-origin", "https://client.example");
        LaunchRecord.Write(Record, options);

        var json = File.ReadAllText(Record);
        var record = JsonNode.Parse(json)!;
        Assert.Equal(1, record["launch"]!.GetValue<int>());
        var args = record["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Equal(
            ["--port", "5300", "--web-root", web, "--mcp-text", text, "--v1-dir", Path.Combine(_root, "v1"), "--mock-seed", "7", "--no-windows",
             "--dev-origin", "http://localhost:5180", "--allow-origin", "https://client.example"], args);
        Assert.DoesNotContain("secret-token-value", json);
        Assert.DoesNotContain("--token", json);
        Assert.DoesNotContain("--fresh", json);
        Assert.DoesNotContain("--no-browser", json);
        Assert.DoesNotContain("--data-dir", json);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Record));

        Assert.Equal(args, LaunchRecord.Read(Record, message => Assert.Fail(message)));
    }

    [Fact]
    public void AHandEditedRecord_GivesBackOnlyAllowlistedFlags_AndSaysWhatItSkipped()
    {
        File.WriteAllText(Record, new JsonObject
        {
            ["launch"] = 1,
            ["args"] = new JsonArray("--fresh", "--token", "stolen", "--mock", "--data-dir", "/elsewhere", "--Engine:RunDir=/x", "--unknown",
                "--web-root", "/w", "--allow-origin", "--no-windows", "--urls=http://0.0.0.0:80"),
        }.ToJsonString());

        var warnings = new List<string>();
        var args = LaunchRecord.Read(Record, warnings.Add);
        Assert.Equal(["--mock", "--web-root", "/w", "--no-windows"], args);
        Assert.Contains(warnings, w => w.Contains("--fresh"));
        Assert.Contains(warnings, w => w.Contains("--token"));
        Assert.Contains(warnings, w => w.Contains("stolen")); // the token's value isn't taken for a flag's
        Assert.Contains(warnings, w => w.Contains("--data-dir"));
        Assert.Contains(warnings, w => w.Contains("--unknown"));
        Assert.Contains(warnings, w => w.Contains("--allow-origin"));
        Assert.Contains(warnings, w => w.Contains("--urls"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"launch":2,"args":["--mock"]}""")]
    [InlineData("""{"launch":1,"args":"--mock"}""")]
    [InlineData("""{"launch":1,"args":["--mock",3]}""")]
    [InlineData("[]")]
    public void ARecordThatDoesntRead_GivesNoFlags(string content)
    {
        File.WriteAllText(Record, content);
        var warnings = new List<string>();
        Assert.Empty(LaunchRecord.Read(Record, warnings.Add));
        Assert.Single(warnings);
    }

    /// <summary>Codex p1 #5: a version of the wrong type is a record this engine doesn't read, never an exception.</summary>
    [Theory]
    [InlineData("""{"launch":"1","args":["--mock"]}""")]
    [InlineData("""{"launch":true,"args":["--mock"]}""")]
    [InlineData("""{"launch":null,"args":["--mock"]}""")]
    [InlineData("""{"launch":[1],"args":["--mock"]}""")]
    [InlineData("""{"launch":{"v":1},"args":["--mock"]}""")]
    [InlineData("""{"launch":1.5,"args":["--mock"]}""")]
    [InlineData("""{"launch":1e400,"args":["--mock"]}""")]
    [InlineData("""{"args":["--mock"]}""")]
    public void AVersionOfTheWrongType_GivesNoFlags_AndSaysSo(string content)
    {
        File.WriteAllText(Record, content);
        var warnings = new List<string>();
        Assert.Empty(LaunchRecord.Read(Record, warnings.Add));
        Assert.Contains("isn't one this engine reads", Assert.Single(warnings));
    }

    [Fact]
    public void ARecordOverItsCap_OrNotAFile_OrMissing_GivesNoFlags()
    {
        File.WriteAllText(Record, """{"launch":1,"args":["--mock"],"pad":""" + "\"" + new string('x', LaunchRecord.MaxBytes) + "\"}");
        var warnings = new List<string>();
        Assert.Empty(LaunchRecord.Read(Record, warnings.Add));
        Assert.Single(warnings);

        Assert.Empty(LaunchRecord.Read(Path.Combine(_root, "missing.launch.json"), message => Assert.Fail(message)));

        if (!OperatingSystem.IsWindows())
        {
            var fifo = Path.Combine(_root, "fifo.launch.json");
            using (var mkfifo = System.Diagnostics.Process.Start("/usr/bin/mkfifo", fifo))
                mkfifo.WaitForExit();
            var read = Task.Run(() => LaunchRecord.Read(fifo, _ => { }));
            Assert.True(read.Wait(TimeSpan.FromSeconds(5)), "A FIFO blocked the read.");
            Assert.Empty(read.Result);
        }
    }

    [Fact]
    public void TheMcpFlag_TakesOnlyTheDataFolder()
    {
        var parsed = EngineCommandLine.Parse(["--mcp", "--data-dir", "/d"]);
        Assert.Null(parsed.Error);
        Assert.Equal("true", parsed.Settings["Engine:Mcp"]);
        Assert.Equal("/d", parsed.Settings["Engine:DataDir"]);
    }
}
