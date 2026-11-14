using System.Diagnostics;
using InfoPanel.AiUsage;
using Xunit;

namespace InfoPanel.Tests.Plugins.AiUsage;

// The wrapper itself, run by bash as Claude Code runs a status line: a stub command for the user's (never the real
// claude), in a fixture home with the Claude folder named by the test, never the environment's.
public sealed class WrapperTests : IDisposable
{
    private readonly FixtureHome _home = new();

    public void Dispose() => _home.Dispose();

    private static readonly DateTimeOffset Now = Fixtures.Now;

    private static string Input(string? rateLimits) => Fixtures.StatusLine(rateLimits);

    private static UnixFileMode ModeOf(string path) => OperatingSystem.IsWindows() ? throw new PlatformNotSupportedException() : File.GetUnixFileMode(path);

    private static void SkipOnWindows() => Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows: the wrapper runs through Git Bash there, checked on a PC.");

    /// <summary>
    /// Runs <paramref name="script"/> (a file) with bash, <paramref name="input"/> on its stdin, the home the fixture's and
    /// <c>CLAUDE_CONFIG_DIR</c> <paramref name="configDir"/> (unset when null), with <paramref name="extra"/> set too.
    /// </summary>
    private (string Out, string Err, int Exit) Run(string script, string input, string? configDir, params (string Name, string Value)[] extra)
    {
        var start = new ProcessStartInfo("bash")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(script);
        start.Environment["HOME"] = _home.Path;
        start.Environment.Remove("CLAUDE_CONFIG_DIR");
        if (configDir != null)
            start.Environment["CLAUDE_CONFIG_DIR"] = configDir;
        foreach (var (name, value) in extra)
            start.Environment[name] = value;
        using var bash = Process.Start(start)!;
        var output = bash.StandardOutput.ReadToEndAsync();
        var error = bash.StandardError.ReadToEndAsync();
        try
        {
            bash.StandardInput.Write(input);
            bash.StandardInput.Close();
        }
        catch (IOException)
        {
            // A script that doesn't read its input (a status line may not) can end before it is written: a closed pipe.
        }
        if (!bash.WaitForExit(30_000))
        {
            // Never left behind: a wrapper blocked on a FIFO would wait for ever.
            bash.Kill(entireProcessTree: true);
            Assert.Fail("bash didn't end within 30 s");
        }
        return (output.Result, error.Result, bash.ExitCode);
    }

    /// <summary>The wrapper for <paramref name="command"/>, written into the fixture home; its path.</summary>
    private string Write(string? command)
    {
        var path = Path.Combine(_home.Path, "wrapper-" + Guid.NewGuid().ToString("N")[..8] + ".sh");
        File.WriteAllText(path, Wrapper.Script(command)!);
        return path;
    }

