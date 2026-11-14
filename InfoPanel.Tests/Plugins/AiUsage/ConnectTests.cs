using System.Text.Json;
using InfoPanel.AiUsage;
using InfoPanel.Plugins;
using Xunit;

namespace InfoPanel.Tests.Plugins.AiUsage;

// Connect Claude Code limits and Disconnect, on fixture homes only (never a real Claude folder or settings file): the
// settings file changed only in its status line, through links, safely against Claude Code writing it meanwhile, undone
// exactly, and the states and confirmations the person sees first.
public sealed class ConnectTests : IDisposable
{
    private readonly FixtureHome _home = new();

    public void Dispose() => _home.Dispose();

    private const string UserCommand = "bash ~/statusline/statusline-command.sh";

    /// <summary>A settings file as Claude Code writes one (2-space indentation, a final newline), its status line with refreshInterval and padding.</summary>
    private const string Written = """
        {
          "model": "opus",
          "statusLine": {
            "type": "command",
            "command": "bash ~/statusline/statusline-command.sh",
            "padding": 0,
            "refreshInterval": 5
          },
          "permissions": {
            "allow": [
              "Bash(ls:*)"
            ]
          },
          "env": {
            "SOME_FLAG": "é <b>"
          }
        }

        """;

    private static readonly UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static UnixFileMode ModeOf(string path) => OperatingSystem.IsWindows() ? throw new PlatformNotSupportedException() : File.GetUnixFileMode(path);

    private ClaudeConnect Connector(ConnectPlaces? places = null) => new(_home.Environment, places ?? new ConnectPlaces([], Windows: false));

    private IReadOnlyList<ClaudeFolder> Found() => InfoPanel.AiUsage.Folders.FindClaude(_home.Environment);

    private static string Real(string path) => ConnectFiles.Real(path)!;

    /// <summary>The status line command that runs the wrapper beside the settings file in <paramref name="folder"/> (real path).</summary>
    private static string WrapperCommandIn(string folder, string wrapper = Wrapper.FileName) => Wrapper.Command(Path.Combine(Real(folder), wrapper), windows: false)!;

