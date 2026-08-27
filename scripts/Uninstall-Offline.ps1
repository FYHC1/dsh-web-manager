# ============================================================
#  dsh-offline-bundle uninstaller. Reverses Install-Offline.ps1:
#  removes the portable tree, the user PATH entry, the autostart
#  value and (only when BOTH sides are gone) the tray manager.
#
#  The tray manager is SHARED by the Windows side and the WSL side
#  (one install at %LOCALAPPDATA%\dsh-web-manager\app, a single tray
#  managing both backends). Uninstalling ONE side must not tear it
#  down — it is removed only once BOTH sides confirm the bundle is
#  gone:
#    winGone = %LOCALAPPDATA%\dsh-bundle no longer exists
#    wslGone = no (remaining) installed WSL distro still has the
#              bundle (~/.dsh-bundle, /opt/dsh-bundle-wsl, the
#              ~/.local/bin/dsh shim)
#  -KeepManager forces keeping it regardless of the above. When the
#  manager is kept, only the WINDOWS-side dsh instance is stopped
#  (via the manager's control pipe) so the tree deletes cleanly;
#  the manager itself and the WSL side stay running.
#
#    powershell -ExecutionPolicy Bypass -File Uninstall-Offline.ps1
#    powershell -ExecutionPolicy Bypass -File Uninstall-Offline.ps1 -PurgeProfile
#    powershell -ExecutionPolicy Bypass -File Uninstall-Offline.ps1 -PurgeWsl Ubuntu
#
#  -PurgeProfile also deletes %USERPROFILE%\.dsh (dsh settings,
#  profiles and credentials — API keys). Default keeps it.
#  -PurgeWsl <distro> also removes that distro's WSL-side bundle
#  (~/.dsh-bundle, shim; WSL ~/.dsh kept).
# ============================================================
[CmdletBinding()]
param(
    [string]$TargetRoot = (Join-Path $env:LOCALAPPDATA 'dsh-bundle'),
    [switch]$KeepManager,   # keep the shared tray manager even when both sides are gone
    [switch]$PurgeProfile,  # also remove %USERPROFILE%\.dsh
    [string]$PurgeWsl = ''  # also remove the WSL side in this distro (~/.dsh-bundle, shim; WSL ~/.dsh kept)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sandboxHome = $env:DSH_WEB_MANAGER_HOME
$managerExe = if ($sandboxHome) {
    Join-Path $sandboxHome 'AppData\Local\dsh-web-manager\app\dsh-web-manager.exe'
} else {
    Join-Path $env:LOCALAPPDATA 'dsh-web-manager\app\dsh-web-manager.exe'
}
$wslExe = Join-Path $env:SystemRoot 'System32\wsl.exe'

# ---------------------------------------------------------------- helpers

# Installed WSL distro names. wsl -l -q emits UTF-16LE, so the output must be
# captured with the Unicode encoding; strips the "*" default-distro marker.
function Get-WslDistros {
    if (-not (Test-Path -LiteralPath $wslExe -PathType Leaf)) { return @() }
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $wslExe
        $psi.Arguments = '-l -q'
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.StandardOutputEncoding = [System.Text.Encoding]::Unicode
        $p = [System.Diagnostics.Process]::Start($psi)
        $out = $p.StandardOutput.ReadToEnd()
        $p.WaitForExit()
        return @($out -split "`r?`n" |
            Where-Object { $_.Trim() } |
            ForEach-Object { ($_.Trim() -replace '^\*?\s*', '') } |
            Where-Object { $_ })
    } catch {
        Write-Warning "[uninstall] distro list failed: $($_.Exception.Message)"
        return @()
    }
}

# True when ANY installed WSL distro still carries the offline bundle
# (~/.dsh-bundle tree, /opt/dsh-bundle-wsl install, or the ~/.local/bin/dsh
# shim created by install-wsl.sh). The shared tray manager stays alive while
# this is true. Test hook: DSH_WEB_MANAGER_TEST_WSL_BUNDLE=yes|no overrides
# the probe for sandbox tests.
function Test-AnyWslBundleRemains {
    $override = $env:DSH_WEB_MANAGER_TEST_WSL_BUNDLE
    if ($override) {
        return $override.Trim().ToLowerInvariant() -eq 'yes'
    }
    if (-not (Test-Path -LiteralPath $wslExe -PathType Leaf)) { return $false }
    foreach ($d in Get-WslDistros) {
        try {
            $out = & $wslExe -d $d -- bash -lc 'test -e "$HOME/.dsh-bundle" -o -e /opt/dsh-bundle-wsl -o -e "$HOME/.local/bin/dsh" && printf YES' 2>$null
            if ($out -match 'YES') { return $true }
        } catch { }
    }
    return $false
}

# ---------------------------------------------------------------- steps

