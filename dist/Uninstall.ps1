# Removes dsh web manager: exits the tray, removes shortcuts and the autostart
# registry value. User configuration is kept unless -PurgeData is given.
#
#   powershell -ExecutionPolicy Bypass -File scripts\Uninstall.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\Uninstall.ps1 -PurgeData
[CmdBinding()]
param([switch]$PurgeData)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installRoot = Join-Path $env:LOCALAPPDATA 'dsh-web-manager'
$exe = Join-Path $installRoot 'app\dsh-web-manager.exe'

# 1. Ask the running manager to exit (it stops the managed dsh service).
if (Test-Path -LiteralPath $exe -PathType Leaf) {
    Start-Process -FilePath $exe -ArgumentList 'exit' -WindowStyle Hidden
    Start-Sleep -Seconds 2
}

# 2. Remove autostart registry value.
try {
    $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(
        'Software\Microsoft\Windows\CurrentVersion\Run', $true)
    if ($runKey) {
        $runKey.DeleteValue('dsh-web-manager', $false)
        $runKey.Close()
    }
} catch { }

# 3. Remove shortcuts (shared tray, Windows backend, WSL backend).
$shortcutNames = @(
    'DeepSeek Harness WebUI (manager).lnk',
    'DeepSeek Harness WebUI (win).lnk',
    'DeepSeek Harness WebUI (wsl).lnk'
)
$desktop = [Environment]::GetFolderPath('Desktop')
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
foreach ($dir in @($desktop, $startMenu)) {
    foreach ($lnk in $shortcutNames) {
        $p = Join-Path $dir $lnk
        if (Test-Path -LiteralPath $p -PathType Leaf) { Remove-Item -LiteralPath $p -Force }
    }
}

# 4. Optional data purge; otherwise keep shared config, logs and the icon.
if ($PurgeData) {
    if (Test-Path -LiteralPath $installRoot) { Remove-Item -LiteralPath $installRoot -Recurse -Force }
    $shared = Join-Path $env:USERPROFILE '.dsh-webui'
    foreach ($f in @('config.json', 'window-size', 'dsh-webui.ico')) {
        $p = Join-Path $shared $f
        if (Test-Path -LiteralPath $p -PathType Leaf) { Remove-Item -LiteralPath $p -Force }
    }
}

Write-Host 'Uninstall finished.'