    [Fact]
    public void The_users_output_stderr_and_exit_code_pass_through_and_the_usage_file_is_written()
    {
        SkipOnWindows();
        var claude = _home.ClaudeFolder(".claude-work");
        var seen = Path.Combine(_home.Path, "stdin-seen.json");
        var input = Input(Fixtures.ClaudeBoth(24, Now.AddHours(4), 45, Now.AddDays(2)));
        var script = Write("cat > \"$STUB_SEEN\"; printf 'line from the command\\n'; printf 'to stderr\\n' >&2; exit 7");

        var (output, error, exit) = Run(script, input, claude, ("STUB_SEEN", seen));

        Assert.Equal(("line from the command\n", "to stderr\n", 7), (output, error, exit));
        Assert.Equal(input, File.ReadAllText(Path.Combine(claude, LimitsReader.FileName)));
        Assert.Equal(input, File.ReadAllText(seen));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(Path.Combine(claude, LimitsReader.FileName)));
    }

    [Fact]
    public void Without_CLAUDE_CONFIG_DIR_the_usage_file_goes_to_the_homes_claude_folder_and_a_missing_folder_is_no_error()
    {
        SkipOnWindows();
        var claude = _home.ClaudeFolder(".claude");
        var input = Input(null);
        var script = Write("echo ok");

        Assert.Equal(("ok\n", "", 0), Run(script, input, configDir: null));
        Assert.Equal(input, File.ReadAllText(Path.Combine(claude, LimitsReader.FileName)));

        Assert.Equal(("ok\n", "", 0), Run(script, input, Path.Combine(_home.Path, "no-such-folder")));
    }

    [Fact]
    public void With_no_status_line_before_the_minimal_line_shows_the_limits()
    {
        SkipOnWindows();
        var claude = _home.ClaudeFolder(".claude");
        var script = Write(null);

        Assert.Equal(("5h 24% · wk 45%\n", "", 0), Run(script, Input(Fixtures.ClaudeBoth(24.6, Now.AddHours(4), 45, Now.AddDays(2))), claude));
        Assert.Equal(("5h 7% · wk –\n", "", 0), Run(script, Input(Fixtures.ClaudeFiveHourOnly(7, Now.AddHours(4))), claude));
        Assert.Equal(("5h – · wk –\n", "", 0), Run(script, Input(null), claude));
        Assert.True(File.Exists(Path.Combine(claude, LimitsReader.FileName)));
    }

    [Fact]
    public void A_command_with_quotes_dollars_and_backslashes_runs_as_it_would_alone_once_Connect_wraps_it()
    {
        SkipOnWindows();
        const string command = "printf '%s|%s|%s\\n' \"it's \\\"quoted\\\"\" '$HOME stays' \"back\\\\slash \\$5\"; echo \"$((2+3))\"";
        var claude = _home.ClaudeFolder(".claude");
        File.WriteAllText(Path.Combine(claude, "settings.json"), $$"""{ "statusLine": { "type": "command", "command": {{System.Text.Json.JsonSerializer.Serialize(command)}} } }""");

        new ClaudeConnect(_home.Environment, new ConnectPlaces([], Windows: false)).Connect(InfoPanel.AiUsage.Folders.FindClaude(_home.Environment), readOn: true);

        var alone = Path.Combine(_home.Path, "alone.sh");
        File.WriteAllText(alone, command + "\n");
        var expected = "it's \"quoted\"|$HOME stays|back\\slash $5\n5\n";
        Assert.Equal((expected, "", 0), Run(alone, Input(null), claude));
        Assert.Equal((expected, "", 0), Run(Path.Combine(claude, Wrapper.FileName), Input(null), claude));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\n\n")]
    public void The_users_command_gets_the_input_byte_for_byte(string end)
    {
        SkipOnWindows();
        var claude = _home.ClaudeFolder(".claude");
        var seen = Path.Combine(_home.Path, "stdin-seen.json");
        var input = Input(null) + end;
        var script = Write("cat > \"$STUB_SEEN\"");

        Assert.Equal(("", "", 0), Run(script, input, claude, ("STUB_SEEN", seen)));

        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(input), File.ReadAllBytes(seen));
    }

    [Fact]
    public void Where_the_usage_file_cant_be_made_a_command_that_doesnt_read_its_input_still_ends_with_no_error()
    {
        SkipOnWindows();
        // More than a pipe holds, so what hands the input to the command is still writing when the command ends.
        var input = new string('a', 200_000);

        Assert.Equal(("ok\n", "", 0), Run(Write("echo ok"), input, Path.Combine(_home.Path, "no-such-folder")));
    }

    [Fact]
    public void A_usage_file_that_is_a_link_is_replaced_and_what_it_names_is_never_written()
    {
        SkipOnWindows();
        var claude = _home.ClaudeFolder(".claude");
        var settings = Path.Combine(claude, "settings.json");
        const string mine = "{ \"model\": \"opus\" }\n";
        File.WriteAllText(settings, mine);
        var usage = Path.Combine(claude, LimitsReader.FileName);
        File.CreateSymbolicLink(usage, settings);
        var input = Input(null);

        Assert.Equal(("ok\n", "", 0), Run(Write("echo ok"), input, claude));

        Assert.Equal(mine, File.ReadAllText(settings));
        Assert.Null(new FileInfo(usage).LinkTarget);
        Assert.Equal(input, File.ReadAllText(usage));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(usage));
        Assert.Equal(["projects"], Directory.GetFileSystemEntries(claude).Select(Path.GetFileName).Where(n => n != "settings.json" && n != LimitsReader.FileName));
    }

    [Fact]
    public void An_existing_usage_file_others_may_read_is_replaced_by_a_private_one()
    {
        SkipOnWindows();
        var claude = _home.ClaudeFolder(".claude");
        var usage = Path.Combine(claude, LimitsReader.FileName);
        File.WriteAllText(usage, "old");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(usage,UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Assert.Equal(("ok\n", "", 0), Run(Write("echo ok"), Input(null), claude));

        Assert.Equal(Input(null), File.ReadAllText(usage));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(usage));
    }

    [Fact]
    public void A_usage_file_that_is_a_FIFO_never_blocks_the_users_command()
    {
        SkipOnWindows();
        var claude = _home.ClaudeFolder(".claude");
        var usage = Path.Combine(claude, LimitsReader.FileName);
        Fifo.Make(usage);

        Assert.Equal(("ok\n", "", 0), Run(Write("echo ok"), Input(null), claude));

        Assert.Equal(Input(null), File.ReadAllText(usage));
    }

    [Fact]
    public void A_FIFO_at_a_temporary_name_a_guess_would_pick_never_blocks_the_users_command()
    {
        SkipOnWindows();
        var claude = _home.ClaudeFolder(".claude");
        // Run in the wrapper's own shell before it (BASH_ENV): a FIFO at the name built from its process id and its next
        // two $RANDOMs (seeded, then seeded again alike). A temporary file made by mktemp never opens it.
        var plant = Path.Combine(_home.Path, "plant.sh");
        File.WriteAllText(plant, "RANDOM=1; __n=$RANDOM$RANDOM; RANDOM=1\nmkfifo \"$CLAUDE_CONFIG_DIR/." + LimitsReader.FileName + ".$$.$__n\"\nunset __n\n");

        Assert.Equal(("ok\n", "", 0), Run(Write("echo ok"), Input(null), claude, ("BASH_ENV", plant)));

        Assert.Equal(Input(null), File.ReadAllText(Path.Combine(claude, LimitsReader.FileName)));
        var others = Directory.GetFileSystemEntries(claude).Select(Path.GetFileName).Where(n => n != "projects" && n != LimitsReader.FileName).ToList();
        Assert.Single(others);
        Assert.StartsWith("." + LimitsReader.FileName + ".", others[0]);
    }
}