# 0. Optional WSL-side removal FIRST, so the remaining-bundle probe below sees
#    the result. Sandbox mode skips every real-WSL mutation.
if ($PurgeWsl) {
    if (-not $sandboxHome -and (Test-Path -LiteralPath $wslExe -PathType Leaf)) {
        $inner = 'rm -rf "$HOME/.dsh-bundle" "$HOME/.local/bin/dsh"; ' +
                 'if [ -e "$HOME/.local/bin/dsh.pre-bundle.bak" ]; then mv "$HOME/.local/bin/dsh.pre-bundle.bak" "$HOME/.local/bin/dsh"; fi'
        & $wslExe -d $PurgeWsl -- bash -c $inner
        Write-Host "[uninstall] WSL side removed in '$PurgeWsl' (WSL ~/.dsh kept)"
    }
}

# Shared tray manager decision: it serves BOTH backends, so it is removed only
# after BOTH sides confirm the bundle is gone. Otherwise it stays, continuing
# to serve the side that still uses it.
$wslGone = -not (Test-AnyWslBundleRemains)
$removeManager = (-not $KeepManager) -and $wslGone
if ($KeepManager) {
    Write-Host '[uninstall] -KeepManager: sharing tray manager kept by flag'
} elseif (-not $wslGone) {
    Write-Host '[uninstall] shared tray manager KEPT (a WSL side still has the bundle)'
}

# 1. Free the Windows tree so it can be deleted:
#    - removing the manager   -> full graceful exit (stops all managed dsh)
#    - keeping it             -> stop only the WINDOWS-side instance through the
#                                control pipe; the manager + WSL side keep running
if ($removeManager) {
    if (-not $sandboxHome -and (Test-Path -LiteralPath $managerExe -PathType Leaf)) {
        Start-Process -FilePath $managerExe -ArgumentList 'exit' -WindowStyle Hidden
        Start-Sleep -Seconds 2
    }
} elseif (-not $sandboxHome -and (Test-Path -LiteralPath $managerExe -PathType Leaf) -and
        @(Get-Process -Name 'dsh-web-manager' -ErrorAction SilentlyContinue).Count -gt 0) {
    $cfgFile = if ($sandboxHome) { Join-Path $sandboxHome '.dsh-webui\config.json' } else { Join-Path $env:USERPROFILE '.dsh-webui\config.json' }
    if (Test-Path -LiteralPath $cfgFile -PathType Leaf) {
        try {
            $cfg = Get-Content -LiteralPath $cfgFile -Raw | ConvertFrom-Json
            foreach ($inst in @($cfg.PSObject.Properties['Instances'].Value)) {
                if ($inst -and $inst.Enabled -and $inst.BackendType -ieq 'windows') {
                    Start-Process -FilePath $managerExe -ArgumentList "closeinstance $($inst.Id)" -WindowStyle Hidden
                    Write-Host "[uninstall] stopping Windows-side instance '$($inst.Id)' (manager kept)"
                }
            }
            Start-Sleep -Seconds 2
        } catch { Write-Warning "[uninstall] could not stop the Windows-side instance: $($_.Exception.Message)" }
    }
}

# 2. The portable tree itself (retry briefly — an anti-virus scan can hold a file).
if (Test-Path -LiteralPath $TargetRoot) {
    $removed = $false
    for ($i = 0; $i -lt 3 -and -not $removed; $i++) {
        try { Remove-Item -LiteralPath $TargetRoot -Recurse -Force -ErrorAction Stop; $removed = $true }
        catch { Start-Sleep -Seconds 2 }
    }
    if ($removed) {
        Write-Host "[uninstall] removed $TargetRoot"
    } else {
        Write-Warning "[uninstall] could not fully remove $TargetRoot (a dsh instance may still hold files; retry after closing it)"
    }
} else {
    Write-Host "[uninstall] nothing at $TargetRoot"
}
$winGone = -not (Test-Path -LiteralPath $TargetRoot)

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

# 5. Optional profile purge (credentials live here!). Never follows into the
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

# 6. Remove the shared tray manager itself — only when BOTH sides are gone and
#    the tree actually came down (winGone covers a failed delete).
if ($removeManager -and $winGone) {
    if (-not $sandboxHome) {
        $managerUninstall = Join-Path $env:LOCALAPPDATA 'dsh-web-manager\app\Uninstall.ps1'
        if (Test-Path -LiteralPath $managerUninstall -PathType Leaf) {
            & $managerUninstall
        } else {
            Write-Warning "[uninstall] manager uninstaller not found: $managerUninstall"
        }
    } else {
        Write-Host '[uninstall] (sandbox) would remove the shared tray manager (both sides gone)'
    }
} else {
    Write-Host '[uninstall] shared tray manager kept'
}

Write-Host 'Offline uninstall finished.'