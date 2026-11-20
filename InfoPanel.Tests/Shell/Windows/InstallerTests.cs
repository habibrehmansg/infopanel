using System.Text;
using System.Text.RegularExpressions;
using InfoPanel.SensorHelper;
using InfoPanel.Shell.Windows;
using InfoPanel.Tests.Editing;
using Xunit;

namespace InfoPanel.Tests.Shell.Windows;

/// <summary>
/// The Windows installer (packaging/windows/InfoPanel.iss) as text: one name, "InfoPanel", in every Windows name, and
/// its replacing of InfoPanel 1.x before it writes anything. 1.x's 32-bit Setup installs into
/// <c>C:\Program Files (x86)\InfoPanel</c> with its uninstall key in the 32-bit registry view; 2.0's Setup finds it there
/// (then in the 64-bit view), ends it, runs its own uninstaller silently and checks it is gone, and removes its sign-in
/// task, the root task <c>\InfoPanel</c>. What it does on a PC is proved only by a run there; these hold its text to that.
/// </summary>
public sealed class InstallerTests
{
    private const string OwnAppId = "{B63E31BF-4503-4A62-BB7A-D021C854BEEA}";
    private const string V1AppId = "{761BE813-4A06-4646-B930-3E647446A307}";

    private static readonly string Script =
        File.ReadAllText(Path.Combine(LiveEngine.RepoRoot(), "packaging", "windows", "InfoPanel.iss"));

    [Fact]
    public void TheName_IsInfoPanel_InTheTitle_TheFolder_TheStartMenu_TheAppsList_TheRunValue_AndTheTaskFolder()
    {
        Assert.Equal("InfoPanel", Defines()["AppName"]);
        Assert.Matches(@"(?m)^AppName=\{#AppName\}\r?$", Script);
        // The title bar and the pages' "InfoPanel <version>".
        Assert.Matches(@"(?m)^AppVerName=\{#AppName\} \{#AppVersion\}\r?$", Script);
        Assert.Matches(@"(?m)^UninstallDisplayName=\{#AppName\}\r?$", Script);
        Assert.Matches(@"(?m)^DefaultDirName=\{autopf\}\\\{#AppName\}\r?$", Script);
        Assert.Matches(@"(?m)^DefaultGroupName=\{#AppName\}\r?$", Script);
        // The group is always "InfoPanel", whatever an earlier build used, and no page offers another.
        Assert.Matches(@"(?m)^UsePreviousGroup=no\r?$", Script);
        Assert.Matches(@"(?m)^DisableProgramGroupPage=yes\r?$", Script);
        var launch = Assert.Single(Entries("Run"), e => e.GetValueOrDefault("Description", "").Contains("LaunchProgram"));
        Assert.Equal("{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}", launch["Description"]);

        // The tray's Run value and the helper's task folder are the engine's own names.
        Assert.Matches(@"(?m)^  RunValue = '\{#AppName\}';\r?$", Script);
        Assert.Matches(@"(?m)^  TaskFolder = '\{#AppName\}';\r?$", Script);
        Assert.Equal("InfoPanel", WinStartup.ValueName);
        Assert.Equal("InfoPanel", HelperTask.FolderName);
        Assert.Equal(@"\InfoPanel", HelperTask.FolderPath);
    }

    [Fact]
    public void NoWindowsNameOrDoc_SaysInfoPanel2()
    {
        // A version ("InfoPanel 2.0", "InfoPanel 2.0.1") isn't a name.
        string[] files =
        [
            "packaging/windows/InfoPanel.iss", "packaging/windows/build.ps1", "InfoPanel/Shell/Windows/WinStartup.cs",
            "InfoPanel.Core/SensorHelper/HelperTask.cs", "README.md", "packaging/release-notes.md", "PORTING.md",
            "docs/2.0/packaging.md", "docs/2.0/desktop-shell.md", "docs/agents/shell.md", "docs/agents/sensor-helper.md",
        ];
        var found = files
            .SelectMany(f => File.ReadAllLines(Path.Combine(LiveEngine.RepoRoot(), f)).Select((line, i) => (f, i, line)))
            .Where(l => Regex.IsMatch(l.line, @"InfoPanel 2(?!\.\d|\d)"))
            .Select(l => $"{l.f}:{l.i + 1}: {l.line.Trim()}")
            .ToList();

        Assert.True(found.Count == 0, string.Join('\n', found));
    }

