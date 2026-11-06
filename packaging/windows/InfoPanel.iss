; InfoPanel 2's Windows installer (Inno Setup 6). Built by build.ps1, which publishes the engine and passes the defines
; below; see docs/2.0/packaging.md. It installs beside InfoPanel 1.x and touches nothing of 1.x's: its own AppId,
; folder, Start-menu group, Run value ("InfoPanel 2") and Task Scheduler folder (\InfoPanel 2).

#ifndef AppVersion
  #error Build with packaging\windows\build.ps1 (it defines AppVersion, FileVersion, PublishDir, RuntimeInstaller, RuntimeVersion and OutputDir).
#endif

#define AppName "InfoPanel 2"
#define AppExeName "InfoPanel.exe"
#define AppPublisher "Habib Rehman"
#define AppURL "https://infopanel.net"
#define RepoRoot AddBackslash(SourcePath) + "..\.."
#define RuntimeFile ExtractFileName(RuntimeInstaller)

[Setup]
; InfoPanel 2's own AppId, kept for every 2.0 release (1.x's is {761BE813-4A06-4646-B930-3E647446A307}).
AppId={{B63E31BF-4503-4A62-BB7A-D021C854BEEA}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
VersionInfoVersion={#FileVersion}
VersionInfoProductTextVersion={#AppVersion}
; One fixed folder, a user can't write (an administrator-only parent): no folder page, no earlier install's folder, and
; a /DIR that resolves anywhere else is refused before anything is stopped or written (PrepareToInstall). Never 1.x's
; C:\Program Files\InfoPanel.
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
UsePreviousAppDir=no
DefaultGroupName={#AppName}
AllowNoIcons=no
DisableWelcomePage=no
; Per machine: Read all sensors registers its SYSTEM task only for a helper under Program Files, whose files a user
; can't write.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile={#RepoRoot}\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=InfoPanel-{#AppVersion}-win-x64-setup
SetupIconFile={#RepoRoot}\packaging\windows\InfoPanel.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; A running engine is asked to stop as at a sign-out, its own orderly stop, before its files are replaced
; (PrepareToInstall); never forced. The Restart Manager then closes, without forcing, anything else holding a file, and
; Setup restarts nothing (that would run it elevated).
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; Excludes: "*.pdb"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\LICENSES.md"; DestDir: "{app}"; Flags: ignoreversion
; The sensor helper's runtime: Microsoft's .NET runtime installer, unpacked and run only when no .NET 8 or later (x64)
; is installed (build.ps1 pins its version and SHA-512).
Source: "{#RuntimeInstaller}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsDotnetRuntime

[InstallDelete]
; An earlier 2.0's folders of its own, replaced whole (never anything of 1.x's, which lives elsewhere).
Type: filesandordirs; Name: "{app}\sensor-helper"
Type: filesandordirs; Name: "{app}\plugins\InfoPanel.Extras"
Type: filesandordirs; Name: "{app}\pawnio"

[UninstallDelete]
; What the engine writes beside the helper when Read all sensors is turned on or off.
Type: filesandordirs; Name: "{app}\sensor-helper"

[Registry]
; The infopanel: link (docs/2.0/desktop-shell.md): a web page's link starts InfoPanel, or asks the running one, to open the
; editor. The link is the command's only argument and the engine reads nothing from it. Per machine (HKA is HKLM in this
; administrative install); the whole key is removed at uninstall.
Root: HKA; Subkey: "Software\Classes\infopanel"; ValueType: string; ValueName: ""; ValueData: "URL:InfoPanel"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\infopanel"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\infopanel\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"",0"
Root: HKA; Subkey: "Software\Classes\infopanel\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Started as the user who ran Setup, never with administrator rights (only the sensor helper's task runs elevated): offered
; only when that user's token is unelevated. Setup started elevated (Run as administrator, an elevated prompt) offers none.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: OriginalUserUnelevated


[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  ApprovedKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';
  RunValue = '{#AppName}';
  TaskFolder = '{#AppName}';
  HelperTaskPrefix = 'Sensor helper ';
  { The engine's hidden top-level window (its desktop thread's), which takes the session's end. }
  EngineWindowClass = 'InfoPanelMessageWindow';
  EngineWindowTitle = 'InfoPanel';
  WM_ENDSESSION = $0016;
  ENDSESSION_CLOSEAPP = 1;
  { How long an engine gets for its orderly stop (its final checkpoint of unsaved changes), and its plugin hosts and
    helpers after it. }
  EngineStopWait = 20000;
  LeftoverWait = 5000;

var
  DotnetChecked, DotnetMissing, RuntimeNeedsRestart: Boolean;
  ElevationChecked, OriginalUnelevated: Boolean;
  WmiReady: Boolean;
  WmiService: Variant;

#include AddBackslash(SourcePath) + "dotnet-check.iss"

{ No Microsoft.NETCore.App 8.0 or later for x64 where the helper's apphost looks: the install location .NET's setup
  registers (32-bit view, as the host reads it), else Program Files\dotnet. }
function NeedsDotnetRuntime: Boolean;
var
  Location: String;
begin
  if not DotnetChecked then
  begin
    DotnetChecked := True;
    if not RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Location) or (Location = '') then
      Location := ExpandConstant('{commonpf64}\dotnet');
    DotnetMissing := not HasNetCore8(Location);
    Log(Format('A stable .NET 8 or later (x64) under %s: %s', [Location, IntToStr(Ord(not DotnetMissing))]));
  end;
  Result := DotnetMissing;
end;

procedure InstallDotnetRuntime;
var
  Code: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Installing the .NET {#RuntimeVersion} runtime for the sensor helper...';
  if not Exec(ExpandConstant('{tmp}\{#RuntimeFile}'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Log(Format('.NET runtime installer {#RuntimeFile} exited %d', [Code]));
  if (Code = 3010) or (Code = 1641) then
    RuntimeNeedsRestart := True
  else if Code <> 0 then
    SuppressibleMsgBox('The .NET runtime could not be installed (code ' + IntToStr(Code) + '). {#AppName} works without it, but Read all sensors needs it: install the .NET {#RuntimeVersion} runtime (x64) from Microsoft, then turn it on.',
      mbInformation, MB_OK, IDOK);
end;

{ Whether the user who started Setup has an unelevated token: integrity Medium (S-1-16-8192), an administrator's
  filtered token or a standard user's. Setup started elevated (Run as administrator, an elevated prompt, UAC off) has an
  elevated original user, and runasoriginaluser would start the engine elevated too: then no launch is offered. Asked
  once, as that user; anything but a found Medium label counts as elevated. The label is found by its SID as a whole
  field of whoami's CSV output (,"S-1-16-8192",): a group's name can't hold a quote or a comma, so no name passes for it. }
function OriginalUserUnelevated: Boolean;
var
  Code: Integer;
  Sys: String;
begin
  if not ElevationChecked then
  begin
    ElevationChecked := True;
    Code := -1;
    Sys := ExpandConstant('{sys}');
    OriginalUnelevated := ExecAsOriginalUser(ExpandConstant('{cmd}'),
      '/d /c ' + Sys + '\whoami.exe /groups /fo csv /nh | ' + Sys + '\findstr.exe /c:",\"S-1-16-8192\"," >nul', '', SW_HIDE, ewWaitUntilTerminated, Code)
      and (Code = 0);
    if OriginalUnelevated then
      Log('The user who started Setup is unelevated: the finish page offers the launch')
    else
      Log(Format('The user who started Setup is elevated (or unknown, %d): the finish page offers no launch', [Code]));
  end;
  Result := OriginalUnelevated;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    OriginalUserUnelevated;
    if NeedsDotnetRuntime then
      InstallDotnetRuntime;
  end;
end;

function NeedRestart: Boolean;
begin
  Result := RuntimeNeedsRestart;
end;

{ Path, fully resolved (".." and "." taken out, slashes made backslashes) and without a trailing backslash. }
function Resolved(const Path: String): String;
begin
  Result := RemoveBackslashUnlessRoot(ExpandFileName(Path));
end;

{ Whether A and B are the same path once resolved, without case. Junctions and short names aren't followed: such a path
  never counts as this install's, which only leaves it alone. }
function SamePath(const A, B: String): Boolean;
begin
  Result := (A <> '') and (B <> '') and (CompareText(Resolved(A), Resolved(B)) = 0);
end;

function IsUnder(const Path, Folder: String): Boolean;
begin
  Result := (Path <> '') and (Pos(Lowercase(AddBackslash(Resolved(Folder))), Lowercase(Resolved(Path))) = 1);
end;

{ '' when the destination is this program's one folder, else why Setup refuses it. }
function DestinationProblem: String;
var
  Want, Got: String;
begin
  Want := Resolved(ExpandConstant('{autopf}\{#AppName}'));
  Got := Resolved(ExpandConstant('{app}'));
  if CompareText(Want, Got) = 0 then
    Result := ''
  else
  begin
    Result := '{#AppName} installs only into ' + Want + ', not ' + Got + '. Run Setup without /DIR.';
    Log('Refused the destination ' + Got + ' (only ' + Want + ')');
  end;
end;

function GetCurrentProcessId: DWORD; external 'GetCurrentProcessId@kernel32.dll stdcall';
function FindWindowEx(Parent, ChildAfter: HWND; ClassName, WindowName: String): HWND; external 'FindWindowExW@user32.dll stdcall';
function GetWindowThreadProcessId(Wnd: HWND; var ProcessId: DWORD): DWORD; external 'GetWindowThreadProcessId@user32.dll stdcall';

function Wmi: Variant;
var
  Locator: Variant;
begin
  if not WmiReady then
  begin
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    { Ending a SYSTEM process (a helper left behind) takes the debug privilege, which an administrator holds but hasn't
      enabled. }
    Locator.Security_.Privileges.AddAsString('SeDebugPrivilege', True);
    WmiService := Locator.ConnectServer('.', 'root\CIMV2');
    WmiReady := True;
  end;
  Result := WmiService;
end;

function TextOf(const Value: Variant): String;
begin
  Result := '';
  if not VarIsNull(Value) then
    Result := Value;
end;

{ The first argument after the program in CommandLine, split as the program's runtime splits it (CommandLineToArgvW's
  rules: the program's own name ends at the closing quote if it opens with one, else at the first space or tab; then
  quotes group, and backslashes count only before a quote). }
function FirstArgument(const CommandLine: String): String;
var
  I, N, Slashes: Integer;
  InQuote: Boolean;
begin
  Result := '';
  N := Length(CommandLine);
  I := 1;
  if (I <= N) and (CommandLine[I] = '"') then
  begin
    I := I + 1;
    while (I <= N) and (CommandLine[I] <> '"') do
      I := I + 1;
    I := I + 1;
  end
  else
    while (I <= N) and (CommandLine[I] <> ' ') and (CommandLine[I] <> #9) do
      I := I + 1;
  while (I <= N) and ((CommandLine[I] = ' ') or (CommandLine[I] = #9)) do
    I := I + 1;
  InQuote := False;
  while I <= N do
  begin
    if CommandLine[I] = '\' then
    begin
      Slashes := 0;
      while (I <= N) and (CommandLine[I] = '\') do
      begin
        Slashes := Slashes + 1;
        I := I + 1;
      end;
      if (I <= N) and (CommandLine[I] = '"') then
      begin
        Result := Result + StringOfChar('\', Slashes div 2);
        if Slashes mod 2 = 1 then
        begin
          Result := Result + '"';
          I := I + 1;
        end;
      end
      else
        Result := Result + StringOfChar('\', Slashes);
    end
    else if CommandLine[I] = '"' then
    begin
      InQuote := not InQuote;
      I := I + 1;
    end
    else if (not InQuote) and ((CommandLine[I] = ' ') or (CommandLine[I] = #9)) then
      Break
    else
    begin
      Result := Result + CommandLine[I];
      I := I + 1;
    end;
  end;
end;

{ Whether Process is an engine: InfoPanel.exe but not one of its plugin hosts. The executable runs as a plugin host
  only when its first argument is exactly --plugin-host (PluginHostEntry.IsHostMode), so that is what is looked for:
  --plugin-host elsewhere in the command line (a data folder named after it) is an engine. }
function IsEngine(const Process: Variant): Boolean;
begin
  Result := (CompareText(TextOf(Process.Name), '{#AppExeName}') = 0) and (CompareStr(FirstArgument(TextOf(Process.CommandLine)), '--plugin-host') <> 0);
end;

{ This install's processes (only those under its program folder: 1.x's program is InfoPanel.exe too). With Engines, its
  engines; otherwise the rest, its plugin hosts and sensor helpers, each ended when EndThem. Returns how many, and lists
  them in Listed. ProcessId, when not 0, looks at that process only. }
function OwnProcesses(Engines, EndThem: Boolean; ProcessId: DWORD; var Listed: String): Integer;
var
  Query: String;
  Processes, Process: Variant;
  I, Code: Integer;
begin
  Result := 0;
  Listed := '';
  Query := 'SELECT Name, ProcessId, SessionId, ExecutablePath, CommandLine FROM Win32_Process WHERE (Name = ''{#AppExeName}'' OR Name = ''InfoPanel.SensorHelper.exe'')';
  if ProcessId <> 0 then
    Query := Query + ' AND ProcessId = ' + IntToStr(ProcessId);
  try
    Processes := Wmi.ExecQuery(Query);
    for I := 0 to Processes.Count - 1 do
    begin
      try
        Process := Processes.ItemIndex(I);
        if IsUnder(TextOf(Process.ExecutablePath), ExpandConstant('{app}')) and (IsEngine(Process) = Engines) then
        begin
          Result := Result + 1;
          Listed := Listed + Format(' %d (session %d)', [Integer(Process.ProcessId), Integer(Process.SessionId)]);
          if EndThem then
          begin
            Code := Process.Terminate(1);
            Log(Format('Ending %s (%d, session %d), left after its engine: %d', [TextOf(Process.ExecutablePath), Integer(Process.ProcessId), Integer(Process.SessionId), Code]));
          end;
        end;
      except
        { Ended meanwhile (a plugin host or helper ends with its engine). }
        Log('Reading a process: ' + GetExceptionMessage);
      end;
    end;
  except
    Log('Listing this install''s processes failed: ' + GetExceptionMessage);
  end;
end;

{ Asks each of this install's engines on this desktop to stop as at the end of the session (WM_ENDSESSION, which the
  Restart Manager sends too): the engine's own orderly stop, which keeps every unsaved change in recovery.json. An
  engine in another session can't be reached this way. }
procedure AskEnginesToStop;
var
  Wnd: HWND;
  Pid: DWORD;
  Listed: String;
begin
  Wnd := 0;
  repeat
    Wnd := FindWindowEx(0, Wnd, EngineWindowClass, EngineWindowTitle);
    if Wnd <> 0 then
    begin
      Pid := 0;
      GetWindowThreadProcessId(Wnd, Pid);
      if (Pid <> 0) and (OwnProcesses(True, False, Pid, Listed) > 0) then
        Log(Format('Asking the engine %d to stop: %d', [Integer(Pid), Ord(PostMessage(Wnd, WM_ENDSESSION, 1, ENDSESSION_CLOSEAPP))]));
    end;
  until Wnd = 0;
end;

{ Stops this install's engines in an orderly way, waiting for each at most EngineStopWait, then ends what they left
  (plugin hosts, helpers) after LeftoverWait. An engine is never forced: '' when none is left, else why. }
function StopOwnProcesses: String;
var
  Waited: Integer;
  Listed: String;
begin
  Result := '';
  if OwnProcesses(True, False, 0, Listed) > 0 then
  begin
    Log('This install''s engines:' + Listed);
    AskEnginesToStop;
    Waited := 0;
    while (OwnProcesses(True, False, 0, Listed) > 0) and (Waited < EngineStopWait) do
    begin
      Sleep(250);
      Waited := Waited + 250;
    end;
    if OwnProcesses(True, False, 0, Listed) > 0 then
    begin
      Log(Format('Still running after %d ms:%s', [Waited, Listed]));
      Result := '{#AppName} is still running (process' + Listed + '). Quit it from its icon in the notification area ' +
        '(unsaved changes are kept), or ask the user of that session to, then try again.';
      Exit;
    end;
    Log(Format('The engines stopped within %d ms', [Waited]));
  end;
  Waited := 0;
  while (OwnProcesses(False, False, 0, Listed) > 0) and (Waited < LeftoverWait) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  OwnProcesses(False, True, 0, Listed);
end;

{ StopOwnProcesses, offering Retry while an engine is left (a silent run answers Cancel): '' once none is left. }
function StopOwnProcessesAsking: String;
begin
  repeat
    Result := StopOwnProcesses;
  until (Result = '') or (SuppressibleMsgBox(Result, mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY);
end;

{ Whether Task is a Read all sensors task for this install: named "Sensor helper <SID>", one action, exactly this
  install's helper (its resolved path) with "--serve --user <SID>", as the helper registers it. }
function IsOwnHelperTask(const Task: Variant): Boolean;
var
  Actions, Action: Variant;
  Name, Sid, Path: String;
begin
  Result := False;
  try
    Name := Task.Name;
    if Pos(HelperTaskPrefix + 'S-1-', Name) <> 1 then
      Exit;
    Sid := Copy(Name, Length(HelperTaskPrefix) + 1, Length(Name));
    Actions := Task.Definition.Actions;
    if Actions.Count <> 1 then
      Exit;
    Action := Actions.Item(1);
    { Only an Exec action has a Path: any other throws, and isn't ours. }
    Path := Action.Path;
    if (Length(Path) >= 2) and (Path[1] = '"') and (Path[Length(Path)] = '"') then
      Path := Copy(Path, 2, Length(Path) - 2);
    Result := SamePath(Path, ExpandConstant('{app}\sensor-helper\InfoPanel.SensorHelper.exe'))
      and (TextOf(Action.Arguments) = '--serve --user ' + Sid);
  except
    Result := False;
  end;
end;

{ This install's Read all sensors tasks (every user's), through the Task Scheduler: each running one stopped (its helper
  runs as SYSTEM in session 0, which nothing else here can stop), and with Remove, each removed (theirs would run a
  removed file) and the folder once empty. The helper itself isn't run: it is a .NET program, and an elevated one would
  take the user's environment. }
procedure HelperTasks(Remove: Boolean);
var
  Service, Folder, Tasks, Task: Variant;
  I: Integer;
begin
  try
    Service := CreateOleObject('Schedule.Service');
    Service.Connect;
    try
      Folder := Service.GetFolder('\' + TaskFolder);
    except
      Log('No Task Scheduler folder \' + TaskFolder);
      Exit;
    end;
    Tasks := Folder.GetTasks(1);
    for I := Tasks.Count downto 1 do
    begin
      Task := Tasks.Item(I);
      if IsOwnHelperTask(Task) then
      begin
        try
          if Task.State = 4 then { TASK_STATE_RUNNING }
          begin
            Log('Stopping the task ' + Task.Path);
            Task.Stop(0);
          end;
        except
          Log('Stopping the task ' + Task.Path + ': ' + GetExceptionMessage);
        end;
        if Remove then
        begin
          Log('Removing the task ' + Task.Path);
          Folder.DeleteTask(Task.Name, 0);
        end;
      end
      else
        Log('Left alone: the task ' + Task.Path + ' (not this install''s helper)');
    end;
    if Remove and (Folder.GetTasks(1).Count = 0) and (Folder.GetFolders(0).Count = 0) then
    begin
      Service.GetFolder('\').DeleteFolder(TaskFolder, 0);
      Log('Removed the Task Scheduler folder \' + TaskFolder);
    end;
  except
    Log('The sensor helper''s tasks: ' + GetExceptionMessage);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := DestinationProblem;
  if Result <> '' then
    Exit;
  HelperTasks(False);
  Result := StopOwnProcessesAsking;
end;

{ The program a command line starts: its first part, quoted or up to the first space. }
function CommandProgram(const Command: String): String;
var
  Rest: String;
  Last: Integer;
begin
  Rest := Trim(Command);
  if (Rest <> '') and (Rest[1] = '"') then
  begin
    Rest := Copy(Rest, 2, Length(Rest));
    Last := Pos('"', Rest);
  end
  else
    Last := Pos(' ', Rest);
  if Last = 0 then
    Result := Rest
  else
    Result := Copy(Rest, 1, Last - 1);
end;

{ The user's Start when I sign in, when its program is this install's (the resolved path; its arguments don't count). }
procedure RemoveRunValue;
var
  Command: String;
begin
  if RegQueryStringValue(HKCU, RunKey, RunValue, Command) then
  begin
    if SamePath(CommandProgram(Command), ExpandConstant('{app}\{#AppExeName}')) then
    begin
      RegDeleteValue(HKCU, RunKey, RunValue);
      RegDeleteValue(HKCU, ApprovedKey, RunValue);
      Log('Removed the Run value ' + RunValue);
    end
    else
      Log('Left alone: the Run value ' + RunValue + ' starts ' + CommandProgram(Command));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Problem: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    HelperTasks(False);
    Problem := StopOwnProcessesAsking;
    if Problem <> '' then
    begin
      Log('Uninstall stopped: ' + Problem);
      Abort;
    end;
    HelperTasks(True);
    RemoveRunValue;
  end;
end;
