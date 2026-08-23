# Installs dsh web manager on Windows: copies the app layout, creates the desktop
# shortcut, initializes the shared config (visible from WSL) and starts the tray.
#
#   powershell -ExecutionPolicy Bypass -File scripts\Install.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\Install.ps1 -SkipLaunch
[CmdletBinding()]
param(
    [string]$SourceDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    [switch]$SkipLaunch,
    [switch]$NoShortcut,
    [switch]$StopLegacyWatcher = $false
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $SourceDir 'dsh-web-manager.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Build output not found: $exe (run scripts\Build.ps1 first)."
}

# 1. Install application files.
# (Explicit enumeration: Copy-Item -LiteralPath '<dir>\*' is a silent no-op on
# PS 5.1 — LiteralPath does not expand wildcards and does not error either.)
$installRoot = Join-Path $env:LOCALAPPDATA 'dsh-web-manager\app'
[System.IO.Directory]::CreateDirectory($installRoot) | Out-Null
Get-ChildItem -LiteralPath $SourceDir -Force | Copy-Item -Destination $installRoot -Recurse -Force
Write-Host "Installed app files to $installRoot"

# 2. Shared config (visible from WSL as /mnt/c/Users/<user>/.dsh-webui/).
$sharedDir = Join-Path $env:USERPROFILE '.dsh-webui'
[System.IO.Directory]::CreateDirectory($sharedDir) | Out-Null
$configFile = Join-Path $sharedDir 'config.json'
if (-not (Test-Path -LiteralPath $configFile -PathType Leaf)) {
    Copy-Item -LiteralPath (Join-Path $SourceDir 'config.example.json') -Destination $configFile -Force
    Write-Host "Initialized shared config at $configFile"
}

# 3. Official icon: keep an existing one, else copy the bundled multi-size icon.
$iconDest = Join-Path $sharedDir 'dsh-webui.ico'
if (-not (Test-Path -LiteralPath $iconDest -PathType Leaf)) {
    $bundled = Join-Path $SourceDir 'assets\dsh-webui.ico'
    if (Test-Path -LiteralPath $bundled -PathType Leaf) {
        Copy-Item -LiteralPath $bundled -Destination $iconDest -Force
    }
}

# 4. v2.1 mutual bootstrap: keep a self-contained Windows-side installer copy in the
#    shared dir (WSL can reach it as /mnt/c/Users/<user>/.dsh-webui/wsl-bootstrap/)
#    so the WSL-side wsl-bootstrap.sh can reinstall the manager if it goes missing.
$bootstrapDir = Join-Path $sharedDir 'wsl-bootstrap'
[System.IO.Directory]::CreateDirectory($bootstrapDir) | Out-Null
Get-ChildItem -LiteralPath $SourceDir -Force | Copy-Item -Destination $bootstrapDir -Recurse -Force
Write-Host "Mirrored installer to $bootstrapDir (for WSL-side bootstrap)"

# 5. v2.1 WSL companion: materialize wsl-start.sh / wsl-bootstrap.sh into the default
#    WSL distro home (best effort; the manager re-materializes on demand).
$wslScripts = Join-Path $SourceDir 'wsl'
if (Test-Path -LiteralPath $wslScripts) {
    try {
        foreach ($scriptName in @('wsl-start.sh', 'wsl-bootstrap.sh')) {
            $src = Join-Path $wslScripts $scriptName
            $sharedCopy = Join-Path $sharedDir $scriptName
            if (Test-Path -LiteralPath $src -PathType Leaf) {
                $content = (Get-Content -LiteralPath $src -Raw) -replace "`r`n", "`n"
                [System.IO.File]::WriteAllText($sharedCopy, $content, (New-Object System.Text.UTF8Encoding($false)))
            }
        }
        & wsl.exe -- bash -lc "mkdir -p ~/.dsh-webui && cp -f /mnt/c/Users/$env:USERNAME/.dsh-webui/wsl-start.sh ~/.dsh-webui/wsl-start.sh && cp -f /mnt/c/Users/$env:USERNAME/.dsh-webui/wsl-bootstrap.sh ~/.dsh-webui/wsl-bootstrap.sh && chmod +x ~/.dsh-webui/wsl-start.sh ~/.dsh-webui/wsl-bootstrap.sh" 2>$null
        Write-Host 'Materialized WSL companion scripts (default distro).'
    }
    catch {
        Write-Host "WSL companion materialization skipped: $($_.Exception.Message)"
    }
}

# 6. Stop the legacy v1.x window watcher (dsh-ui-winsize.ps1) if requested.
if ($StopLegacyWatcher) {
    $legacy = Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" |
        Where-Object { $_.CommandLine -like '*dsh-ui-winsize.ps1*' }
    if ($legacy) {
        $legacy | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        Write-Host "Stopped legacy watcher process(es)."
    }
}

# 7. Shortcuts.
if (-not $NoShortcut) {
    $target = Join-Path $installRoot 'dsh-web-manager.exe'
    $iconPath = Join-Path $installRoot 'assets\dsh-webui.ico'
    $shell = New-Object -ComObject WScript.Shell

    $desktop = [Environment]::GetFolderPath('Desktop')
    $lnkDir = $desktop
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    $shortcutName = 'DeepSeek Harness WebUI (manager).lnk'

    foreach ($dir in @($lnkDir, $startMenu)) {
        $lnk = $shell.CreateShortcut((Join-Path $dir $shortcutName))
        $lnk.TargetPath = $target
        $lnk.Arguments = 'open'
        $lnk.WorkingDirectory = (Split-Path -Parent $target)
        $lnk.IconLocation = "$iconPath,0"
        $lnk.Description = 'dsh web manager - DeepSeek Harness WebUI'
        $lnk.Save()
    }
    Write-Host "Created shortcuts: desktop + start menu"
}

# 8. Start the manager (unless skipped).
if (-not $SkipLaunch) {
    $installedExe = Join-Path $installRoot 'dsh-web-manager.exe'
    Start-Process -FilePath $installedExe -ArgumentList 'open' -WindowStyle Hidden
    Write-Host "Started dsh web manager."
}

Write-Host 'Install finished.'