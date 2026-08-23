# ============================================================
#  dsh-offline-bundle uninstaller. Reverses Install-Offline.ps1:
#  removes the portable tree, the user PATH entry, the autostart
#  value, and (unless -KeepManager) the tray manager itself.
#
#    powershell -ExecutionPolicy Bypass -File Uninstall-Offline.ps1
#    powershell -ExecutionPolicy Bypass -File Uninstall-Offline.ps1 -PurgeProfile
#
#  -PurgeProfile also deletes %USERPROFILE%\.dsh (dsh settings,
#  profiles and credentials — API keys). Default keeps it.
# ============================================================
[CmdletBinding()]
param(
    [string]$TargetRoot = (Join-Path $env:LOCALAPPDATA 'dsh-bundle'),
    [switch]$KeepManager,   # keep the shared tray manager (e.g. an online install also uses it)
    [switch]$PurgeProfile,  # also remove %USERPROFILE%\.dsh
    [string]$PurgeWsl = ''  # also remove the WSL side in this distro (~/.dsh-bundle, shim; WSL ~/.dsh kept)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sandboxHome = $env:DSH_WEB_MANAGER_HOME

# 1. Stop the tray manager (gracefully stops the managed dsh service).
$managerExe = if ($sandboxHome) {
    Join-Path $sandboxHome 'AppData\Local\dsh-web-manager\app\dsh-web-manager.exe'
} else {
    Join-Path $env:LOCALAPPDATA 'dsh-web-manager\app\dsh-web-manager.exe'
}
if (Test-Path -LiteralPath $managerExe -PathType Leaf) {
    if ($KeepManager) {
        Write-Host '[uninstall] -KeepManager: leaving the tray manager running/installed'
    } else {
        Start-Process -FilePath $managerExe -ArgumentList 'exit' -WindowStyle Hidden
        Start-Sleep -Seconds 2
    }
}

# 2. Remove the manager (files, shortcuts, autostart) via its own uninstaller.
if (-not $KeepManager -and -not $sandboxHome) {
    $managerUninstall = Join-Path $env:LOCALAPPDATA 'dsh-web-manager\app\Uninstall.ps1'
    if (Test-Path -LiteralPath $managerUninstall -PathType Leaf) {
        & $managerUninstall
    } else {
        Write-Warning "[uninstall] manager uninstaller not found: $managerUninstall"
    }
}

# 3. User PATH: drop the bundle bin dir (type-preserving). Skipped in sandbox
#    mode so a test run never rewrites the real HKCU environment.
$binDir = Join-Path $TargetRoot 'bin'
if (-not $sandboxHome) {
    $envKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    if ($envKey) {
        $kind = $envKey.GetValueKind('Path')
        $cur = [string]$envKey.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $parts = @($cur -split ';' | Where-Object { $_ -ne '' -and $_ -ne $binDir })
        $envKey.SetValue('Path', $parts -join ';', $kind)
        $envKey.Close()
        Write-Host '[uninstall] user PATH entry removed'
    }
}

# 4. Config: clear DshCommand if it pointed into the bundle (leaves the rest).
$sharedDir = if ($sandboxHome) { Join-Path $sandboxHome '.dsh-webui' } else { Join-Path $env:USERPROFILE '.dsh-webui' }
$configFile = Join-Path $sharedDir 'config.json'
if (Test-Path -LiteralPath $configFile -PathType Leaf) {
    try {
        $config = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
        if ($config.PSObject.Properties['DshCommand'] -and ([string]$config.DshCommand) -like '*\dsh-bundle\*') {
            $config.DshCommand = ''
            $config | ConvertTo-Json -Depth 8 | ForEach-Object { [System.IO.File]::WriteAllText($configFile, $_, (New-Object System.Text.UTF8Encoding($false))) }
            Write-Host '[uninstall] config DshCommand cleared'
        }
    } catch { Write-Warning "[uninstall] could not update config: $($_.Exception.Message)" }
}

# 5. The portable tree itself.
if (Test-Path -LiteralPath $TargetRoot) {
    Remove-Item -LiteralPath $TargetRoot -Recurse -Force
    Write-Host "[uninstall] removed $TargetRoot"
} else {
    Write-Host "[uninstall] nothing at $TargetRoot"
}

# 6. Optional profile purge (credentials live here!). Never follows into the
#    real %USERPROFILE% while a test sandbox home is active.
if ($PurgeProfile) {
    $dshHome = if ($sandboxHome) { Join-Path $sandboxHome '.dsh' } else { Join-Path $env:USERPROFILE '.dsh' }
    if (Test-Path -LiteralPath $dshHome) {
        Remove-Item -LiteralPath $dshHome -Recurse -Force
        Write-Host "[uninstall] purged $dshHome (profiles + credentials)"
    }
} else {
    Write-Host '[uninstall] ~/.dsh kept (use -PurgeProfile to delete profiles and credentials)'
}

# 7. Optional WSL-side removal (real distros only; ~/.dsh inside WSL is kept).
if ($PurgeWsl) {
    $wslExe = Join-Path $env:SystemRoot 'System32\wsl.exe'
    if (-not $sandboxHome -and (Test-Path -LiteralPath $wslExe -PathType Leaf)) {
        $inner = 'rm -rf "$HOME/.dsh-bundle" "$HOME/.local/bin/dsh"; ' +
                 'if [ -e "$HOME/.local/bin/dsh.pre-bundle.bak" ]; then mv "$HOME/.local/bin/dsh.pre-bundle.bak" "$HOME/.local/bin/dsh"; fi'
        & $wslExe -d $PurgeWsl -- bash -c $inner
        Write-Host "[uninstall] WSL side removed in '$PurgeWsl' (WSL ~/.dsh kept)"
    }
}

Write-Host 'Offline uninstall finished.'
