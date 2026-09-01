; Inno Setup script for Datameter.
;
; Build with:
;   dotnet publish src/Datameter.App/Datameter.App.csproj -c Release -r win-x64
;   iscc /DPublishDir="<full path to the publish folder>" installer\Datameter.iss
;
; The app is published self-contained — it carries .NET and the Windows App SDK — so this
; installs without any prerequisite download and without needing the Windows App Runtime
; present on the target machine.

#define AppName        "Datameter"
#define AppVersion     "1.0.0"
#define AppPublisher   "Alexander Akinbiyi"
#define AppUrl         "https://github.com/CollaborativeSketch/Datameter"
#define AppExeName     "Datameter.exe"

#ifndef PublishDir
  #error PublishDir is not defined. Pass /DPublishDir="...\publish" to iscc.
#endif

[Setup]
; Never change AppId: it is how Windows recognises an existing install to upgrade.
AppId={{8F3A1C42-6B7E-4D59-9E2A-1D0F5B7C4A31}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
SetupIconFile=..\assets\datameter.ico

; Per-user install by default, so no UAC prompt and no admin rights needed. A user with
; admin rights can still choose an all-users install from the first page.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; Datameter only reads usage the current user can already see, so it never needs elevation.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

OutputDir={#PublishDir}\..\installer
OutputBaseFilename=DatameterSetup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "startup";     Description: "Start {#AppName} when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";        Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}";  Filename: "{app}\{#AppExeName}"; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Open {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The usage database and settings live outside {app}; leave them, so reinstalling keeps the
; history Datameter has accumulated beyond what Windows itself retains.
Type: dirifempty; Name: "{app}"
