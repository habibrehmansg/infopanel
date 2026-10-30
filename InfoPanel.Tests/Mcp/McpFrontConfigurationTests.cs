using Xunit;

namespace InfoPanel.Tests.Mcp;

/// <summary>
/// The engine's settings in the AI app's environment (<c>Engine__*</c>) reach neither the front nor the engine it starts:
/// the front reads only the data folder and its own host settings, and the engine it starts gets only the launch record's
/// flags, the data folder and <c>--no-browser</c>, with <c>--fresh=false</c> said outright.
/// </summary>
[Collection(McpHarnessCollection.Name)]
public sealed class McpFrontConfigurationTests
{
    private const string Settings = """{"format":"infopanel.settings/1","frameRate":20}""";

    [Fact]
    public async Task AnInheritedFreshSetting_NeverWipesTheDataFolder_OnAnAutomaticStart()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var folder = new HarnessFolder();
        folder.RecordQuietStart();
        Directory.CreateDirectory(folder.Data);
        var settings = Path.Combine(folder.Data, "settings.json");
        File.WriteAllText(settings, Settings); // the owner's data: --fresh would delete it

        await using var front = new McpHarness(folder.Data, environment: new Dictionary<string, string>
        {
            ["Engine__Fresh"] = "true",
            ["ASPNETCORE_Engine__Fresh"] = "true",
        });
        await front.InitializeAsync();
        // Any call starts the engine; one it answers itself (a tool it doesn't have) shows it is up.
        var text = McpHarness.TextOf(await front.CallAsync("no_such_tool"));
        Assert.Contains("wasn't running, so it was started", text);
        Assert.Contains("no_such_tool", text);

        Assert.True(File.Exists(settings), "The data folder was wiped by a read call.");
        Assert.Equal(Settings, File.ReadAllText(settings));
        var args = McpHarness.ArgumentsOf((await folder.PidAsync())!.Value);
        Assert.Contains("--fresh=false", args);
        front.AssertStdoutIsOnlyProtocol();
    }

    [Fact]
    public async Task OtherEngineSettingsInTheEnvironment_StopNeitherTheFrontNorTheEngineItStarts()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var folder = new HarnessFolder();
        folder.RecordQuietStart();
        await using var front = new McpHarness(folder.Data, environment: new Dictionary<string, string>
        {
            ["Engine__Port"] = "not-a-port",
            ["Engine__Token"] = "inherited token",
        });
        await front.InitializeAsync();
        // Any call starts the engine; one it answers itself (a tool it doesn't have) shows it is up.
        var text = McpHarness.TextOf(await front.CallAsync("no_such_tool"));
        Assert.Contains("wasn't running, so it was started", text);
        Assert.Contains("no_such_tool", text);
        front.AssertStdoutIsOnlyProtocol();
    }

    /// <summary>
    /// Codex p1 #2: a run folder others can write in (a shared folder named by <c>Engine__RunDir</c>) is never used: the
    /// call says why, and nothing is started there.
    /// </summary>
    [Fact]
    public async Task ARunFolderOthersCanWriteIn_IsNotUsed_AndTheCallSaysWhy()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var folder = new HarnessFolder();
        var shared = Path.Combine(Path.GetTempPath(), "ips-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(shared);
        try
        {
            File.SetUnixFileMode(shared, (UnixFileMode)0b111_111_111); // 0777, as a shared folder is
            await using var front = new McpHarness(folder.Data, startSeconds: 10, environment: new Dictionary<string, string>
            {
                [TestRunFolder.Variable] = shared,
            });
            await front.InitializeAsync();
            var result = await front.CallAsync("list_profiles");
            var text = McpHarness.TextOf(result);
            Assert.True(result["isError"]!.GetValue<bool>());
            Assert.Contains("isn't this user's alone", text);
            Assert.Contains("others can write", text);
            Assert.DoesNotContain("was started", text);
            Assert.Empty(Directory.EnumerateFileSystemEntries(shared));
            Assert.False(Directory.Exists(folder.Data));
            front.AssertStdoutIsOnlyProtocol();
        }
        finally
        {
            Directory.Delete(shared, recursive: true);
        }
    }
}
