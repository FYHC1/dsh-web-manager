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

# Dotted version comparison for FileVersion strings ("3.5.0.0"); missing segments
# read as 0. Returns 1 / 0 / -1.
function Compare-Version([string]$a, [string]$b) {
    $pa = @($a -split '\.' | ForEach-Object { $n = 0; [void][int]::TryParse($_, [ref]$n); $n })
    $pb = @($b -split '\.' | ForEach-Object { $n = 0; [void][int]::TryParse($_, [ref]$n); $n })
    $count = [Math]::Max($pa.Count, $pb.Count)
    for ($i = 0; $i -lt $count; $i++) {
        $x = if ($i -lt $pa.Count) { $pa[$i] } else { 0 }
        $y = if ($i -lt $pb.Count) { $pb[$i] } else { 0 }
        if ($x -ne $y) { return if ($x -gt $y) { 1 } else { -1 } }
    }
    return 0
}

$desktop = [Environment]::GetFolderPath('Desktop')
if (-not $desktop) { $desktop = Join-Path $env:USERPROFILE 'Desktop' }

$appRoot = Join-Path $env:LOCALAPPDATA 'dsh-web-manager\app'
$exe = Join-Path $appRoot 'dsh-web-manager.exe'
$ico = Join-Path $appRoot 'assets\dsh-webui.ico'

# 1. Ensure the shared tray manager exists. "Shared" = one install location that
#    both (win) and (wsl) shortcuts use. Only installed when absent — an existing
#    manager (possibly the running one, whose exe is locked) is reused as-is; the
#    user's config/state live under %USERPROFILE%\.dsh-webui and are never touched.
#    IMPORTANT: never DOWNGRADE the shared install with a stale bundled exe. The
#    bundle inside an old plugin install (a profile copy from a previous dsh
#    version) can lag the deployed manager, and restoring it silently regresses
#    the tray (lost features/fields, e.g. StopAttached). Copy only when absent,
#    or when the bundled exe is strictly newer.
$bundled = Join-Path $PSScriptRoot '..\dist\dsh-web-manager.exe'
if (-not (Test-Path -LiteralPath $bundled -PathType Leaf)) {
    # Not running from a package checkout with a prebuilt dist: fall back to the
    # full installer, which builds/materializes the manager.
    Write-Host "[ensure-shortcut] bundled exe not found; running Install.ps1 instead"
    & (Join-Path $PSScriptRoot 'Install.ps1') -SkipLaunch
} elseif (Test-Path -LiteralPath $exe -PathType Leaf) {
    $installedVer = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
    $bundledVer = (Get-Item -LiteralPath $bundled).VersionInfo.FileVersion
    if (-not $installedVer -or -not $bundledVer -or (Compare-Version $installedVer $bundledVer) -ge 0) {
        Write-Host "[ensure-shortcut] reusing shared manager: $exe (v$installedVer)"
    } else {
        Write-Host "[ensure-shortcut] upgrading shared manager v$installedVer -> v$bundledVer"
        try {
            Copy-Item -LiteralPath $bundled -Destination $exe -Force
        } catch {
            Write-Host "[ensure-shortcut] upgrade failed (manager in use?): $($_.Exception.Message)"
        }
    }
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
        # v3.8: WebView2 runtime files (managed assemblies + native loader) must
        # follow the exe, otherwise the embedded window backend cannot start.
        foreach ($rel in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll')) {
            $src = Join-Path $PSScriptRoot "..\dist\$rel"
            if (Test-Path -LiteralPath $src -PathType Leaf) {
                Copy-Item -LiteralPath $src -Destination (Join-Path $appRoot $rel) -Force
            }
        }
        foreach ($arch in @('x64', 'x86')) {
            $srcDir = Join-Path $PSScriptRoot "..\dist\$arch"
            if (Test-Path -LiteralPath $srcDir -PathType Directory) {
                [System.IO.Directory]::CreateDirectory((Join-Path $appRoot $arch)) | Out-Null
                Get-ChildItem -LiteralPath $srcDir -File | Copy-Item -Destination (Join-Path $appRoot $arch) -Force
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