    [Fact]
    public void TheAppId_IsKept_And1xIsFoundOnlyThroughItsUninstallKey_InTheViewsByName()
    {
        Assert.Matches(@"(?m)^AppId=\{" + Regex.Escape(OwnAppId) + @"\r?$", Script);
        var code = Code();
        // 1.x's AppId only names its key, and 2.0's own key is this AppId's.
        Assert.Equal(1, Regex.Count(code, Regex.Escape(V1AppId)));
        Assert.Contains(@"V1Key = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + V1AppId + "_is1';", code);
        Assert.Contains(@"OwnKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + OwnAppId + "_is1';", code);
        // Setup is a 32-bit process: every read names its view, the 32-bit one (where 1.x's 32-bit Setup wrote) first.
        Assert.DoesNotMatch(@"\bHKLM\b(?!32|64)", code);
        Assert.DoesNotMatch(@"\bHKEY_LOCAL_MACHINE\b", code);
        Assert.Matches(@"if ReadV1\(HKLM32\) then\s+V1Found := True\s+else\s+V1Found := ReadV1\(HKLM64\);", Body("function FindV1: Boolean;"));
        var read = Body("function ReadV1(const Root: Integer): Boolean;");
        Assert.Matches(@"Result := RegKeyExists\(Root, V1Key\);\s+if not Result then\s+Exit;", read);
        foreach (var value in new[] { "UninstallString", "InstallLocation", "Inno Setup: App Path", "DisplayVersion" })
            Assert.Contains($"RegQueryStringValue(Root, V1Key, '{value}', ", read);
        // Every use of the key is through ReadV1's Root, or the view it was found in.
        Assert.All(Regex.Matches(code, @"\w+\((\w+), V1Key").Select(m => m.Groups[1].Value), root => Assert.Contains(root, new[] { "Root", "V1Root" }));
    }

    [Fact]
    public void PrepareToInstall_RefusesAWrongFolderAndAnEarlierBuild_Replaces1x_ThenStopsThisInstallsOwn_InThatOrder()
    {
        var body = Body("function PrepareToInstall(var NeedsRestart: Boolean): String;");

        string[] steps =
        [
            "Result := DestinationProblem;", "Result := EarlierBuildProblem;", "if FindV1 then", "Result := V1FolderProblem;",
            "Result := CloseV1;", "Result := RemoveV1;", "RemoveV1Task;", "HelperTasks(False);", "Result := StopOwnProcessesAsking;",
        ];
        Assert.Equal(steps, steps.OrderBy(s => body.IndexOf(s, StringComparison.Ordinal)));
        Assert.All(steps, s => Assert.Equal(1, Regex.Count(body, Regex.Escape(s))));
        // Each refusal stops Setup at once, before anything of 2.0's is written.
        foreach (var step in new[] { "DestinationProblem", "EarlierBuildProblem", "V1FolderProblem", "CloseV1", "RemoveV1" })
            Assert.Matches(Regex.Escape($"Result := {step};") + @"\s+if Result <> '' then\s+Exit;", body);
        // Closing and uninstalling only when 1.x was found, and only once its folder and this one are apart; its task
        // always (1.x may have been uninstalled by hand).
        Assert.Matches(@"if FindV1 then\s+begin\s+Result := V1FolderProblem;\s+if Result <> '' then\s+Exit;\s+Result := CloseV1;[\s\S]*Result := RemoveV1;\s+if Result <> '' then\s+Exit;\s+end;\s+RemoveV1Task;", body);
    }

    [Fact]
    public void A1xInThisFolder_OrHoldingIt_OrInsideIt_IsRefused_BeforeAnythingIsClosedOrRun()
    {
        // 1.x installed by hand into C:\Program Files\InfoPanel (or a folder that holds it, or one inside it): its
        // programs would be taken for this install's, and its uninstaller would leave its plugins' .dll.ini where this
        // install's clean-up runs. Setup takes none of it on: the user uninstalls 1.x first.
        var body = Body("function V1FolderProblem: String;");

        Assert.Matches(@"if V1Folder = '' then\s+Exit;", body);
        Assert.Contains("App := ExpandConstant('{app}');", body);
        // The same folder, this one inside 1.x's, 1.x's inside this one: compared resolved and without case.
        Assert.Contains("if not (SamePath(V1Folder, App) or IsUnder(App, V1Folder) or IsUnder(V1Folder, App)) then", body);
        Assert.Contains(
            "Result := 'InfoPanel ' + V1Version + ' is installed in the folder this setup uses (' + Folder + '). Uninstall it from Settings ' + #$203A + ' Apps, then run this setup again; its profiles and settings are kept.';",
            body);
        // A refusal changes nothing.
        Assert.DoesNotMatch(@"\bExec\(|Delete|Terminate|V1Processes|CloseV1|RemoveV1", body);
    }

    [Fact]
    public void AnEarlierTestBuild_InAnotherFolder_IsRefused_WithItsOwnName_AndAnUpdateInThisFolderIsNot()
    {
        var body = Body("function EarlierBuildProblem: String;");

        Assert.Contains("RegQueryStringValue(HKLM64, OwnKey, 'InstallLocation', Location)", body);
        Assert.Matches(@"if \(Location = ''\) or SamePath\(Location, ExpandConstant\('\{app\}'\)\) then\s+Exit;", body);
        // The build's name is read from its key ("InfoPanel 2" on the dry runs), never written here.
        Assert.Contains("RegQueryStringValue(HKLM64, OwnKey, 'DisplayName', Name)", body);
        Assert.Contains(
            "Result := 'An earlier test build of {#AppName} is installed in ' + Location + '. Uninstall it (\"' + Name + '\") from Settings ' + #$203A + ' Apps, then run this setup again.';",
            body);
        Assert.DoesNotMatch(@"\bExec\(|Delete|Terminate", body);
    }

    [Fact]
    public void Only1xsOwnProcesses_AreEnded_ThoseUnderItsFolder_NeverThisInstalls()
    {
        var body = Body("function V1Processes(EndThem: Boolean; var Listed: String): Integer;");

        Assert.Matches(@"if V1Folder = '' then\s+Exit;", body);
        // 1.x's folder and this one are apart by then (V1FolderProblem refused any overlap), so what is under this
        // install's folder is only ever 2.0's own program, an update's.
        Assert.Contains("if IsUnder(Path, V1Folder) and not IsUnder(Path, ExpandConstant('{app}')) then", body);
        Assert.Contains("Process.Terminate(1)", body);
        var close = Body("function CloseV1: String;");
        Assert.Matches(@"Count := V1Processes\(False, Listed\);\s+until \(Count = 0\) or \(Waited >= V1StopWait\);", close);
        Assert.Contains("until (Result = '') or (SuppressibleMsgBox(Result, mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY);", close);
        Assert.Matches(@"(?m)^  V1StopWait = 10000;", Script);
    }

    [Fact]
    public void A1xListThatFails_IsUnknown_NotNone_AndNeverReachesItsUninstaller()
    {
        // The list of processes failing (WMI unavailable) is a third answer, -1: never "nothing is running".
        var list = Body("function V1Processes(EndThem: Boolean; var Listed: String): Integer;");
        Assert.Matches(@"except\s+Result := -1;\s+Log\('Listing InfoPanel 1\.x''s processes failed: ' \+ GetExceptionMessage\);\s+end;\s*$", list);

        // CloseV1 answers unknown with Retry/Cancel (a silent run answers Cancel), before and after the wait, so it
        // returns '' only when the list was read and empty.
        var close = Body("function CloseV1: String;");
        Assert.Equal(2, Regex.Count(close, @"Count := V1Processes\((True|False), Listed\);"));
        Assert.Equal(2, Regex.Count(close, @"if Count < 0 then\s+Result := Unknown\s+else if Count > 0 then"));
        Assert.Contains(
            "Unknown := 'InfoPanel ' + V1Version + ' may still be running, and Setup can''t check (Windows didn''t list its programs). Close it from its icon in the notification area, then Retry; or uninstall it from Settings ' + #$203A + ' Apps, then run this setup again.';",
            close);
        Assert.Matches(@"Result := '';\s+Count := V1Processes\(True, Listed\);\s+if Count < 0 then\s+Result := Unknown\s+else if Count > 0 then", close);
        Assert.Matches(@"Count := V1Processes\(False, Listed\);\s+until \(Count = 0\) or \(Waited >= V1StopWait\);\s+if Count < 0 then\s+Result := Unknown\s+else if Count > 0 then", close);
        // Every count is read through Count, so no "> 0" alone stands for "none".
        Assert.DoesNotMatch(@"V1Processes\([^)]*\) [<>=]", close);

        // And a CloseV1 that isn't '' stops Setup before RemoveV1 runs the uninstaller (the order test pins it).
        Assert.Matches(@"Result := CloseV1;\s+if Result <> '' then\s+Exit;\s+Result := RemoveV1;", Body("function PrepareToInstall(var NeedsRestart: Boolean): String;"));
    }

    [Fact]
    public void A1xRowThatCantBeRead_IsUnknown_NotSkipped_AndOnlyAWhollyPlacedListIsACount()
    {
        // The query lists InfoPanel.exe only (1.x's plugin hosts are InfoPanel.exe --plugin-host too), so every row it
        // gives is one Setup must place: a row whose path is Null or empty, or whose reading throws, is never "another
        // program", and makes the whole answer unknown unless a second look by its id finds it gone.
        var list = Body("function V1Processes(EndThem: Boolean; var Listed: String): Integer;");
        Assert.Contains("FROM Win32_Process WHERE Name = ''{#AppExeName}''')", list);

        // A row is placed only once its path is read and not empty: then under 1.x's folder (counted), or not counted.
        Assert.Matches(@"Placed := False;\s+Pid := -1;\s+try\s+Process := Processes\.ItemIndex\(I\);", list);
        Assert.Matches(@"Path := TextOf\(Process\.ExecutablePath\);\s+if Path <> '' then\s+begin\s+if IsUnder\(Path, V1Folder\) and not IsUnder\(Path, ExpandConstant\('\{app\}'\)\) then[\s\S]*?end;\s+Placed := True;\s+end;", list);
        Assert.Equal(1, Regex.Count(list, @"Placed := True;"));
        // A row whose reading throws isn't placed either; the exception is logged, never taken for "elsewhere".
        Assert.Matches(@"except\s+Log\('Reading a process: ' \+ GetExceptionMessage\);\s+end;\s+if not Placed then\s+begin", list);
        // An unplaced row: unknown at once without its id, else looked at again by id once the list is read.
        Assert.Matches(@"if Pid < 0 then\s+Unknown := True\s+else\s+begin\s+SetArrayLength\(Unread, GetArrayLength\(Unread\) \+ 1\);\s+Unread\[GetArrayLength\(Unread\) - 1\] := Pid;", list);
        Assert.Matches(@"for I := 0 to GetArrayLength\(Unread\) - 1 do\s+if not V1ProcessGone\(Unread\[I\]\) then\s+Unknown := True;", list);

        // The count is returned only when nothing is unknown, and only a count ends anything: the 1.x processes are
        // ended after the whole list was placed, never while a row may still make it unknown.
        Assert.Matches(@"if Unknown then\s+begin\s+Result := -1;[\s\S]*?end\s+else\s+begin\s+Result := GetArrayLength\(Found\);\s+if EndThem then", list);
        Assert.DoesNotMatch(@"Result := Result \+ 1", list);
        Assert.Equal(1, Regex.Count(list, @"Result := GetArrayLength\(Found\);"));
        Assert.Equal(1, Regex.Count(list, @"\.Terminate\(1\)"));
        Assert.True(list.IndexOf(".Terminate(1)", StringComparison.Ordinal) > list.IndexOf("Result := GetArrayLength(Found);", StringComparison.Ordinal));

        // The second look: that InfoPanel.exe, by id, gone only when the list of it is read and empty.
        var gone = Body("function V1ProcessGone(const ProcessId: Integer): Boolean;");
        Assert.Contains("Format('SELECT ProcessId FROM Win32_Process WHERE ProcessId = %d AND Name = ''{#AppExeName}''', [ProcessId])", gone);
        Assert.Matches(@"Result := Processes\.Count = 0;", gone);
        Assert.Matches(@"except\s+Result := False;", gone);
    }

    [Fact]
    public void V1sUninstaller_IsRunSilently_AndWaitedFor_AndJudgedByWhatIsLeft_NotByItsExitCode()
    {
        var body = Body("function RemoveV1: String;");

        Assert.Contains("Uninstaller := RemoveQuotes(V1Uninstaller);", body);
        Assert.Contains("Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code)", body);
        Assert.Matches(@"if \(Uninstaller = ''\) or not FileExists\(Uninstaller\) then", body);
        Assert.Matches(@"if not V1Gone then\s+begin\s+Result := 'InfoPanel ' \+ V1Version \+ ' couldn''t be uninstalled \(code ' \+ IntToStr\(Code\) \+ '\)\. Uninstall it from Settings ' \+ #\$203A \+ ' Apps, then run this setup again\.';", body);
        // Gone: its key from the view it was found in, and its program from its folder.
        var gone = Body("function V1Gone: Boolean;");
        Assert.Contains("not RegKeyExists(V1Root, V1Key)", gone);
        Assert.Contains("not FileExists(AddBackslash(V1Folder) + '{#AppExeName}')", gone);
        Assert.Equal(1, Regex.Count(Code(), @"/VERYSILENT"));
    }

    [Fact]
    public void Only1xsRootTask_IsRemoved_OneExecActionOfInfoPanelExe_NeverAFolderOfThatName()
    {
        var body = Body("procedure RemoveV1Task;");

        Assert.Matches(@"(?m)^  V1TaskName = 'InfoPanel';", Script);
        Assert.Contains(@"Root := Service.GetFolder('\');", body);
        Assert.Contains("Task := Root.GetTask(V1TaskName);", body);
        Assert.Matches(@"if not IsV1Task\(Task\) then\s+begin\s+Log\([^\n]*\);\s+Exit;\s+end;", body);
        Assert.Contains("Root.DeleteTask(V1TaskName, 0);", body);
        Assert.DoesNotContain("DeleteFolder", body);
        var own = Body("function IsV1Task(const Task: Variant): Boolean;");
        Assert.Matches(@"if Actions\.Count <> 1 then\s+Exit;", own);
        Assert.Contains("Result := CompareText(ExtractFileName(RemoveQuotes(Path)), '{#AppExeName}') = 0;", own);
    }

    [Fact]
    public void TheReadyPage_Names1x_OnlyWhenItIsFound()
    {
        var body = Body("function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;");

        foreach (var memo in new[] { "MemoUserInfoInfo", "MemoDirInfo", "MemoTypeInfo", "MemoComponentsInfo", "MemoGroupInfo", "MemoTasksInfo" })
            Assert.Contains($"AddReadyBlock(Result, {memo}, NewLine);", body);
        Assert.Matches(@"if FindV1 then\s+AddReadyBlock\(Result, 'InfoPanel ' \+ V1Version \+ ':' \+ NewLine \+", body);
        Assert.Contains("Setup closes it if it is running and uninstalls it.", body);
        Assert.Contains("Its profiles and settings stay on this PC; InfoPanel brings them over as copies.", body);
        Assert.Contains("Save any work in it first.", body);
    }

    [Fact]
    public void NothingOutsideTheProgramFolder_IsDeleted_NoDataFolder_No1xPluginSettings()
    {
        var deletes = Entries("InstallDelete").Concat(Entries("UninstallDelete")).ToList();

        Assert.NotEmpty(deletes);
        Assert.All(deletes, e => Assert.StartsWith(@"{app}\", e["Name"]));
        // Own subfolders only, none 1.x ever had: nothing under plugins (1.x's uninstaller leaves its plugins' .dll.ini
        // there, plugins\InfoPanel.Extras\ for its bundled one, should 1.x have been in this folder once), never a
        // wildcard.
        Assert.All(deletes, e => Assert.DoesNotMatch(@"(?i)^\{app\}\\plugins(\\|$)|\*|\?|\.ini", e["Name"]));
        Assert.All(deletes, e => Assert.Contains(e["Name"], new[] { @"{app}\sensor-helper", @"{app}\pawnio" }));
        // No file or folder is deleted by the code: %LOCALAPPDATA%\InfoPanel, ProgramData\InfoPanel and 1.x's leftover
        // folder are never touched.
        Assert.DoesNotMatch(@"\b(DeleteFile|DelTree|RemoveDir|DelayDeleteFile)\(", Code());
        Assert.DoesNotMatch(@"(?i)\{(localappdata|commonappdata|userappdata)\}", Script);
    }

    private static Dictionary<string, string> Defines() =>
        Regex.Matches(Script, @"(?m)^#define (\w+) ""([^""]*)""\r?$").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

    /// <summary>The [Code] section without its comments (Pascal's <c>{ ... }</c>).</summary>
    private static string Code()
    {
        var at = Script.IndexOf("\n[Code]", StringComparison.Ordinal);
        Assert.True(at >= 0, "No [Code] section");
        return Regex.Replace(Script[at..].Replace("\r", ""), @"\{ [^}]*\}", "", RegexOptions.Singleline);
    }

    /// <summary>The body of the Pascal routine whose header line is <paramref name="header"/>, between its begin and end.</summary>
    private static string Body(string header)
    {
        var match = Regex.Match(Script, $@"(?ms)^{Regex.Escape(header)}\r?$.*?^begin\r?$(?<body>.*?)^end;\r?$");
        Assert.True(match.Success, "No " + header);
        return match.Groups["body"].Value.Replace("\r", "");
    }

    /// <summary>A section's entries, each its parameters as Inno Setup reads them (quotes doubled inside).</summary>
    private static List<Dictionary<string, string>> Entries(string section)
    {
        var block = Regex.Match(Script, $@"(?m)^\[{section}\]\r?\n(?<lines>(?:(?!\[).*\r?\n)*)");
        Assert.True(block.Success, $"The installer has no [{section}] section.");
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
                var quoted = i < line.Length && line[i] == '"';
                if (quoted)
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
                entry[name] = quoted ? value.ToString() : line[i..end].Trim();
                for (i = end + 1; i < line.Length && line[i] == ' '; i++) { }
            }
            entries.Add(entry);
        }
        return entries;
    }
}
