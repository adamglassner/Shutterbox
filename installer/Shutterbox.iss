; Inno Setup script for Shutterbox (Inno Setup 6.3 or newer)
; Build: run ..\build.ps1, or compile this file with ISCC.exe after publishing the exe.
;
; Installs to Program Files for all users by default (asks for administrator permission).
; The first page also offers "Install for me only", which needs no administrator rights.
;
; The Bytehawk Studio Launcher can run this setup silently:
;   Shutterbox-<version>-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOICONS /DIR="<folder>"

#define MyAppName "Shutterbox"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppPublisher "Bytehawk Studio"
#define MyAppExeName "Shutterbox.exe"
#define MyAppUrl "https://github.com/adamglassner/shutterbox"
#ifndef PublishDir
  #define PublishDir "..\dist\publish"
#endif

[Setup]
AppId={{4B7D2E19-8C3A-4F61-A5E2-7D9C1B3F6A84}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
AppCopyright=© 2026 {#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Shutterbox-{#MyAppVersion}-Setup
SetupIconFile=..\assets\shutterbox.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Ask to close Shutterbox if it's running (Windows Restart Manager)
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel1=Welcome to the [name/ver] Setup Wizard
FinishedHeadingLabel=[name/ver] is installed

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; The icon is embedded in Shutterbox.exe. /NOICONS (used by the Bytehawk Studio Launcher) skips the Start menu entry.
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "Photo organizer"; Check: not NoIconsRequested
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "Photo organizer"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

; No [UninstallDelete]: uninstalling removes the program and shortcuts.
; Your settings, library, albums and tags (%APPDATA%\Shutterbox) and your photos are left alone.

[Code]
{ /NOICONS: the launcher installs Shutterbox without Start menu entries. Inno Setup only honours it
  on the Start Menu folder page, which Shutterbox doesn't show, so check the command line directly. }
function NoIconsRequested: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/NOICONS') = 0 then
      Result := True;
end;
