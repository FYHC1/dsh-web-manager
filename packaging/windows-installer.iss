; ============================================================
;  dsh-offline-bundle Windows installer (Inno Setup).
;  Bundles the whole offline bundle (node + dsh + profiles +
;  tray manager + optional WSL payload) into one setup EXE and
;  runs Install-Offline.ps1 after extraction.
;
;  Build (CI or locally, ISCC from https://jrsoftware.org/isinfo.php):
;    ISCC /DBundleDir=..\bundle-out\dsh-offline-bundle /DMyVersion=3.8.0 ^
;         /O..\bundle-out packaging\windows-installer.iss
; ============================================================

#ifndef BundleDir
#define BundleDir "..\bundle-out\dsh-offline-bundle"
#endif
#ifndef MyVersion
#define MyVersion "0.0.0"
#endif
#ifndef PayloadTag
#define PayloadTag "local"
#endif

[Setup]
AppId={{7C1A5B92-6E2B-4B0F-9E44-D55AF2B2D201}
AppName=dsh offline bundle (dsh + dsh web manager)
AppVersion={#MyVersion}
AppPublisher=dsh-web-manager contributors
AppPublisherURL=https://github.com/FYHC1/dsh-web-manager
DefaultDirName={localappdata}\dsh-offline-bundle
DefaultGroupName=dsh offline bundle
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
; OutputDir is resolved against the ISCC CURRENT DIRECTORY (not the .iss file),
; so pin it to the .iss location — otherwise a run from the repo root writes
; outside the repo and dies with "cannot find the path".
OutputDir={#SourcePath}..\bundle-out
OutputBaseFilename=dsh-offline-bundle-setup_{#MyVersion}_x64_{#PayloadTag}
UninstallDisplayName=dsh offline bundle {#MyVersion}
Uninstallable=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "innosetup\ChineseSimplified.isl"

[Messages]
zh.WelcomeLabel2=这将把离线一体化包（便携 Node + dsh + 预烘焙 profile + dsh web manager 托盘）安装到您的电脑。%n%n继续之前请关闭其他应用程序。

[Files]
Source: "{#BundleDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs

[Run]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Install-Offline.ps1"" -BundleDir ""{app}"" -WithWsl"; \
  WorkingDir: "{app}"; \
  Description: "{cm:LaunchProgram,dsh offline bundle}（安装到本机并启动托盘管理器）"; \
  Flags: postinstall skipifsilent runasoriginaluser unchecked

[UninstallRun]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Uninstall-Offline.ps1"""; \
  RunOnceId: "UninstallOffline"; Flags: runhidden

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
