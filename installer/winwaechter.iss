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
RestartApplications=yes
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
; Data dir: owner Administrators, only Administrators/SYSTEM full, Users read
Filename: "{sys}\icacls.exe"; Parameters: """{commonappdata}\WinWaechter"" /setowner *S-1-5-32-544"; Flags: runhidden waituntilterminated
Filename: "{sys}\icacls.exe"; Parameters: """{commonappdata}\WinWaechter"" /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX"; Flags: runhidden waituntilterminated
; Start after install (interactive and silent, e.g. self-update)
Filename: "{app}\WinWaechter.App.exe"; Description: "WinWächter starten"; Flags: nowait postinstall skipifsilent
Filename: "{app}\WinWaechter.App.exe"; Flags: nowait skipifnotsilent

[Code]
var
  KeepData: Boolean;

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
  if (CurUninstallStep = usPostUninstall) and (not KeepData) then
    DelTree(ExpandConstant('{commonappdata}\WinWaechter'), True, True, True);
end;
