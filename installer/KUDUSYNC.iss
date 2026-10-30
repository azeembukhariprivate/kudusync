; KUDUSYNC installer (Inno Setup 6)
; Installs for the current Windows user only, so installing never asks for administrator rights
; (and syncing never needs them either).

#ifndef MyAppVersion
  #define MyAppVersion "1.1.2"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\KUDUSYNC"
#endif

#define MyAppName "KUDUSYNC"
#define MyAppExe "KUDUSYNC.exe"
#define MyAppUrl "https://github.com/azeembukhariprivate/kudusync"

[Setup]
AppId={{9E2F6B41-37C8-4A0D-B5E6-1C84D27F3A95}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Azeem Bukhari
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=KUDUSYNC-{#MyAppVersion}-setup
SetupIconFile=..\src\KUDUSYNC\Assets\KUDUSYNC.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany=Azeem Bukhari
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "contextmenu"; Description: "Add ""Sync with KUDUSYNC"" and ""Back up this folder"" to folders' right-click menu"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "self-test.log,KUDUSYNC.portable"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Parameters: "--register-shell"; Tasks: contextmenu; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExe}"; Description: "Open KUDUSYNC now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Removes the right-click menu, the start-up entry and the scheduled tasks, and closes the tray part.
Filename: "{app}\{#MyAppExe}"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "KUDUSYNCUnregister"

[Registry]
; Remove saved settings when uninstalling. (Pairs, records and logs in %LOCALAPPDATA%\KUDUSYNC are kept, in case of a reinstall.)
Root: HKCU; Subkey: "Software\KUDUSYNC"; Flags: uninsdeletekey dontcreatekey
