; DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
; Not open source. No permission is granted to copy, fork or redistribute.
;
; DiscForge.iss — Inno Setup script.
;
; Build steps (on a Windows machine with Inno Setup installed):
;   1. From the repo root:   powershell -ExecutionPolicy Bypass .\installer\publish.ps1
;   2. Compile this script:  "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\DiscForge.iss
;      (or open it in the Inno Setup IDE and press F9)
;
; Output:  installer\Output\DiscForge-Setup-<version>.exe
;
; What it does:
;   - installs the self-contained DiscForge (GUI + dforge CLI) to Program Files
;   - Start Menu group with DiscForge, the CLI prompt, docs and uninstaller
;   - optional desktop icon (task on the wizard's "Select Additional Tasks")
;   - optional "add dforge to PATH" (task) so the CLI works from any terminal
;   - registers a proper uninstaller in Add/Remove Programs
;   - requests admin at install time (raw disc access needs elevation)

#define AppName        "DiscForge"
#define AppPublisher   "MaTRIX TeAm"
#define AppExeName     "DiscForge.exe"
#define CliExeName     "dforge.exe"
#define AppId          "{{B4E7B0A2-3C1D-4F2E-9A6B-DF17C0DE5F00}"

; Pull the version straight from the published GUI exe so the installer and the
; app can never disagree.
#define AppVersion GetVersionNumbersString("..\publish\DiscForge.exe")

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\publish\LICENSE.txt
OutputDir=Output
OutputBaseFilename=DiscForge-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=yes
SetupIconFile=..\src\DiscForge.App\DiscForge.ico
; Raw SPTI disc access needs elevation; install for all users.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "modifypath"; Description: "Add the dforge command-line tool to PATH"; GroupDescription: "Command line:"

[Files]
; The entire self-contained publish folder. excludes keep the licence copy and
; any stray pdb out of the payload (they're handled separately / not shipped).
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "*.pdb"
Source: "..\src\DiscForge.App\DiscForge.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";                 Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName} Command Prompt";  Filename: "{cmd}"; Parameters: "/k ""echo DiscForge CLI ready. Type  dforge --help  to begin. && title DiscForge CLI"""; WorkingDir: "{app}"; IconFilename: "{app}\DiscForge.ico"
Name: "{group}\Documentation";              Filename: "{app}\docs"
Name: "{group}\Licence";                    Filename: "{app}\LICENSE.txt"
Name: "{group}\Uninstall {#AppName}";       Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";           Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; The app manifest is requireAdministrator, but Inno runs post-install steps
; as the original (non-elevated) user — so a direct launch fails with
; "code 740: requires elevation". shellexec routes the launch through
; ShellExecute, which lets Windows show a UAC prompt and start the app
; elevated, exactly as a Start-menu launch would.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent shellexec

[Code]
// PATH management for the dforge CLI.
// Adds/removes the install dir on the system PATH when the modifypath task is
// chosen. Idempotent: never double-adds, and only strips its own entry on
// uninstall.

const
  EnvKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

function PathContains(const Paths, Dir: string): Boolean;
begin
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0;
end;

procedure AddToPath(const Dir: string);
var
  Paths: string;
begin
  if not RegQueryStringValue(HKLM, EnvKey, 'Path', Paths) then
    Paths := '';
  if PathContains(Paths, Dir) then
    exit;
  if (Paths <> '') and (Paths[Length(Paths)] <> ';') then
    Paths := Paths + ';';
  RegWriteExpandStringValue(HKLM, EnvKey, 'Path', Paths + Dir);
  { The [Setup] ChangesEnvironment=yes directive makes Inno broadcast
    WM_SETTINGCHANGE after install, so new terminals pick this up. }
end;

procedure RemoveFromPath(const Dir: string);
var
  Paths: string;
begin
  if not RegQueryStringValue(HKLM, EnvKey, 'Path', Paths) then
    exit;
  if not PathContains(Paths, Dir) then
    exit;
  { Rebuild without the entry, case-insensitively. }
  Paths := ';' + Paths + ';';
  StringChangeEx(Paths, ';' + Dir + ';', ';', True);
  { Trim the sentinel semicolons. }
  if (Length(Paths) > 0) and (Paths[1] = ';') then Delete(Paths, 1, 1);
  if (Length(Paths) > 0) and (Paths[Length(Paths)] = ';') then Delete(Paths, Length(Paths), 1);
  RegWriteExpandStringValue(HKLM, EnvKey, 'Path', Paths);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    if WizardIsTaskSelected('modifypath') then
      AddToPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveFromPath(ExpandConstant('{app}'));
end;