    private static string? CommandOf(string settings)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(settings));
        return doc.RootElement.TryGetProperty("statusLine", out var line) && line.ValueKind == JsonValueKind.Object && line.TryGetProperty("command", out var command)
            ? command.GetString()
            : null;
    }

    private static string[] Backups(string folder, string name = "settings.json") =>
        Directory.GetFiles(folder, name + ClaudeConnect.BackupInfix + "*");

    private string Settings(string folder, string text)
    {
        var path = Path.Combine(folder, "settings.json");
        File.WriteAllText(path, text);
        return path;
    }

    private static void SkipOnWindows() => Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows: Connect waits for its check on a PC, and links need a privilege the tests don't have.");

    [Fact]
    public void Five_folders_linking_to_one_file_are_connected_by_one_change_and_every_link_stays()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var settings = Settings(main, Written);
        string[] others = [".claude-gg", ".claude-jq", ".claude-jw", ".claude-p"];
        foreach (var other in others)
            File.CreateSymbolicLink(Path.Combine(_home.ClaudeFolder(other), "settings.json"), settings);
        var connect = Connector();
        var folders = Found();
        Assert.Equal(5, folders.Count);

        var before = connect.DescribeConnect(folders, readOn: true);
        Assert.True(before.Enabled);
        Assert.Equal("Not connected · 5 Claude folders, 1 settings file", before.State);
        Assert.Equal("Connect Claude Code limits", before.Confirm!.Title);
        TestContext.Current.TestOutputHelper?.WriteLine($"{before.State}\n\n{before.Confirm.Title}\n\n{before.Confirm.Text}");
        Assert.StartsWith("Changes 1 settings file, shared by 5 Claude folders (.claude, .claude-gg, .claude-jq, .claude-jw, .claude-p): ~/.claude/settings.json.", before.Confirm.Text);
        Assert.Contains($"Your status line now: {UserCommand}\nAfter: bash ~/.claude/infopanel-statusline.sh, which runs your command unchanged", before.Confirm.Text);
        Assert.Contains("A backup is kept beside each file it changes (settings.json.infopanel-backup-{date}-{time}). Disconnect puts your status line back.", before.Confirm.Text);
        Assert.Contains("Overridden: none (no project or organisation setting sets a status line).", before.Confirm.Text);

        connect.Connect(folders, readOn: true);

        var wrapperCommand = WrapperCommandIn(main);
        Assert.Equal(wrapperCommand, CommandOf(settings));
        foreach (var other in others)
        {
            var link = new FileInfo(Path.Combine(_home.Path, other, "settings.json"));
            Assert.Equal(settings, link.LinkTarget);
            Assert.Equal(wrapperCommand, CommandOf(link.FullName));
        }
        Assert.False(new FileInfo(settings).LinkTarget != null);
        Assert.Single(Backups(main));
        Assert.Equal(Written, File.ReadAllText(Backups(main)[0]));
        Assert.Equal(Private, ModeOf(Backups(main)[0]));
        Assert.Equal(Private | UnixFileMode.UserExecute, ModeOf(Path.Combine(main, Wrapper.FileName)));
        Assert.Contains("eval 'bash ~/statusline/statusline-command.sh' <&3 3<&-", File.ReadAllText(Path.Combine(main, Wrapper.FileName)));
        Assert.Equal("Connected · 5 Claude folders", connect.DescribeConnect(folders, readOn: true).State);
        Assert.False(connect.DescribeConnect(folders, readOn: true).Enabled);

        var disconnect = connect.DescribeDisconnect(folders, readOn: true);
        Assert.True(disconnect.Enabled);
        Assert.StartsWith($"Puts back {UserCommand} in ~/.claude/settings.json.", disconnect.Confirm!.Text);
    }

    [Fact]
    public void Connect_changes_only_the_status_lines_command_and_Disconnect_puts_the_file_back_byte_for_byte()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var settings = Settings(main, Written);
        var connect = Connector();

        connect.Connect(Found(), readOn: true);

        using (var doc = JsonDocument.Parse(File.ReadAllText(settings)))
        {
            Assert.Equal(["model", "statusLine", "permissions", "env"], doc.RootElement.EnumerateObject().Select(p => p.Name));
            var line = doc.RootElement.GetProperty("statusLine");
            Assert.Equal(["type", "command", "padding", "refreshInterval"], line.EnumerateObject().Select(p => p.Name));
            Assert.Equal("command", line.GetProperty("type").GetString());
            Assert.Equal(0, line.GetProperty("padding").GetInt32());
            Assert.Equal(5, line.GetProperty("refreshInterval").GetInt32());
            Assert.Equal("é <b>", doc.RootElement.GetProperty("env").GetProperty("SOME_FLAG").GetString());
        }
        Assert.Equal(Written.Replace(UserCommand, WrapperCommandIn(main)), File.ReadAllText(settings));

        _home.Clock.Now = _home.Clock.Now.AddMinutes(1);
        connect.Disconnect(Found(), readOn: true);

        Assert.Equal(Written, File.ReadAllText(settings));
        Assert.False(File.Exists(Path.Combine(main, Wrapper.FileName)));
        Assert.False(File.Exists(Path.Combine(main, "infopanel-statusline.json")));
        Assert.Equal(2, Backups(main).Length);
        Assert.False(connect.DescribeDisconnect(Found(), readOn: true).Enabled);
        Assert.Equal("Not connected · 1 Claude folder, 1 settings file", connect.DescribeConnect(Found(), readOn: true).State);
    }

    [Fact]
    public void A_folder_with_no_settings_file_gets_one_with_only_the_minimal_status_line_and_Disconnect_takes_it_out()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var connect = Connector();

        var state = connect.DescribeConnect(Found(), readOn: true);
        Assert.Contains("It isn't there yet: it is made with only the status line.\nAfter: bash ~/.claude/infopanel-statusline.sh, a status line showing the limits (5h 24% · wk 45%)", state.Confirm!.Text);
        Assert.DoesNotContain("A backup is kept", state.Confirm.Text);

        connect.Connect(Found(), readOn: true);

        var settings = Path.Combine(main, "settings.json");
        var wrapperCommand = WrapperCommandIn(main);
        Assert.Equal($"{{\n  \"statusLine\": {{\n    \"type\": \"command\",\n    \"command\": \"{wrapperCommand}\"\n  }}\n}}\n", File.ReadAllText(settings));
        Assert.Equal(Private, ModeOf(settings));
        var script = File.ReadAllText(Path.Combine(main, Wrapper.FileName));
        Assert.DoesNotContain("eval", script);
        Assert.Contains("printf '5h %s · wk %s\\n'", script);
        Assert.Empty(Backups(main));

        connect.Disconnect(Found(), readOn: true);
        Assert.Equal("{}\n", File.ReadAllText(settings));
        Assert.False(File.Exists(Path.Combine(main, Wrapper.FileName)));
    }

    [Fact]
    public void A_file_that_isnt_plain_JSON_is_refused_and_nothing_is_written()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        const string withComments = "{\n  // my settings\n  \"statusLine\": { \"type\": \"command\", \"command\": \"x\" },\n}\n";
        var settings = Settings(main, withComments);
        var connect = Connector();

        var state = connect.DescribeConnect(Found(), readOn: true);
        Assert.False(state.Enabled);
        Assert.Equal("Can't connect: ~/.claude/settings.json: it isn't plain JSON (comments, trailing commas or something else); nothing changed", state.State);
        var failure = Assert.Throws<InvalidOperationException>(() => connect.Connect(Found(), readOn: true));
        Assert.Contains("~/.claude/settings.json: it isn't plain JSON", failure.Message);

        Assert.Equal(withComments, File.ReadAllText(settings));
        Assert.Equal(["settings.json"], Directory.GetFiles(main).Select(Path.GetFileName));
    }

    [Fact]
    public void A_change_between_the_read_and_the_rename_is_read_again_and_kept()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var settings = Settings(main, Written);
        var connect = Connector();
        var changes = 0;
        connect.BeforeReplace = real =>
        {
            // Claude Code writes the file (a new member) while Connect is about to rename over it: once.
            if (changes++ == 0)
                File.WriteAllText(real, Written.Replace("\"model\": \"opus\"", "\"model\": \"opus\",\n  \"theme\": \"dark\""));
        };

        connect.Connect(Found(), readOn: true);

        Assert.Equal(2, changes);
        using var doc = JsonDocument.Parse(File.ReadAllText(settings));
        Assert.Equal("dark", doc.RootElement.GetProperty("theme").GetString());
        Assert.Equal(WrapperCommandIn(main), CommandOf(settings));
        Assert.Single(Backups(main));
        Assert.Contains("\"theme\": \"dark\"", File.ReadAllText(Backups(main)[0]));
        Assert.Empty(Directory.GetFiles(main, ".*.tmp"));
    }

    [Fact]
    public void A_file_that_keeps_changing_is_left_as_its_writer_left_it_with_nothing_of_Connects()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var settings = Settings(main, Written);
        var connect = Connector();
        var changes = 0;
        connect.BeforeReplace = real => File.WriteAllText(real, Written.Replace("opus", $"opus-{++changes}"));

        var failure = Assert.Throws<InvalidOperationException>(() => connect.Connect(Found(), readOn: true));

        Assert.Contains($"it kept changing while Connect wrote it ({ClaudeConnect.Attempts} times); nothing changed there", failure.Message);
        Assert.Equal(ClaudeConnect.Attempts, changes);
        Assert.Equal(Written.Replace("opus", $"opus-{ClaudeConnect.Attempts}"), File.ReadAllText(settings));
        Assert.Equal(["settings.json"], Directory.GetFiles(main).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_write_after_the_last_read_and_before_the_rename_is_kept_and_the_change_made_again_from_it(bool byRename)
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var settings = Settings(main, Written);
        var connect = Connector();
        var edited = Written.Replace("\"model\": \"opus\"", "\"model\": \"opus\",\n  \"theme\": \"dark\"");
        var writes = 0;
        connect.AfterReread = real =>
        {
            // Claude Code writes the file after Connect's last read of it and before Connect's rename: once, through a
            // rename of its own (an atomic writer) or in place.
            if (writes++ != 0)
                return;
            if (byRename)
            {
                File.WriteAllText(real + ".claude-tmp", edited);
                File.Move(real + ".claude-tmp", real, overwrite: true);
            }
            else
            {
                File.WriteAllText(real, edited);
            }
        };

        connect.Connect(Found(), readOn: true);

        Assert.Equal(2, writes);
        Assert.Equal(edited.Replace(UserCommand, WrapperCommandIn(main)), File.ReadAllText(settings));
        Assert.Contains("\"theme\": \"dark\"", File.ReadAllText(Assert.Single(Backups(main))));
        Assert.Equal(["infopanel-statusline.json", Wrapper.FileName, "settings.json"],
            Directory.GetFiles(main).Select(Path.GetFileName).Where(n => !n!.Contains(ClaudeConnect.BackupInfix, StringComparison.Ordinal)).Order(StringComparer.Ordinal));

        connect.Disconnect(Found(), readOn: true);
        Assert.Equal(edited, File.ReadAllText(settings));
    }

    [Fact]
    public void Two_settings_files_in_one_folder_named_alike_keep_their_own_wrapper_and_record_and_Disconnect_puts_back_each_ones_own()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var work = _home.ClaudeFolder(".claude-work");
        var dotfiles = Directory.CreateDirectory(Path.Combine(_home.Path, "dotfiles")).FullName;
        var json = Path.Combine(dotfiles, "claude.json");
        var conf = Path.Combine(dotfiles, "claude.conf");
        var theirs = Written.Replace(UserCommand, "bash ~/work-statusline.sh");
        File.WriteAllText(json, Written);
        File.WriteAllText(conf, theirs);
        File.CreateSymbolicLink(Path.Combine(main, "settings.json"), json);
        File.CreateSymbolicLink(Path.Combine(work, "settings.json"), conf);
        var connect = Connector();

        connect.Connect(Found(), readOn: true);

        Assert.NotEqual(CommandOf(json), CommandOf(conf));
        Assert.Equal("Connected · 2 Claude folders", connect.DescribeConnect(Found(), readOn: true).State);
        var disconnect = connect.DescribeDisconnect(Found(), readOn: true).Confirm!.Text;
        Assert.Contains($"Puts back {UserCommand} in ~/dotfiles/claude.json.", disconnect);
        Assert.Contains("Puts back bash ~/work-statusline.sh in ~/dotfiles/claude.conf.", disconnect);

        connect.Disconnect(Found(), readOn: true);

        Assert.Equal(Written, File.ReadAllText(json));
        Assert.Equal(theirs, File.ReadAllText(conf));
        Assert.Equal(["claude.conf", "claude.json"], Directory.GetFiles(dotfiles).Select(Path.GetFileName).Where(n => !n!.Contains(ClaudeConnect.BackupInfix, StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Disconnect_refuses_a_record_that_names_another_settings_file()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var settings = Settings(main, Written);
        var connect = Connector();
        connect.Connect(Found(), readOn: true);
        var connected = File.ReadAllText(settings);
        var record = Path.Combine(main, "infopanel-statusline.json");
        File.WriteAllText(record, File.ReadAllText(record)
            .Replace(JsonSerializer.Serialize(Real(settings)), JsonSerializer.Serialize(Path.Combine(Real(main), "other.json")))
            .Replace(UserCommand, "bash ~/someone-elses.sh"));

        var failure = Assert.Throws<InvalidOperationException>(() => connect.Disconnect(Found(), readOn: true));

        Assert.Contains("another settings file", failure.Message);
        Assert.Equal(connected, File.ReadAllText(settings));
        Assert.True(File.Exists(Path.Combine(main, Wrapper.FileName)));
    }

    [Fact]
    public void A_connect_that_succeeds_during_another_ones_retry_keeps_the_wrapper_and_record_the_file_runs()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var settings = Settings(main, Written);
        var first = Connector();
        var second = Connector();
        var presses = 0;
        // A second press connects the file while the first is about to rename over it: the first reads it again, finds
        // it connected, and must not take away what it now runs.
        second.BeforeReplace = _ =>
        {
            if (presses++ == 0)
                first.Connect(Found(), readOn: true);
        };

        Assert.Equal("Already connected", second.Connect(Found(), readOn: true));

        Assert.Equal(WrapperCommandIn(main), CommandOf(settings));
        Assert.True(File.Exists(Path.Combine(main, Wrapper.FileName)));
        Assert.True(File.Exists(Path.Combine(main, "infopanel-statusline.json")));
        Assert.Equal("Connected · 1 Claude folder", second.DescribeConnect(Found(), readOn: true).State);
        second.Disconnect(Found(), readOn: true);
        Assert.Equal(Written, File.ReadAllText(settings));
    }

    [Theory]
    [InlineData("claude\"work.json")]
    [InlineData("claude'work.json")]
    [InlineData("claude\\work.json")]
    public void A_retry_keeps_the_wrapper_and_record_the_file_runs_when_JSON_escapes_their_name(string name)
    {
        SkipOnWindows();
        // The wrapper is named after the settings file, so its name in the file is escaped JSON, never its bytes as is.
        var main = _home.ClaudeFolder(".claude");
        var dotfiles = Directory.CreateDirectory(Path.Combine(_home.Path, "dotfiles")).FullName;
        var target = Path.Combine(dotfiles, name);
        File.WriteAllText(target, Written);
        File.CreateSymbolicLink(Path.Combine(main, "settings.json"), target);
        var named = new SettingsFile(Real(target), [], SettingsState.Absent, null, null);
        var first = Connector();
        var second = Connector();
        var presses = 0;
        second.BeforeReplace = _ =>
        {
            if (presses++ == 0)
                first.Connect(Found(), readOn: true);
        };

        Assert.Equal("Already connected", second.Connect(Found(), readOn: true));

        Assert.Equal(WrapperCommandIn(dotfiles, Path.GetFileName(named.WrapperPath)), CommandOf(target));
        Assert.True(File.Exists(named.WrapperPath));
        Assert.True(File.Exists(named.RecordPath));
        Assert.Equal("Connected · 1 Claude folder", second.DescribeConnect(Found(), readOn: true).State);
        second.Disconnect(Found(), readOn: true);
        Assert.Equal(Written, File.ReadAllText(target));
    }

    [Fact]
    public void Disconnect_puts_back_the_command_and_keeps_what_the_user_changed_in_the_status_line_since()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var settings = Settings(main, Written);
        var connect = Connector();
        connect.Connect(Found(), readOn: true);
        File.WriteAllText(settings, File.ReadAllText(settings).Replace("\"padding\": 0", "\"padding\": 8"));

        connect.Disconnect(Found(), readOn: true);

        Assert.Equal(Written.Replace("\"padding\": 0", "\"padding\": 8"), File.ReadAllText(settings));
    }

    [Fact]
    public void A_file_the_change_would_take_past_the_largest_read_is_refused_and_nothing_is_written()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        const string head = "{\"statusLine\":{\"type\":\"command\",\"command\":\"x\"},\"big\":\"";
        const string tail = "\"}";
        var text = head + new string('a', ClaudeConnect.MaxSettingsBytes - head.Length - tail.Length - 8) + tail;
        var settings = Settings(main, text);
        var connect = Connector();

        var failure = Assert.Throws<InvalidOperationException>(() => connect.Connect(Found(), readOn: true));

        Assert.Contains("larger than 1 MiB once changed; nothing changed", failure.Message);
        Assert.Equal(text, File.ReadAllText(settings));
        Assert.Equal(["settings.json"], Directory.GetFiles(main).Select(Path.GetFileName));
    }

    [Fact]
    public void Connect_twice_writes_nothing_the_second_time()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var settings = Settings(main, Written);
        var connect = Connector();
        connect.Connect(Found(), readOn: true);
        var bytes = File.ReadAllBytes(settings);
        var written = File.GetLastWriteTimeUtc(settings);
        var wrapper = File.GetLastWriteTimeUtc(Path.Combine(main, Wrapper.FileName));
        Thread.Sleep(20);

        Assert.Equal("Already connected", connect.Connect(Found(), readOn: true));

        Assert.Equal(bytes, File.ReadAllBytes(settings));
        Assert.Equal(written, File.GetLastWriteTimeUtc(settings));
        Assert.Equal(wrapper, File.GetLastWriteTimeUtc(Path.Combine(main, Wrapper.FileName)));
        Assert.Single(Backups(main));
    }

    [Fact]
    public void After_the_user_changes_the_status_line_Disconnect_leaves_the_file_and_removes_only_the_wrapper()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var settings = Settings(main, Written);
        var connect = Connector();
        connect.Connect(Found(), readOn: true);
        var theirs = Written.Replace(UserCommand, "bash ~/my-new-statusline.sh");
        File.WriteAllText(settings, theirs);

        Assert.Equal("Not connected: your status line changed after Connect", connect.DescribeConnect(Found(), readOn: true).State);
        var disconnect = connect.DescribeDisconnect(Found(), readOn: true);
        Assert.True(disconnect.Enabled);
        Assert.Contains("~/.claude/settings.json: your status line changed after Connect, so it isn't touched; only the wrapper is removed.", disconnect.Confirm!.Text);

        connect.Disconnect(Found(), readOn: true);

        Assert.Equal(theirs, File.ReadAllText(settings));
        Assert.False(File.Exists(Path.Combine(main, Wrapper.FileName)));
        Assert.False(File.Exists(Path.Combine(main, "infopanel-statusline.json")));
        Assert.Single(Backups(main));
        Assert.Equal("Not connected · 1 Claude folder, 1 settings file", connect.DescribeConnect(Found(), readOn: true).State);
    }

    [Fact]
    public void After_the_user_changes_the_status_line_Connect_wraps_the_new_command()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var settings = Settings(main, Written);
        var connect = Connector();
        connect.Connect(Found(), readOn: true);
        File.WriteAllText(settings, Written.Replace(UserCommand, "bash ~/my-new-statusline.sh"));
        _home.Clock.Now = _home.Clock.Now.AddMinutes(1);

        connect.Connect(Found(), readOn: true);

        Assert.Equal(WrapperCommandIn(main), CommandOf(settings));
        Assert.Contains("eval 'bash ~/my-new-statusline.sh'", File.ReadAllText(Path.Combine(main, Wrapper.FileName)));
        Assert.Equal(2, Backups(main).Length);
        connect.Disconnect(Found(), readOn: true);
        Assert.Equal("bash ~/my-new-statusline.sh", CommandOf(settings));
    }

    [Fact]
    public void A_status_line_that_runs_the_wrapper_itself_is_never_wrapped()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var settings = Settings(main, Written);
        var connect = Connector();
        connect.Connect(Found(), readOn: true);
        var theirs = Written.Replace(UserCommand, WrapperCommandIn(main) + " | cut -c1-40");
        File.WriteAllText(settings, theirs);

        Assert.False(connect.DescribeConnect(Found(), readOn: true).Enabled);
        Assert.Throws<InvalidOperationException>(() => connect.Connect(Found(), readOn: true));
        Assert.Equal(theirs, File.ReadAllText(settings));
        Assert.True(File.Exists(Path.Combine(main, Wrapper.FileName)));

        // Disconnect leaves it too: the status line still runs it.
        connect.Disconnect(Found(), readOn: true);
        Assert.Equal(theirs, File.ReadAllText(settings));
        Assert.True(File.Exists(Path.Combine(main, Wrapper.FileName)));
    }

    [Fact]
    public void A_deleted_wrapper_reads_broken_Connect_writes_it_again_and_Disconnect_puts_the_status_line_back()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var settings = Settings(main, Written);
        var connect = Connector();
        connect.Connect(Found(), readOn: true);
        var wrapper = Path.Combine(main, Wrapper.FileName);
        var script = File.ReadAllText(wrapper);
        var connected = File.ReadAllBytes(settings);
        File.Delete(wrapper);

        var state = connect.DescribeConnect(Found(), readOn: true);
        Assert.Equal("Broken: the wrapper is missing", state.State);
        Assert.True(state.Enabled);
        Assert.Contains("the wrapper is written again from its record; the settings file isn't changed", state.Confirm!.Text);

        connect.Connect(Found(), readOn: true);
        Assert.Equal(script, File.ReadAllText(wrapper));
        Assert.Equal(connected, File.ReadAllBytes(settings));
        Assert.Single(Backups(main));

        File.Delete(wrapper);
        Assert.True(connect.DescribeDisconnect(Found(), readOn: true).Enabled);
        connect.Disconnect(Found(), readOn: true);
        Assert.Equal(Written, File.ReadAllText(settings));
        Assert.False(File.Exists(Path.Combine(main, "infopanel-statusline.json")));
    }

    [Fact]
    public void A_settings_file_that_is_a_link_is_written_through_at_its_target_and_the_link_is_never_replaced()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var dotfiles = Directory.CreateDirectory(Path.Combine(_home.Path, "dotfiles")).FullName;
        var target = Path.Combine(dotfiles, "claude.json");
        File.WriteAllText(target, Written);
        var link = Path.Combine(main, "settings.json");
        File.CreateSymbolicLink(link, target);
        var connect = Connector();

        Assert.StartsWith("Changes 1 settings file (.claude): ~/dotfiles/claude.json.", connect.DescribeConnect(Found(), readOn: true).Confirm!.Text);
        connect.Connect(Found(), readOn: true);

        Assert.Equal(target, new FileInfo(link).LinkTarget);
        // Named after the file's whole real path: its name and 8 hex of the path's SHA-256.
        var named = new SettingsFile(Real(target), [], SettingsState.Absent, null, null);
        Assert.Matches("^infopanel-statusline\\.claude\\.json\\.[0-9a-f]{8}\\.sh$", Path.GetFileName(named.WrapperPath));
        var wrapperCommand = WrapperCommandIn(dotfiles, Path.GetFileName(named.WrapperPath));
        Assert.Equal(wrapperCommand, CommandOf(target));
        Assert.True(File.Exists(named.WrapperPath));
        Assert.True(File.Exists(named.RecordPath));
        Assert.Single(Backups(dotfiles, "claude.json"));
        Assert.Equal(["settings.json"], Directory.GetFileSystemEntries(main).Select(Path.GetFileName).Where(n => n != "projects"));

        connect.Disconnect(Found(), readOn: true);
        Assert.Equal(target, new FileInfo(link).LinkTarget);
        Assert.Equal(Written, File.ReadAllText(target));
    }

    [Fact]
    public void A_link_to_nothing_is_refused_and_not_made()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var link = Path.Combine(main, "settings.json");
        File.CreateSymbolicLink(link, Path.Combine(_home.Path, "gone.json"));
        var connect = Connector();

        Assert.Equal("Can't connect: ~/.claude/settings.json: it is a link to a file that isn't there", connect.DescribeConnect(Found(), readOn: true).State);
        Assert.Throws<InvalidOperationException>(() => connect.Connect(Found(), readOn: true));
        Assert.False(File.Exists(Path.Combine(_home.Path, "gone.json")));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void Only_the_newest_three_backups_are_kept()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        Settings(main, Written);
        var connect = Connector();
        for (var round = 0; round < 3; round++)
        {
            connect.Connect(Found(), readOn: true);
            _home.Clock.Now = _home.Clock.Now.AddMinutes(1);
            connect.Disconnect(Found(), readOn: true);
            _home.Clock.Now = _home.Clock.Now.AddMinutes(1);
        }

        Assert.Equal(["settings.json.infopanel-backup-20261110-100300", "settings.json.infopanel-backup-20261110-100400", "settings.json.infopanel-backup-20261110-100500"],
            Backups(main).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Claude Code's name for a project's folder: the folder it ran in as the system gives it, links resolved (the
    /// fixtures' temp folder is under one on macOS), with every character but ASCII letters and digits as '-'.
    /// </summary>
    private static string ClaudeProjectName(string path) => new([.. (ConnectFiles.Real(path) ?? path).Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);

    [Fact]
    public void Projects_whose_settings_set_the_status_line_are_named_in_the_confirmation()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        Settings(main, Written);
        var myApp = Path.Combine(_home.Path, "work", "my-app");
        Directory.CreateDirectory(Path.Combine(myApp, ".claude"));
        File.WriteAllText(Path.Combine(myApp, ".claude", "settings.local.json"), """{ "statusLine": { "type": "command", "command": "echo project" } }""");
        var plain = Path.Combine(_home.Path, "work", "plain_app");
        Directory.CreateDirectory(Path.Combine(plain, ".claude"));
        File.WriteAllText(Path.Combine(plain, ".claude", "settings.json"), """{ "model": "opus" }""");
        foreach (var project in new[] { myApp, plain, _home.Path })
            Directory.CreateDirectory(Path.Combine(main, "projects", ClaudeProjectName(project)));

        var text = Connector().DescribeConnect(Found(), readOn: true).Confirm!.Text;

        // The home's own .claude/settings.json is the user's, not a project's.
        Assert.EndsWith("In these projects your project's status line is used, so limits are seen only from other projects: ~/work/my-app.", text);
    }

    [Fact]
    public void A_projects_folder_name_is_read_back_as_the_folders_that_exist()
    {
        SkipOnWindows();
        Directory.CreateDirectory(Path.Combine(_home.Path, "work"));
        var work = Real(Path.Combine(_home.Path, "work"));
        Directory.CreateDirectory(Path.Combine(work, "a-b"));
        Directory.CreateDirectory(Path.Combine(work, "a", "b"));
        Directory.CreateDirectory(Path.Combine(work, ".hidden app"));

        Assert.Equal([Path.Combine(work, "a-b"), Path.Combine(work, "a", "b")], Overrides.ProjectPaths(_home.Environment, ClaudeProjectName(Path.Combine(work, "a-b"))).Order(StringComparer.Ordinal));
        Assert.Equal([Path.Combine(work, ".hidden app")], Overrides.ProjectPaths(_home.Environment, ClaudeProjectName(Path.Combine(work, ".hidden app"))));
        Assert.Empty(Overrides.ProjectPaths(_home.Environment, ClaudeProjectName(Path.Combine(work, "nowhere"))));
    }

    [Fact]
    public void Managed_settings_that_set_the_status_line_disable_Connect_which_writes_nothing_and_Disconnect_still_undoes()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var settings = Settings(main, Written);
        var managed = Directory.CreateDirectory(Path.Combine(_home.Path, "managed")).FullName;
        var file = Path.Combine(managed, "managed-settings.json");
        var connect = Connector(new ConnectPlaces([Path.Combine(managed, "absent.json"), file], Windows: false));

        File.WriteAllText(file, """{ "statusLine": { "type": "command", "command": "org-statusline" } }""");
        var state = connect.DescribeConnect(Found(), readOn: true);
        Assert.Equal((false, "Your organisation's settings set the status line"), (state.Enabled, state.State));
        Assert.Equal("Your organisation's settings set the status line", Assert.Throws<InvalidOperationException>(() => connect.Connect(Found(), readOn: true)).Message);
        Assert.Equal(Written, File.ReadAllText(settings));
        Assert.Equal(["settings.json"], Directory.GetFiles(main).Select(Path.GetFileName));
        Assert.False(connect.DescribeDisconnect(Found(), readOn: true).Enabled);

        // Connected before the organisation's setting came: Disconnect still puts the status line back.
        Connector().Connect(Found(), readOn: true);
        Assert.True(connect.DescribeDisconnect(Found(), readOn: true).Enabled);
        connect.Disconnect(Found(), readOn: true);
        Assert.Equal(Written, File.ReadAllText(settings));

        File.WriteAllText(file, "{ not json");
        Assert.Equal("Your organisation's settings couldn't be read, so Connect can't tell whether they set the status line", connect.DescribeConnect(Found(), readOn: true).State);

        File.WriteAllText(file, """{ "permissions": { "deny": [] } }""");
        Assert.True(connect.DescribeConnect(Found(), readOn: true).Enabled);
    }

    [Fact]
    public void On_Windows_both_actions_wait_for_the_PC_check()
    {
        var main = _home.ClaudeFolder(".claude");
        var settings = Settings(main, Written);
        var connect = Connector(new ConnectPlaces([], Windows: true));

        foreach (var state in new[] { connect.DescribeConnect(Found(), readOn: true), connect.DescribeDisconnect(Found(), readOn: true) })
            Assert.Equal((false, "Not available on Windows yet"), (state.Enabled, state.State));
        Assert.Throws<InvalidOperationException>(() => connect.Connect(Found(), readOn: true));
        Assert.Equal(Written, File.ReadAllText(settings));
    }

    [Fact]
    public void Windows_paths_are_quoted_for_Git_Bash_and_a_path_bash_would_change_is_refused()
    {
        Assert.Equal("bash \"C:/Users/Jo Smith/.claude/infopanel-statusline.sh\"", Wrapper.Command(@"C:\Users\Jo Smith\.claude\infopanel-statusline.sh", windows: true));
        Assert.Equal("bash \"D:/claude (work)/it's/infopanel-statusline.sh\"", Wrapper.Command(@"D:\claude (work)\it's\infopanel-statusline.sh", windows: true));
        foreach (var odd in new[] { @"C:\Users\$jo\.claude\x.sh", @"C:\Users\a`b\x.sh", @"C:\Users\100%\x.sh", @"C:\Users\hey!\x.sh" })
            Assert.Null(Wrapper.Command(odd, windows: true));
        Assert.Equal("bash '/Users/jo/it'\\''s/infopanel-statusline.sh'", Wrapper.Command("/Users/jo/it's/infopanel-statusline.sh", windows: false));
    }

    [Fact]
    public void The_plugins_actions_say_their_states_and_with_Read_Claude_Code_off_touch_no_file()
    {
        SkipOnWindows();
        var main = _home.ClaudeFolder(".claude");
        var settings = Settings(main, Written);
        var plugin = new AiUsagePlugin(_home.Environment, new ConnectPlaces([], Windows: false));
        plugin.Initialize();
        plugin.ApplyConfig(AiUsagePlugin.ReadClaudeCode, false); // saved off: the reader's default is on
        plugin.Load([]);
        var (opens, stats) = (_home.Files.Opens, _home.Files.Stats);

        foreach (var action in new[] { nameof(AiUsagePlugin.ConnectClaudeCodeLimits), nameof(AiUsagePlugin.DisconnectClaudeCodeLimits) })
            Assert.Equal((false, ClaudeConnect.ReadOffState), (plugin.Describe(action)!.Enabled, plugin.Describe(action)!.State));
        Assert.Equal(ClaudeConnect.ReadOffState, Assert.Throws<InvalidOperationException>(plugin.ConnectClaudeCodeLimits).Message);
        Assert.Equal((opens, stats), (_home.Files.Opens, _home.Files.Stats));
        Assert.Null(plugin.Describe("Update"));

        plugin.ApplyConfig(AiUsagePlugin.ReadClaudeCode, true);
        var connect = plugin.Describe(nameof(AiUsagePlugin.ConnectClaudeCodeLimits))!;
        Assert.Equal((true, "Not connected · 1 Claude folder, 1 settings file"), (connect.Enabled, connect.State));
        Assert.False(plugin.Describe(nameof(AiUsagePlugin.DisconnectClaudeCodeLimits))!.Enabled);

        plugin.ConnectClaudeCodeLimits();
        Assert.Equal(WrapperCommandIn(main), CommandOf(settings));
        Assert.Equal("Connected · 1 Claude folder", plugin.Describe(nameof(AiUsagePlugin.DisconnectClaudeCodeLimits))!.State);

        plugin.DisconnectClaudeCodeLimits();
        Assert.Equal(Written, File.ReadAllText(settings));
    }
}
