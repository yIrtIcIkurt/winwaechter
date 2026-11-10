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
AppPublisher=Tahsin Yilmaz
AppCopyright=© 2026 Tahsin Yilmaz
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
; Wizard images in all DPI sizes (built by tools\make-installer-images.ps1)
#ifexist "..\assets\installer\wizard-164x314.bmp"
WizardImageFile=..\assets\installer\wizard-164x314.bmp,..\assets\installer\wizard-205x393.bmp,..\assets\installer\wizard-246x471.bmp,..\assets\installer\wizard-328x628.bmp,..\assets\installer\wizard-410x785.bmp
WizardSmallImageFile=..\assets\installer\small-55.bmp,..\assets\installer\small-69.bmp,..\assets\installer\small-83.bmp,..\assets\installer\small-110.bmp,..\assets\installer\small-138.bmp
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
; Start after install (interactive and silent, e.g. self-update).
; shellexec is required: the app's manifest demands admin rights, and plain
; CreateProcess from the unelevated post-install context fails with error 740.
Filename: "{app}\WinWaechter.App.exe"; Description: "WinWächter starten"; Flags: nowait postinstall skipifsilent shellexec
Filename: "{app}\WinWaechter.App.exe"; Flags: nowait skipifnotsilent shellexec

[Code]
const
  INVALID_FILE_ATTRIBUTES = -1;   // 0xFFFFFFFF as a signed 32-bit value (FILE_ATTRIBUTE_* come with Inno)

var
  KeepData: Boolean;

function GetFileAttributesW(lpFileName: String): Integer;
  external 'GetFileAttributesW@kernel32.dll stdcall';

// True for junctions and symbolic links; False when the path does not exist.
function IsReparsePoint(const Path: String): Boolean;
var
  Attr: Integer;
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

procedure WarnLink(const DataDir: String);
begin
  SuppressibleMsgBox('Der Datenordner ' + DataDir + ' ist inzwischen eine Verknüpfung (Junction oder symbolischer Link). ' +
    'Seine Rechte wurden aus Sicherheitsgründen nicht gesetzt. Bitte die Verknüpfung entfernen und das Setup erneut starten.',
    mbError, MB_OK, IDOK);
end;

// Data dir: owner Administrators; only Administrators/SYSTEM full, Users read. Root first, then
// (after a fresh link check right before it) the children drop their own rules and inherit the
// root. /L: icacls never works through a link. Owners of files are left alone on purpose: a file a
// normal user planted keeps its owner, and the app ignores journals not owned by
// Administrators/SYSTEM. The app takes over subfolder ownership itself at every start.
procedure SecureDataDir();
var
  DataDir, Icacls: String;
  Code: Integer;
begin
  DataDir := ExpandConstant('{commonappdata}\WinWaechter');
  Icacls := ExpandConstant('{sys}\icacls.exe');
  if IsReparsePoint(ExpandConstant('{commonappdata}')) or IsReparsePoint(DataDir) then
  begin
    WarnLink(DataDir);
    exit;
  end;
  Exec(Icacls, '"' + DataDir + '" /setowner *S-1-5-32-544 /L', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(Icacls, '"' + DataDir + '" /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /L',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
  // re-check right before touching the children
  if IsReparsePoint(ExpandConstant('{commonappdata}')) or IsReparsePoint(DataDir) then
  begin
    WarnLink(DataDir);
    exit;
  end;
  Exec(Icacls, '"' + DataDir + '\*" /reset /T /C /L /Q', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then SecureDataDir();
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
