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
; lzma2/max was noticeably slow to EXTRACT (1.4 GB tree, single-threaded
; decompression); /normal keeps the solid LZMA2 win with ~4x faster unpack at a
; modest +10-15% setup size.
Compression=lzma2/normal
; Inno replaces existing {app} files by renaming them first; a running tray
; manager / dsh web holds those files (v3.9.3 hard-links dsh-bundle to {app},
; so the OLD dsh shares inodes with the tree being replaced) and the rename
; fails with "尝试重命名...文件时出错". We quit the old stack in
; [Code] InitializeSetup BEFORE Inno extracts; these directives add a second
; line of defense for any other window-holding process.
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
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

[Code]
{ Gracefully stop the PREVIOUS tray manager (and the dsh backends it owns)
  BEFORE Inno extracts over the app dir. This is what makes the upgrade safe
  when the old dsh / dsh-bundle files are still open: 'exit' is a control
  action the manager forwards to its running primary instance, which then
  shuts down its services and exits. On a fresh machine there is nothing to
  stop and the action is a no-op. A short sleep lets the manager finish its
  bridge shutdown before extraction starts renaming files. }
function InitializeSetup(): Boolean;
var
  ManagerExe: String;
  ErrorCode: Integer;
begin
  Result := True;
  ManagerExe := ExpandConstant('{localappdata}\dsh-web-manager\app\dsh-web-manager.exe');
  if FileExists(ManagerExe) then
  begin
    if ShellExec('open', ManagerExe, 'exit', '', SW_HIDE, ewNoWait, ErrorCode) then
      Sleep(5000)   { give the old manager time to stop its services + exit }
    else
      { non-fatal: extraction will simply retry/fail loudly if really locked }
      Log('InitializeSetup: could not stop previous manager (code ' + IntToStr(ErrorCode) + ')');
  end;
end;
