; WinWaechter installer (Inno Setup 6). Build via build-release.ps1.
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\0.1.0"
#endif

[Setup]
AppId={{9F3C2A71-5B84-4E0D-A6D2-1C7E48B05F3A}
AppName=WinWächter
AppVersion={#AppVersion}
AppPublisher=WinWächter
DefaultDirName={autopf}\WinWaechter
DefaultGroupName=WinWächter
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
OutputDir=..\dist
OutputBaseFilename=WinWaechter-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\WinWaechter.App.exe
#ifexist "..\assets\icon\winwaechter.ico"
SetupIconFile=..\assets\icon\winwaechter.ico
#endif

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{commonappdata}\WinWaechter"

[Icons]
Name: "{autoprograms}\WinWächter"; Filename: "{app}\WinWaechter.App.exe"
Name: "{autodesktop}\WinWächter"; Filename: "{app}\WinWaechter.App.exe"; Tasks: desktopicon

[Run]
; Data dir: owner Administrators, only Administrators/SYSTEM full, Users read (/L: never through a link).
; A junction at the data dir aborts setup earlier (PrepareToInstall).
Filename: "{sys}\icacls.exe"; Parameters: """{commonappdata}\WinWaechter"" /setowner *S-1-5-32-544 /L"; Flags: runhidden waituntilterminated
Filename: "{sys}\icacls.exe"; Parameters: """{commonappdata}\WinWaechter"" /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /L"; Flags: runhidden waituntilterminated
; Everything below drops its own rules and inherits the root ACL. Owners are left alone on purpose:
; a file a normal user planted keeps its owner, and the app ignores journals not owned by
; Administrators/SYSTEM. The app takes over subfolder ownership itself at every start.
Filename: "{sys}\icacls.exe"; Parameters: """{commonappdata}\WinWaechter\*"" /reset /T /C /L /Q"; Flags: runhidden waituntilterminated
; Start after install (interactive and silent, e.g. self-update)
Filename: "{app}\WinWaechter.App.exe"; Description: "WinWächter starten"; Flags: nowait postinstall skipifsilent
Filename: "{app}\WinWaechter.App.exe"; Flags: nowait skipifnotsilent

[Code]
const
  FILE_ATTRIBUTE_REPARSE_POINT = $400;
  INVALID_FILE_ATTRIBUTES = $FFFFFFFF;

var
  KeepData: Boolean;

function GetFileAttributesW(lpFileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

// True for junctions and symbolic links; False when the path does not exist.
function IsReparsePoint(const Path: String): Boolean;
var
  Attr: Cardinal;
begin
  Attr := GetFileAttributesW(Path);
  Result := (Attr <> INVALID_FILE_ATTRIBUTES) and ((Attr and FILE_ATTRIBUTE_REPARSE_POINT) <> 0);
end;

// A pre-created junction would make icacls rewrite the ACL of its target: refuse to install.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  DataDir: String;
begin
  Result := '';
  DataDir := ExpandConstant('{commonappdata}\WinWaechter');
  if IsReparsePoint(ExpandConstant('{commonappdata}')) or IsReparsePoint(DataDir) then
    Result := 'Der Datenordner ' + DataDir + ' ist eine Verknüpfung (Junction oder symbolischer Link). ' +
      'Aus Sicherheitsgründen wird WinWächter nicht installiert. Bitte die Verknüpfung entfernen und das Setup erneut starten.';
end;

function InitializeUninstall(): Boolean;
begin
  KeepData := True;
  // Silent uninstall keeps the data
  if not UninstallSilent then
    KeepData := MsgBox('Sicherungen und Ablage (Ordner ' + ExpandConstant('{commonappdata}\WinWaechter') + ') behalten?',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDYES;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // never delete through a link into some other folder
  if (CurUninstallStep = usPostUninstall) and (not KeepData)
     and not IsReparsePoint(ExpandConstant('{commonappdata}\WinWaechter')) then
    DelTree(ExpandConstant('{commonappdata}\WinWaechter'), True, True, True);
end;
