#ifndef MyAppVersion
  #define MyAppVersion GetEnv("NAMA_VERSION")
#endif

#if MyAppVersion == ""
  #error NAMA_VERSION must be set before compiling the installer.
#endif

#define MyAppName "Nama"
#define MyAppPublisher "Nama"
#define MyAppURL "https://github.com/YusBera/Nama"
#define MyAppExeName "Nama.exe"

[Setup]
AppId={{C4E98868-9325-4626-9812-8836D74E5BF0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=Nama-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "explorer"; Description: "Add Nama to the Explorer right-click menu"; GroupDescription: "Windows integration:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Nama"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\Nama"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--install-context-menu"; StatusMsg: "Adding Nama to Explorer..."; Flags: runhidden waituntilterminated; Tasks: explorer
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Nama"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--uninstall-context-menu"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveNamaExplorerEntries"
