; InfoPanel's Windows installer (Inno Setup 6). Built by build.ps1, which publishes the engine and passes the defines
; below; see docs/2.0/packaging.md. It replaces InfoPanel 1.x: on a PC with 1.x, Setup closes it, runs its own
; uninstaller silently, checks it is gone and removes its sign-in task before it writes anything (PrepareToInstall);
; 1.x's profiles and settings stay. One name, "InfoPanel", for everything: the folder, the Start menu, the Apps list,
; the Run value and the Task Scheduler folder (\InfoPanel).

#ifndef AppVersion
  #error Build with packaging\windows\build.ps1 (it defines AppVersion, FileVersion, PublishDir, RuntimeInstaller, RuntimeVersion and OutputDir).
#endif

#define AppName "InfoPanel"
#define AppExeName "InfoPanel.exe"
#define AppPublisher "Habib Rehman"
#define AppURL "https://infopanel.net"
; Start when I sign in (the tray's, WinStartup): the Run value AppName under the user's Run key, and Task Manager's switch.
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
#define ApprovedKey "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
#define RepoRoot AddBackslash(SourcePath) + "..\.."
#define RuntimeFile ExtractFileName(RuntimeInstaller)

[Setup]
; 2.0's own AppId, kept for every 2.0 release. Not 1.x's ({761BE813-4A06-4646-B930-3E647446A307}): 1.x's 32-bit Setup
; registered it in the 32-bit view, and Inno Setup looks for an earlier install only in its own view, so taking it
; would upgrade nothing. 1.x is removed by its own uninstaller instead (PrepareToInstall).
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
; a /DIR that resolves anywhere else is refused before anything is stopped or written (PrepareToInstall). The 64-bit
; C:\Program Files\InfoPanel; 1.x's 32-bit Setup installed into C:\Program Files (x86)\InfoPanel.
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
UsePreviousAppDir=no
; The Start menu group is always "InfoPanel" (1.x's group's path, which its uninstaller empties first), whatever an
; earlier build used, and no page offers another.
DefaultGroupName={#AppName}
UsePreviousGroup=no
DisableProgramGroupPage=yes
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
; An earlier 2.0's folders of its own, replaced whole: names 1.x never had. Nothing under plugins: 1.x's uninstaller
; leaves its plugins' .dll.ini settings there (plugins\InfoPanel.Extras\ for its bundled one), and a 1.x once installed
; by hand into this folder leaves them here. (The retired plugins\InfoPanel.Extras of 2.0's own early builds was only
; ever in their own folder, which their uninstaller empties.)
Type: filesandordirs; Name: "{app}\sensor-helper"
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
; Start when I sign in, ticked: the tray's Run value ("C:\Program Files\InfoPanel\InfoPanel.exe"), written by reg.exe as
; the user who ran Setup, into that user's HKCU (Setup's own is the administrator's when another user elevated it).
; Offered, as the launch, only when that user's token is unelevated: Setup started elevated (Run as administrator, maybe
; with another administrator's credentials) can't run anything as the user who started it, so it writes nothing.
; Unticked, nothing changes.
; A silent install writes it too, unless /STARTATSIGNIN=no.
Filename: "{sys}\reg.exe"; Parameters: "add ""HKCU\{#RunKey}"" /v ""{#AppName}"" /t REG_SZ /d ""\""{app}\{#AppExeName}\"""" /f"; Description: "Start {#AppName} when I sign in"; Flags: postinstall runasoriginaluser runhidden; Check: OriginalUserUnelevated and StartAtSignInAsked; BeforeInstall: ClearStartupSwitch

[Code]
const
  RunKey = '{#RunKey}';
  ApprovedKey = '{#ApprovedKey}';
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
  { This install's own uninstall key, which Setup writes in the 64-bit view (it installs in 64-bit mode). }
  OwnKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{B63E31BF-4503-4A62-BB7A-D021C854BEEA}_is1';
  { InfoPanel 1.x's uninstall key: its 32-bit Setup wrote it in the 32-bit view. Setup is a 32-bit process, so every
    read names its view (HKLM32, HKLM64), never plain HKLM. }
  V1Key = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{761BE813-4A06-4646-B930-3E647446A307}_is1';
  { 1.x's Run on startup: a task of this name at the Task Scheduler's root. }
  V1TaskName = 'InfoPanel';
  { How long 1.x's ended programs get to go, and its uninstall to show it is gone after its uninstaller returned. }
  V1StopWait = 10000;
  V1GoneWait = 10000;

var
  DotnetChecked, DotnetMissing, RuntimeNeedsRestart: Boolean;
  ElevationChecked, OriginalUnelevated: Boolean;
  WmiReady: Boolean;
  WmiService: Variant;
  { InfoPanel 1.x as its uninstall key says (FindV1): the view it was found in, its uninstaller, folder and version. }
  V1Found: Boolean;
  V1Root: Integer;
  V1Uninstaller, V1Folder, V1Version: String;

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
      Log('The user who started Setup is unelevated: the finish page offers the launch and Start when I sign in')
    else
      Log(Format('The user who started Setup is elevated (or unknown, %d): the finish page offers no launch and no Start when I sign in', [Code]));
  end;
  Result := OriginalUnelevated;
end;

{ Start when I sign in is offered (ticked) unless the command line says /STARTATSIGNIN=no. }
function StartAtSignInAsked: Boolean;
begin
  Result := CompareText(ExpandConstant('{param:STARTATSIGNIN|yes}'), 'no') <> 0;
end;

{ Before the Run value is written: Task Manager's switch for it cleared, as the tray's Start when I sign in clears it, as
  the user who ran Setup. reg.exe answers 1 when there is none. }
procedure ClearStartupSwitch;
var
  Code: Integer;
begin
  if not ExecAsOriginalUser(ExpandConstant('{sys}\reg.exe'), 'delete "HKCU\{#ApprovedKey}" /v "{#AppName}" /f', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Log(Format('Task Manager''s switch for the Run value {#AppName} cleared: reg.exe exited %d', [Code]));
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

{ '' unless an earlier test build of 2.0 (this AppId) is installed in another folder (its key's InstallLocation): this
  Setup doesn't remove it; the user uninstalls it first, by the name its key gives. A 2.0 in this folder is an update. }
function EarlierBuildProblem: String;
var
  Location, Name: String;
begin
  Result := '';
  if not RegQueryStringValue(HKLM64, OwnKey, 'InstallLocation', Location) or (Location = '') then
    if not RegQueryStringValue(HKLM64, OwnKey, 'Inno Setup: App Path', Location) then
      Location := '';
  if (Location = '') or SamePath(Location, ExpandConstant('{app}')) then
    Exit;
  if not RegQueryStringValue(HKLM64, OwnKey, 'DisplayName', Name) or (Name = '') then
    Name := '{#AppName}';
  Location := RemoveBackslashUnlessRoot(Location);
  Result := 'An earlier test build of {#AppName} is installed in ' + Location + '. Uninstall it ("' + Name + '") from Settings ' + #$203A + ' Apps, then run this setup again.';
  Log('Refused: an earlier build, ' + Name + ', is installed in ' + Location);
end;

{ Reads InfoPanel 1.x's uninstall key in one view (Root: HKLM32 or HKLM64): False when it isn't there. Its folder is
  InstallLocation, else Inno Setup's App Path, else its uninstaller's folder (Inno Setup's uninstaller is in it). }
function ReadV1(const Root: Integer): Boolean;
begin
  Result := RegKeyExists(Root, V1Key);
  if not Result then
    Exit;
  V1Root := Root;
  V1Uninstaller := '';
  V1Folder := '';
  V1Version := '';
  RegQueryStringValue(Root, V1Key, 'UninstallString', V1Uninstaller);
  if not RegQueryStringValue(Root, V1Key, 'InstallLocation', V1Folder) or (V1Folder = '') then
    RegQueryStringValue(Root, V1Key, 'Inno Setup: App Path', V1Folder);
  if (V1Folder = '') and (V1Uninstaller <> '') then
    V1Folder := ExtractFileDir(RemoveQuotes(V1Uninstaller));
  if not RegQueryStringValue(Root, V1Key, 'DisplayVersion', V1Version) or (V1Version = '') then
    V1Version := '1.x';
end;

{ Whether InfoPanel 1.x is installed: its uninstall key in the 32-bit view (where its 32-bit Setup wrote it), else the
  64-bit one. Never a folder guessed. Read again at each call. }
function FindV1: Boolean;
var
  View: String;
begin
  if ReadV1(HKLM32) then
    V1Found := True
  else
    V1Found := ReadV1(HKLM64);
  if V1Found then
  begin
    View := '64-bit';
    if V1Root = HKLM32 then
      View := '32-bit';
    Log(Format('InfoPanel %s is installed (its key in the %s view): folder %s, uninstaller %s', [V1Version, View, V1Folder, V1Uninstaller]));
  end
  else
    Log('No InfoPanel 1.x is installed');
  Result := V1Found;
end;

{ '' unless InfoPanel 1.x's folder (its key's) is this install's folder, holds it or is inside it (resolved, without
  case; WOW64 redirects no Program Files path, only the key's view, and both views are read): 1.x put there by hand.
  Then its programs would pass for this install's, and its uninstaller would leave its plugins' .dll.ini where this
  install writes, so Setup takes none of it on and changes nothing: the user uninstalls 1.x first. }
function V1FolderProblem: String;
var
  App, Folder: String;
begin
  Result := '';
  if V1Folder = '' then
    Exit;
  App := ExpandConstant('{app}');
  if not (SamePath(V1Folder, App) or IsUnder(App, V1Folder) or IsUnder(V1Folder, App)) then
    Exit;
  Folder := RemoveBackslashUnlessRoot(V1Folder);
  Result := 'InfoPanel ' + V1Version + ' is installed in the folder this setup uses (' + Folder + '). Uninstall it from Settings ' + #$203A + ' Apps, then run this setup again; its profiles and settings are kept.';
  Log('Refused: InfoPanel ' + V1Version + ' is installed in ' + Folder + ', which overlaps ' + App);
end;

{ Whether the InfoPanel.exe with this process id is gone: a second look, by its id and name. False when Windows
  doesn't answer. }
function V1ProcessGone(const ProcessId: Integer): Boolean;
var
  Processes: Variant;
begin
  try
    Processes := Wmi.ExecQuery(Format('SELECT ProcessId FROM Win32_Process WHERE ProcessId = %d AND Name = ''{#AppExeName}''', [ProcessId]));
    Result := Processes.Count = 0;
  except
    Result := False;
    Log(Format('Looking again at process %d: %s', [ProcessId, GetExceptionMessage]));
  end;
end;

{ 1.x's running programs: every InfoPanel.exe (its plugin hosts are InfoPanel.exe too, so the query lists them all)
  whose path is under 1.x's folder and never under this install's (by then apart from 1.x's, V1FolderProblem: what is
  under it is 2.0's own). Returns how many, listed in Listed, only when every row was read and placed: its path read
  and not empty, then under 1.x's folder (counted), under this install's or elsewhere (another program of that name).
  A row whose path is Null or empty, or whose reading throws, makes the whole answer -1 (unknown, never none) unless a
  second look by its id finds it gone; -1 too when the processes couldn't be listed. Only a count ends anything: with
  EndThem, the counted ones are ended (Terminate) once the whole list was placed. None without a folder. }
function V1Processes(EndThem: Boolean; var Listed: String): Integer;
var
  Processes, Process: Variant;
  Found: array of Variant;
  Unread: array of Integer;
  I, Pid, Code: Integer;
  Path: String;
  Placed, Unknown: Boolean;
begin
  Result := 0;
  Listed := '';
  if V1Folder = '' then
    Exit;
  Unknown := False;
  try
    Processes := Wmi.ExecQuery('SELECT Name, ProcessId, SessionId, ExecutablePath FROM Win32_Process WHERE Name = ''{#AppExeName}''');
    for I := 0 to Processes.Count - 1 do
    begin
      Placed := False;
      Pid := -1;
      try
        Process := Processes.ItemIndex(I);
        Pid := Integer(Process.ProcessId);
        Path := TextOf(Process.ExecutablePath);
        if Path <> '' then
        begin
          if IsUnder(Path, V1Folder) and not IsUnder(Path, ExpandConstant('{app}')) then
          begin
            Listed := Listed + Format(' %d (session %d)', [Pid, Integer(Process.SessionId)]);
            SetArrayLength(Found, GetArrayLength(Found) + 1);
            Found[GetArrayLength(Found) - 1] := Process;
          end;
          Placed := True;
        end;
      except
        Log('Reading a process: ' + GetExceptionMessage);
      end;
      if not Placed then
      begin
        Log(Format('Process %d (%s) couldn''t be read: unknown unless it is gone', [Pid, '{#AppExeName}']));
        if Pid < 0 then
          Unknown := True
        else
        begin
          SetArrayLength(Unread, GetArrayLength(Unread) + 1);
          Unread[GetArrayLength(Unread) - 1] := Pid;
        end;
      end;
    end;
    for I := 0 to GetArrayLength(Unread) - 1 do
      if not V1ProcessGone(Unread[I]) then
        Unknown := True;
    if Unknown then
    begin
      Result := -1;
      Log('InfoPanel 1.x''s processes: unknown, a process that may be its couldn''t be read');
    end
    else
    begin
      Result := GetArrayLength(Found);
      if EndThem then
        for I := 0 to Result - 1 do
        begin
          Process := Found[I];
          try
            Code := Process.Terminate(1);
            Log(Format('Ending InfoPanel %s: %s (%d, session %d): %d', [V1Version, TextOf(Process.ExecutablePath), Integer(Process.ProcessId), Integer(Process.SessionId), Code]));
          except
            Log('Ending a process: ' + GetExceptionMessage);
          end;
        end;
    end;
  except
    Result := -1;
    Log('Listing InfoPanel 1.x''s processes failed: ' + GetExceptionMessage);
  end;
end;

{ Ends InfoPanel 1.x by force, as 1.x's own updates did (it has no window to ask; the Ready page says to save first),
  waits up to V1StopWait for it to go, and offers Retry while one is left or the list can't be read (a silent run
  answers Cancel): '' only once the list was read and none is left, so its uninstaller never runs while one may. Setup
  is elevated and Wmi enables the debug privilege, so a 1.x started elevated by its task, or in another session, is
  ended too. }
function CloseV1: String;
var
  Count, Waited: Integer;
  Listed, Unknown: String;
begin
  Unknown := 'InfoPanel ' + V1Version + ' may still be running, and Setup can''t check (Windows didn''t list its programs). Close it from its icon in the notification area, then Retry; or uninstall it from Settings ' + #$203A + ' Apps, then run this setup again.';
  repeat
    Result := '';
    Count := V1Processes(True, Listed);
    if Count < 0 then
      Result := Unknown
    else if Count > 0 then
    begin
      Waited := 0;
      repeat
        Sleep(250);
        Waited := Waited + 250;
        Count := V1Processes(False, Listed);
      until (Count = 0) or (Waited >= V1StopWait);
      if Count < 0 then
        Result := Unknown
      else if Count > 0 then
      begin
        Log(Format('InfoPanel %s still running after %d ms:%s', [V1Version, Waited, Listed]));
        Result := 'InfoPanel ' + V1Version + ' is still running (process' + Listed + ') and couldn''t be closed. Close it, then try again.';
      end
      else
        Log(Format('InfoPanel %s closed within %d ms', [V1Version, Waited]));
    end;
    if Result = Unknown then
      Log('InfoPanel ' + V1Version + '''s processes couldn''t be listed: Setup doesn''t uninstall it');
  until (Result = '') or (SuppressibleMsgBox(Result, mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY);
end;

{ Whether 1.x is gone: its key from the view it was found in, and its program from its folder. }
function V1Gone: Boolean;
begin
  Result := not RegKeyExists(V1Root, V1Key) and ((V1Folder = '') or not FileExists(AddBackslash(V1Folder) + '{#AppExeName}'));
end;

{ Runs InfoPanel 1.x's own uninstaller silently and waits for it; it removes what 1.x's Setup installed (the program,
  its shortcuts, its key) and never 1.x's data. Then 1.x is judged by what is left, not by the exit code alone: its
  uninstaller's first phase returns as its second, run from a copy, finishes, so V1Gone is asked for up to V1GoneWait.
  '' when 1.x is gone, else why (a missing uninstaller too). }
function RemoveV1: String;
var
  Uninstaller: String;
  Code, Waited: Integer;
begin
  Result := '';
  Uninstaller := RemoveQuotes(V1Uninstaller);
  Code := -1;
  WizardForm.PageDescriptionLabel.Caption := 'Removing InfoPanel ' + V1Version + '...';
  if (Uninstaller = '') or not FileExists(Uninstaller) then
    Log('InfoPanel ' + V1Version + '''s uninstaller is missing: ' + V1Uninstaller)
  else if not Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Log(Format('InfoPanel %s''s uninstaller could not be started: %d %s', [V1Version, Code, SysErrorMessage(Code)]))
  else
  begin
    Log(Format('InfoPanel %s''s uninstaller exited %d', [V1Version, Code]));
    Waited := 0;
    while not V1Gone and (Waited < V1GoneWait) do
    begin
      Sleep(250);
      Waited := Waited + 250;
    end;
  end;
  if not V1Gone then
  begin
    Result := 'InfoPanel ' + V1Version + ' couldn''t be uninstalled (code ' + IntToStr(Code) + '). Uninstall it from Settings ' + #$203A + ' Apps, then run this setup again.';
    Log('InfoPanel ' + V1Version + ' is still installed: Setup stops');
  end
  else
    Log('InfoPanel ' + V1Version + ' is uninstalled');
end;

{ Whether Task is 1.x's Run on startup: exactly one action, a program (only an Exec action has a Path: any other
  throws) whose file name is InfoPanel.exe, as 1.x registers it. }
function IsV1Task(const Task: Variant): Boolean;
var
  Actions, Action: Variant;
  Path: String;
begin
  Result := False;
  try
    Actions := Task.Definition.Actions;
    if Actions.Count <> 1 then
      Exit;
    Action := Actions.Item(1);
    Path := Action.Path;
    Result := CompareText(ExtractFileName(RemoveQuotes(Path)), '{#AppExeName}') = 0;
  except
    Result := False;
  end;
end;

{ 1.x's Run on startup, the task \InfoPanel at the Task Scheduler's root, stopped if running and deleted when it is 1.x's
  (IsV1Task); also when 1.x was uninstalled earlier: its task would start a removed program, and the helper's folder
  \InfoPanel can't be made beside a task of that name. Only a task is asked for: a folder \InfoPanel (this install's
  helper tasks) is never touched. Anything else of that name is left alone. A failure is logged; Setup goes on. }
procedure RemoveV1Task;
var
  Service, Root, Task: Variant;
begin
  try
    Service := CreateOleObject('Schedule.Service');
    Service.Connect;
    Root := Service.GetFolder('\');
    try
      Task := Root.GetTask(V1TaskName);
    except
      Log('No task \' + V1TaskName + ' at the Task Scheduler''s root');
      Exit;
    end;
    if not IsV1Task(Task) then
    begin
      Log('Left alone: the task \' + V1TaskName + ' (not InfoPanel 1.x''s)');
      Exit;
    end;
    try
      if Task.State = 4 then { TASK_STATE_RUNNING }
      begin
        Log('Stopping InfoPanel 1.x''s task \' + V1TaskName);
        Task.Stop(0);
      end;
    except
      Log('Stopping InfoPanel 1.x''s task \' + V1TaskName + ': ' + GetExceptionMessage);
    end;
    Root.DeleteTask(V1TaskName, 0);
    Log('Removed InfoPanel 1.x''s task \' + V1TaskName);
  except
    Log('InfoPanel 1.x''s task \' + V1TaskName + ': ' + GetExceptionMessage);
  end;
end;

{ One block of the Ready page's memo, after a blank line when there is one before it. }
procedure AddReadyBlock(var Memo: String; const Block, NewLine: String);
begin
  if Block = '' then
    Exit;
  if Memo <> '' then
    Memo := Memo + NewLine + NewLine;
  Memo := Memo + Block;
end;

{ The Ready page: Inno Setup's own blocks, then, only when 1.x is installed, what Setup does with it. }
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := '';
  AddReadyBlock(Result, MemoUserInfoInfo, NewLine);
  AddReadyBlock(Result, MemoDirInfo, NewLine);
  AddReadyBlock(Result, MemoTypeInfo, NewLine);
  AddReadyBlock(Result, MemoComponentsInfo, NewLine);
  AddReadyBlock(Result, MemoGroupInfo, NewLine);
  AddReadyBlock(Result, MemoTasksInfo, NewLine);
  if FindV1 then
    AddReadyBlock(Result, 'InfoPanel ' + V1Version + ':' + NewLine +
      Space + 'Setup closes it if it is running and uninstalls it.' + NewLine +
      Space + 'Its profiles and settings stay on this PC; InfoPanel brings them over as copies.' + NewLine +
      Space + 'Save any work in it first.', NewLine);
end;

{ Before anything is written, in this order, each refusal stopping Setup with its message: the one folder; no earlier
  test build elsewhere; InfoPanel 1.x, when installed, refused in a folder overlapping this one, else closed and
  uninstalled by its own uninstaller and checked gone; 1.x's sign-in task removed (always); then this install's own
  helper tasks and engines stopped (an update). }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := DestinationProblem;
  if Result <> '' then
    Exit;
  Result := EarlierBuildProblem;
  if Result <> '' then
    Exit;
  if FindV1 then
  begin
    Result := V1FolderProblem;
    if Result <> '' then
      Exit;
    Result := CloseV1;
    if Result <> '' then
      Exit;
    Result := RemoveV1;
    if Result <> '' then
      Exit;
  end;
  RemoveV1Task;
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

{ One user's Start when I sign in (Root and Hive: HKCU and '', or HKU and a loaded hive's name and a backslash), when its
  program is this install's (the resolved path; its arguments don't count). Read here and checked here, whichever hive. }
procedure RemoveRunValueIn(const Root: Integer; const Hive: String);
var
  Command: String;
begin
  if RegQueryStringValue(Root, Hive + RunKey, RunValue, Command) then
  begin
    if SamePath(CommandProgram(Command), ExpandConstant('{app}\{#AppExeName}')) then
    begin
      RegDeleteValue(Root, Hive + RunKey, RunValue);
      RegDeleteValue(Root, Hive + ApprovedKey, RunValue);
      Log('Removed the Run value ' + RunValue + ' (' + Hive + RunKey + ')');
    end
    else
      Log('Left alone: the Run value ' + RunValue + ' (' + Hive + RunKey + ') starts ' + CommandProgram(Command));
  end;
end;

{ Start when I sign in, removed from every hive loaded under HKEY_USERS (each signed-in user's: the uninstalling user's
  too when another administrator elevated the uninstall, which then runs as that administrator, and the uninstaller can
  run nothing as the user who started it) and from the uninstaller's own HKCU. A user who isn't signed in keeps theirs. }
procedure RemoveRunValues;
var
  Hives: TArrayOfString;
  I: Integer;
begin
  if RegGetSubkeyNames(HKU, '', Hives) then
    for I := 0 to GetArrayLength(Hives) - 1 do
      RemoveRunValueIn(HKU, Hives[I] + '\');
  RemoveRunValueIn(HKCU, '');
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
    RemoveRunValues;
  end;
end;
