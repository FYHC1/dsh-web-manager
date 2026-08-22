# ============================================================
#  dsh-web-manager: ensure the shared tray manager is installed
#  and create the platform-specific desktop shortcut.
#
#  The shortcut launches the SHARED tray manager (one install at
#  %LOCALAPPDATA%\dsh-web-manager\app) with the right backend:
#    (win).lnk -> dsh-web-manager.exe "open windows"
#    (wsl).lnk -> dsh-web-manager.exe "open wsl"
#  Idempotent: re-running is a no-op when everything is in place.
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File ensure-shortcut.ps1 -Backend windows
#    powershell -ExecutionPolicy Bypass -File ensure-shortcut.ps1 -Backend wsl
# ============================================================
[CmdletBinding()]
param(
    [ValidateSet('windows', 'wsl')]
    [string]$Backend = 'windows'
)
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$desktop = [Environment]::GetFolderPath('Desktop')
if (-not $desktop) { $desktop = Join-Path $env:USERPROFILE 'Desktop' }

$appRoot = Join-Path $env:LOCALAPPDATA 'dsh-web-manager\app'
$exe = Join-Path $appRoot 'dsh-web-manager.exe'
$ico = Join-Path $appRoot 'assets\dsh-webui.ico'

# 1. Ensure the shared tray manager exists. "Shared" = one install location that
#    both (win) and (wsl) shortcuts use. Only installed when absent — an existing
#    manager (possibly the running one, whose exe is locked) is reused as-is; the
#    user's config/state live under %USERPROFILE%\.dsh-webui and are never touched.
$bundled = Join-Path $PSScriptRoot '..\dist\dsh-web-manager.exe'
if (-not (Test-Path -LiteralPath $bundled -PathType Leaf)) {
    # Not running from a package checkout with a prebuilt dist: fall back to the
    # full installer, which builds/materializes the manager.
    Write-Host "[ensure-shortcut] bundled exe not found; running Install.ps1 instead"
    & (Join-Path $PSScriptRoot 'Install.ps1') -SkipLaunch
} elseif (Test-Path -LiteralPath $exe -PathType Leaf) {
    Write-Host "[ensure-shortcut] reusing shared manager: $exe"
} else {
    [System.IO.Directory]::CreateDirectory($appRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $appRoot 'assets')) | Out-Null
    try {
        Copy-Item -LiteralPath $bundled -Destination $exe -Force
        foreach ($asset in @('assets\dsh-webui.ico', 'assets\dsh-webui.svg')) {
            $src = Join-Path $PSScriptRoot "..\dist\$asset"
            if (Test-Path -LiteralPath $src -PathType Leaf) {
                Copy-Item -LiteralPath $src -Destination (Join-Path $appRoot $asset) -Force
            }
        }
        Write-Host "[ensure-shortcut] installed shared manager to $appRoot"
    }
    catch {
        Write-Host "[ensure-shortcut] install failed (may be in use): $($_.Exception.Message)"
    }
}

# 2. Create the .lnk (idempotent; replaces a stale/legacy shortcut that points
#    at the old wscript/VBS launcher instead of the tray manager).
$name = if ($Backend -eq 'wsl') { 'DeepSeek Harness WebUI (wsl).lnk' } else { 'DeepSeek Harness WebUI (win).lnk' }
$lnkPath = Join-Path $desktop $name
$ws = New-Object -ComObject WScript.Shell
if (Test-Path -LiteralPath $lnkPath -PathType Leaf) {
    $existing = $ws.CreateShortcut($lnkPath)
    if ([string]$existing.TargetPath -ieq $exe -and [string]$existing.Arguments -ieq "open $Backend") {
        Write-Host "[ensure-shortcut] shortcut already correct: $lnkPath"
        exit 0
    }
    Write-Host "[ensure-shortcut] replacing stale shortcut (old target: $($existing.TargetPath) $($existing.Arguments))"
}

$lnk = $ws.CreateShortcut($lnkPath)
$lnk.TargetPath = $exe
$lnk.Arguments = "open $Backend"
$lnk.WorkingDirectory = $appRoot
$lnk.Description = "DeepSeek Harness WebUI ($Backend dsh web)"
if (Test-Path -LiteralPath $ico -PathType Leaf) { $lnk.IconLocation = "$ico,0" }
$lnk.Save()
Write-Host "[ensure-shortcut] created $lnkPath -> `"$exe`" open $Backend"
