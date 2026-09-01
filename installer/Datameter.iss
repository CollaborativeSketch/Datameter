; Inno Setup script for Datameter.
;
; One installer is produced per architecture, so a download is ~60 MB rather than ~160 MB —
; which matters for an app whose whole subject is how much data you are getting through.
;
; Build (see installer\build.ps1, which does all three):
;   dotnet publish src\Datameter.App\Datameter.App.csproj -c Release -r win-x64
;   iscc /DArch=x64 /DPublishDir="<full path to that publish folder>" installer\Datameter.iss

#define AppName        "Datameter"
#define AppVersion     "1.0.0"
#define AppPublisher   "Alexander Akinbiyi"
#define AppUrl         "https://github.com/CollaborativeSketch/Datameter"
#define AppExeName     "Datameter.exe"

#ifndef PublishDir
  #error PublishDir is not defined. Pass /DPublishDir="...\publish" to iscc.
#endif
#ifndef Arch
  #error Arch is not defined. Pass /DArch=x64, x86 or arm64 to iscc.
#endif
#ifndef DistDir
  #error DistDir is not defined. Pass /DDistDir="...\dist" to iscc.
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
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright=Copyright (C) 2026 {#AppPublisher}
SetupIconFile=..\assets\datameter.ico

; Per-user install by default, so no UAC prompt and no admin rights needed. A user with
; admin rights can still choose an all-users install from the first page.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; Windows App SDK and .NET 9 both require Windows 10 1809 (build 17763) or later; the usage
; API this app is built on does not exist before it either.
MinVersion=10.0.17763

#if Arch == "x64"
  ArchitecturesAllowed=x64compatible and not arm64
  ArchitecturesInstallIn64BitMode=x64compatible
#elif Arch == "arm64"
  ArchitecturesAllowed=arm64
  ArchitecturesInstallIn64BitMode=arm64
#else
  ; The x86 build runs everywhere, including under emulation on x64 and ARM64.
#endif

OutputDir={#DistDir}
OutputBaseFilename=DatameterSetup-{#AppVersion}-{#Arch}
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
