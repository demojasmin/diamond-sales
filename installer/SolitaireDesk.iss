; Solitaire Desk — Windows installer (Inno Setup 6)
;
; Build with:
;     dotnet publish DiamondDesktop\DiamondDesktop.csproj -c Release -o publish\DiamondDesktop
;     "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\SolitaireDesk.iss
;
; winget installs Inno Setup per-user, so ISCC lands under LOCALAPPDATA rather than Program Files.
; A machine-wide install puts it in "C:\Program Files (x86)\Inno Setup 6\".
;
; Ships a self-contained build: the .NET 10 Desktop Runtime is inside it, so a desk with no runtime
; installed still runs the app. That is the reason for the ~150 MB — a framework-dependent build is
; a tenth of the size and turns every install into a support call about a missing runtime.
;
; Version comes off the built executable rather than being repeated here. One place to bump for a
; release (DiamondDesktop.csproj <Version>), and the installer cannot drift from the binary.

#define Source   "..\publish\DiamondDesktop"
#define ExeName  "DiamondDesktop.exe"
#define AppName  "Solitaire Desk"
#define AppVer   GetVersionNumbersString(Source + "\" + ExeName)

[Setup]
AppId={{8F3C1B57-2E44-4C6A-9E71-5A0D2C8B4F19}
AppName={#AppName}
AppVersion={#AppVer}
AppVerName={#AppName} {#AppVer}
AppPublisher=Solitaire
VersionInfoVersion={#AppVer}
VersionInfoDescription=Diamond Sales & Inventory

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#ExeName}

; Per-machine when run elevated, per-user when not. A trading desk is often a standard account,
; and refusing to install without an admin password is how the app never gets installed.
PrivilegesRequiredOverridesAllowed=dialog

OutputDir=..\dist
OutputBaseFilename=SolitaireDesk-Setup-{#AppVer}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; A running copy holds DiamondDesktop.exe open. Without this the installer copies over a locked
; file, fails halfway, and leaves a half-written install behind.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
; Everything the publish produced, minus the two things that must not ship:
;   *.pdb — debug symbols, which map the binary back to source
;   *.xml — API doc files from packages, of no use on a desk
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; \
    Excludes: "*.pdb,*.xml"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#ExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ExeName}"; Description: "Start {#AppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; appsettings.json is PreserveNewest, so an edited config survives an upgrade — but it is ours and
; should go when the app does. Anything the user saved elsewhere is untouched.
Type: files; Name: "{app}\appsettings.json"
Type: dirifempty; Name: "{app}"
