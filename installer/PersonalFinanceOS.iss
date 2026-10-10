; Inno Setup script for Personal Finance OS.
; Built by CI:  ISCC /DAppVersion=x.y.z /DPublishDir=<dotnet publish output> PersonalFinanceOS.iss
;
; Data-preservation rules (spec §4):
;  * Program files go to the install folder; user data lives in %LOCALAPPDATA%\PersonalFinanceOS.
;  * This installer never writes, replaces or deletes anything in the data folder.
;  * Upgrading replaces program files only (same AppId). Uninstalling keeps the data folder.

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

#define AppName "Personal Finance OS"
#define AppExe "PersonalFinanceOS.exe"

[Setup]
; Never change AppId: it is how Windows recognises upgrades of the same app.
AppId={{6B3F2B8E-4C1A-4E8B-9D51-2F7A0C9E1D44}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#NumericVersion}
AppPublisher=Benjamin Smet
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install: no administrator rights needed, every Windows user installs and keeps their own data.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=PersonalFinanceOS-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

[Languages]
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
dutch.DataKept=Je financiële gegevens zijn NIET verwijderd. Ze staan nog in:%n%n%1%n%nInstalleer je de app opnieuw, dan vind je alles terug. Wil je ze echt wissen, verwijder dan zelf die map.
english.DataKept=Your financial data has NOT been deleted. It is still in:%n%n%1%n%nIf you reinstall the app, everything will be there. To really erase it, delete that folder yourself.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\PersonalFinanceOS');
    if DirExists(DataDir) and not UninstallSilent then
      MsgBox(FmtMessage(CustomMessage('DataKept'), [DataDir]), mbInformation, MB_OK);
  end;
end;
