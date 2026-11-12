using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using InfoPanel.Shell.Windows;
using InfoPanel.Tests.Editing;
using Xunit;

namespace InfoPanel.Tests.Shell.Windows;

/// <summary>
/// Start when I sign in: the <c>InfoPanel 2</c> value under the user's Run key, read as Task Manager reads it (its
/// StartupApproved entry can turn it off), written with the engine's own path, removed with its StartupApproved entry.
/// Through <see cref="FakeRegistry"/> on any OS; the registry itself on Windows, under a key of the test's own.
/// </summary>
public sealed class WinStartupTests
{
    private const string Command = "\"C:\\Program Files\\InfoPanel\\InfoPanel.exe\"";

    private readonly FakeRegistry _registry = new();

    private WinStartup Startup() => new(_registry, Command);

    private static byte[] Approved(byte state) => [state, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    [Fact]
    public void Off_WithoutTheRunValue_On_WithIt()
    {
        Assert.False(Startup().IsOn());

        _registry.SetString(WinStartup.RunKey, WinStartup.ValueName, "\"D:\\elsewhere\\InfoPanel.exe\"");

        Assert.True(Startup().IsOn());
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(6, true)]
    [InlineData(3, false)]
    [InlineData(7, false)]
    public void TaskManagersSwitch_IsRead(byte state, bool on)
    {
        _registry.SetString(WinStartup.RunKey, WinStartup.ValueName, Command);
        _registry.Values[(WinStartup.ApprovedKey, WinStartup.ValueName)] = Approved(state);

        Assert.Equal(on, Startup().IsOn());
    }

    [Fact]
    public void TurningItOn_WritesTheEnginesPath_UnderTheRunKey_AndClearsTaskManagersSwitch()
    {
        _registry.Values[(WinStartup.ApprovedKey, WinStartup.ValueName)] = Approved(3);

        Startup().Set(true);

        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", WinStartup.RunKey);
        Assert.Equal("InfoPanel 2", WinStartup.ValueName);
        var (key, value) = Assert.Single(_registry.Values);
        Assert.Equal((WinStartup.RunKey, WinStartup.ValueName), key);
        Assert.Equal(Command, value);
        Assert.True(Startup().IsOn());
    }

    [Fact]
    public void TheValue_IsNot1xs()
    {
        // 1.4.x deletes the Run value "InfoPanel" at every start (its legacy startup removal), so beside it 2.0's own
        // value must be another name; Windows compares value names without case.
        Assert.False(string.Equals("InfoPanel", WinStartup.ValueName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TurningItOff_RemovesTheValue_AndTaskManagersSwitch()
    {
        _registry.SetString(WinStartup.RunKey, WinStartup.ValueName, Command);
        _registry.Values[(WinStartup.ApprovedKey, WinStartup.ValueName)] = Approved(2);
        _registry.SetString(WinStartup.RunKey, "Other app", "other.exe");

        Startup().Set(false);

        Assert.Equal((WinStartup.RunKey, "Other app"), Assert.Single(_registry.Values).Key);
        Assert.False(Startup().IsOn());
    }

    [Fact]
    public void TheCommand_IsTheEnginesQuotedPath_OrTheDotnetHostWithTheEngine()
    {
        Assert.Equal("\"C:\\Program Files\\InfoPanel\\InfoPanel.exe\"",
            WinStartup.CommandFor(@"C:\Program Files\InfoPanel\InfoPanel.exe", @"C:\Program Files\InfoPanel\InfoPanel.dll"));
        Assert.Equal("\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\ip\\engine\\InfoPanel.dll\"",
            WinStartup.CommandFor(@"C:\Program Files\dotnet\dotnet.exe", @"C:\ip\engine\InfoPanel.dll"));
    }

    [Fact]
    public void TheInstallersFinishPage_OffersIt_Ticked_WritingThisValue_AsTheUserWhoStartedSetup()
    {
        // packaging/windows/InfoPanel.iss: the finish page's "Start InfoPanel 2 when I sign in" runs reg.exe as the user
        // who started Setup (Setup's own HKCU is the elevating administrator's hive), writing what Set(true) writes for
        // the installed engine, after clearing Task Manager's switch as Set does. Ticked, and run by a silent install.
        var script = File.ReadAllText(Path.Combine(LiveEngine.RepoRoot(), "packaging", "windows", "InfoPanel.iss"));
        var defines = Regex.Matches(script, @"(?m)^#define (\w+) ""([^""]*)""\r?$")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        string Defined(string text) => Regex.Replace(text, @"\{#(\w+)\}", m => defines[m.Groups[1].Value]);
        string Installed(string text) => Defined(text).Replace("{app}", @"C:\Program Files\InfoPanel 2").Replace("{sys}", @"C:\Windows\System32");

        var entry = Assert.Single(RunEntries(script), e => e.GetValueOrDefault("Description", "").Contains("sign in"));

        Assert.Equal("Start InfoPanel 2 when I sign in", Installed(entry["Description"]));
        Assert.Equal(@"C:\Windows\System32\reg.exe", Installed(entry["Filename"]));
        Assert.Equal(["postinstall", "runasoriginaluser", "runhidden"], entry["Flags"].Split(' ', StringSplitOptions.RemoveEmptyEntries).Order());
        Assert.Equal(
            ["add", @"HKCU\" + WinStartup.RunKey, "/v", WinStartup.ValueName, "/t", "REG_SZ", "/d",
                WinStartup.CommandFor(@"C:\Program Files\InfoPanel 2\InfoPanel.exe", @"C:\Program Files\InfoPanel 2\InfoPanel.dll"), "/f"],
            Arguments(Installed(entry["Parameters"])));
        var before = Regex.Match(script, $@"(?ms)^procedure {Regex.Escape(entry["BeforeInstall"])};\r?$.*?^end;\r?$");
        Assert.True(before.Success, "No procedure " + entry["BeforeInstall"]);
        Assert.Contains(
            $"ExecAsOriginalUser(ExpandConstant('{{sys}}\\reg.exe'), 'delete \"HKCU\\{WinStartup.ApprovedKey}\" /v \"{WinStartup.ValueName}\" /f'",
            Defined(before.Value));
    }

    [Fact]
    public void TheInstallersFinishPage_OffersIt_OnlyWhenTheUserWhoStartedSetupIsUnelevated_AsItOffersTheLaunch()
    {
        // Setup started elevated (Run as administrator with another administrator's credentials) can't run anything as
        // the user who started it: runasoriginaluser would write the elevating user's HKCU. Then no entry, and no write.
        var script = File.ReadAllText(Path.Combine(LiveEngine.RepoRoot(), "packaging", "windows", "InfoPanel.iss"));
        var entries = RunEntries(script);
        var signIn = Assert.Single(entries, e => e.GetValueOrDefault("Description", "").Contains("sign in"));
        var launch = Assert.Single(entries, e => e.GetValueOrDefault("Description", "").Contains("LaunchProgram"));

        Assert.Equal("OriginalUserUnelevated", launch["Check"]);
        Assert.Equal(["OriginalUserUnelevated", "StartAtSignInAsked"], Regex.Split(signIn["Check"], @"\s+and\s+").Order());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("yes", true)]
    [InlineData("0", true)]
    [InlineData("no", false)]
    [InlineData("No", false)]
    [InlineData("NO", false)]
    public void TheInstallersStartAtSignInSwitch_TurnsTheEntryOff_OnlyWithNo(string? given, bool asked)
    {
        // StartAtSignInAsked, as Inno Setup evaluates it: {param:NAME|default} is /NAME=value's value, else the default;
        // CompareText compares without case.
        var script = File.ReadAllText(Path.Combine(LiveEngine.RepoRoot(), "packaging", "windows", "InfoPanel.iss"));
        var body = Regex.Match(script, @"(?ms)^function StartAtSignInAsked: Boolean;\r?$\s*begin\s*(?<body>.*?)\s*^end;\r?$");
        Assert.True(body.Success, "No function StartAtSignInAsked");
        var compare = Regex.Match(body.Groups["body"].Value,
            @"^Result := CompareText\(ExpandConstant\('\{param:(?<name>\w+)\|(?<default>\w*)\}'\), '(?<off>\w*)'\) <> 0;$");
        Assert.True(compare.Success, "StartAtSignInAsked isn't one comparison of the switch: " + body.Groups["body"].Value);

        Assert.Equal("STARTATSIGNIN", compare.Groups["name"].Value);
        Assert.Equal(asked, !string.Equals(given ?? compare.Groups["default"].Value, compare.Groups["off"].Value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The <c>[Run]</c> section's entries, each its parameters as Inno Setup reads them (quotes doubled inside).</summary>
    private static List<Dictionary<string, string>> RunEntries(string script)
    {
        var block = Regex.Match(script, @"(?m)^\[Run\]\r?\n(?<lines>(?:(?!\[).*\r?\n)*)");
        Assert.True(block.Success, "The installer has no [Run] section.");
        var entries = new List<Dictionary<string, string>>();
        foreach (var line in block.Groups["lines"].Value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith(';')))
        {
            var entry = new Dictionary<string, string>();
            var i = 0;
            while (i < line.Length)
            {
                var colon = line.IndexOf(':', i);
                var name = line[i..colon].Trim();
                for (i = colon + 1; i < line.Length && line[i] == ' '; i++) { }
                var value = new StringBuilder();
                if (i < line.Length && line[i] == '"')
                {
                    for (i++; i < line.Length; i++)
                    {
                        if (line[i] != '"')
                            value.Append(line[i]);
                        else if (i + 1 < line.Length && line[i + 1] == '"')
                            value.Append(line[++i]);
                        else
                            break;
                    }
                    i++;
                }
                var end = line.IndexOf(';', i);
                if (end < 0)
                    end = line.Length;
                entry[name] = value.Length > 0 ? value.ToString() : line[i..end].Trim();
                for (i = end + 1; i < line.Length && line[i] == ' '; i++) { }
            }
            entries.Add(entry);
        }
        return entries;
    }

    /// <summary>A program's arguments as its C runtime splits them (CommandLineToArgvW's rules after the program's name).</summary>
    private static List<string> Arguments(string commandLine)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, started = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var slashes = 0;
                for (; i < commandLine.Length && commandLine[i] == '\\'; i++)
                    slashes++;
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1)
                        current.Append('"');
                    else
                        quoted = !quoted;
                }
                else
                {
                    current.Append('\\', slashes);
                    i--;
                }
                started = true;
            }
            else if (c == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (!quoted && c is ' ' or '\t')
            {
                if (started)
                    arguments.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }
        if (started)
            arguments.Add(current.ToString());
        return arguments;
    }

    [Fact]
    public void TheRegistry_ReadsWritesAndDeletesAValue()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The registry exists only on Windows.");
        if (OperatingSystem.IsWindows())
            RegistryRoundTrip();
    }

    [SupportedOSPlatform("windows")]
    private static void RegistryRoundTrip()
    {
        var key = @"Software\InfoPanelTests\" + Guid.NewGuid().ToString("N");
        var registry = new WinRegistry();
        try
        {
            Assert.Null(registry.Get(key, "InfoPanel"));
            registry.SetString(key, "InfoPanel", Command);
            Assert.Equal(Command, registry.Get(key, "InfoPanel"));
            registry.Delete(key, "InfoPanel");
            registry.Delete(key, "InfoPanel");
            Assert.Null(registry.Get(key, "InfoPanel"));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(key, throwOnMissingSubKey: false);
        }
    }
}
