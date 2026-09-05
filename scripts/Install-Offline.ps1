# ============================================================
#  dsh-offline-bundle target-machine installer (runs OFFLINE).
#
#  Installs: portable Node + dsh npm tree + pre-baked ~/.dsh
#  profile + the dsh-web-manager tray app, wires the manager to
#  the bundled dsh via config (DshCommand), optionally adds the
#  bundle bin dir to the user PATH. Idempotent: re-running with
#  a newer bundle upgrades in place (the manager never downgrades).
#
#  Usage (from the extracted bundle root):
#    powershell -ExecutionPolicy Bypass -File Install-Offline.ps1
#    powershell -ExecutionPolicy Bypass -File Install-Offline.ps1 -AutoStart -SkipLaunch
#
#  Testing: honours DSH_WEB_MANAGER_HOME (manager files land in the
#  sandbox home instead of the real %LOCALAPPDATA% install).
# ============================================================
[CmdletBinding()]
param(
    # NOTE: defaults are resolved in the body, not in param(): under
    # `powershell -File` the PS 5.1 $PSScriptRoot can be empty inside parameter
    # default expressions, which made Join-Path fail with "argument to
    # parameter 'Path' is an empty string".
    [string]$BundleDir = '',
    [string]$TargetRoot = '',
    [switch]$NoPath,        # do not touch the user PATH
    [switch]$NoShortcut,    # let the manager installer skip its shortcuts
    [switch]$AutoStart,     # HKCU Run entry for the tray manager
    [switch]$SkipLaunch,    # install without starting the tray
    [switch]$SkipManager,   # node+dsh+profile only (no tray app)
    [switch]$WithWsl,       # force the WSL-side install (error when the payload is absent)
    [switch]$SkipWsl,       # never touch WSL, even when the payload is embedded
    [switch]$NoProgressUI,  # no WinForms progress window (console-only runs)
    [string]$WslDistro = '' # target distro; empty = auto-detect (prefer a running, non-helper distro)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

if (-not $BundleDir) { $BundleDir = $PSScriptRoot }
if (-not $BundleDir) { throw 'BundleDir could not be determined (pass -BundleDir explicitly).' }
if (-not $TargetRoot) { $TargetRoot = Join-Path $env:LOCALAPPDATA 'dsh-bundle' }

# Testing sandbox (TESTING.md): when DSH_WEB_MANAGER_HOME is set, every
# machine-scope write below (manager files, profile, shared config) lands
# inside that home instead of the real user profile.
$sandboxHome = $env:DSH_WEB_MANAGER_HOME

# ---------- Observability: transcript log + progress UI + error popup ----------
# The setup runs this script with a HIDDEN console (Inno runhidden): without a
# visible surface the user cannot tell the install is running (and may kill
# it), and a failure is completely silent. So:
#   1. every line goes to a transcript under %LOCALAPPDATA%\dsh-web-manager\logs
#   2. a small WinForms progress window shows status (unless -NoProgressUI)
#   3. a fatal error pops a MessageBox pointing at the log (visible even hidden)
# Background: on machines with 360/Huorong-style security suites a hidden
# PowerShell doing mass file I/O was observed being terminated mid-install
# (exit 0x40010004), leaving a half-extracted bundle; the visible window makes
# the process legible and lets the user approve any security prompt.
$installLogDir = if ($sandboxHome) { Join-Path $sandboxHome 'AppData\Local\dsh-web-manager\logs' } else { Join-Path $env:LOCALAPPDATA 'dsh-web-manager\logs' }
try { [System.IO.Directory]::CreateDirectory($installLogDir) | Out-Null } catch {}
$installLog = Join-Path $installLogDir 'install-offline.log'
$script:TranscriptOn = $false
try {
    if (Test-Path -LiteralPath $installLog -PathType Leaf) {
        Move-Item -LiteralPath $installLog -Destination ($installLog + '.old') -Force -ErrorAction SilentlyContinue
    }
    Start-Transcript -Path $installLog -Force | Out-Null
    $script:TranscriptOn = $true
} catch { Write-Warning ('[offline] transcript log unavailable: ' + $_.Exception.Message) }

# Progress window on a background STA runspace; the main install thread feeds
# it through the synchronized hashtable. Any UI failure degrades to headless.
$script:UI = $null
function Start-InstallProgressUI {
    if ($NoProgressUI -or $sandboxHome) { return }
    try {
        $sync = [hashtable]::Synchronized(@{
            Status = 'Preparing to install…'
            Done   = $false
            Lines  = [System.Collections.ArrayList]::Synchronized((New-Object System.Collections.ArrayList))
        })
        # STA for WinForms: set on the InitialSessionState (the direct
        # CreateRunspace(ApartmentState) overloads bind ambiguously in PS 5.1).
        $iss = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
        $iss.ApartmentState = [System.Threading.ApartmentState]::STA
        $rs = [runspacefactory]::CreateRunspace($iss)
        $rs.Open()
        $rs.SessionStateProxy.SetVariable('UISync', $sync)
        $ui = [powershell]::Create()
        $ui.Runspace = $rs
        [void]$ui.AddScript(@'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$f = New-Object System.Windows.Forms.Form
$f.Text = 'dsh offline bundle installer'
$f.Size = New-Object System.Drawing.Size(600, 340)
$f.StartPosition = 'CenterScreen'
$f.FormBorderStyle = 'FixedDialog'
$f.MaximizeBox = $false
$f.MinimizeBox = $false
$lbl = New-Object System.Windows.Forms.Label
$lbl.Location = New-Object System.Drawing.Point(16, 16); $lbl.Size = New-Object System.Drawing.Size(556, 36)
$lbl.Font = New-Object System.Drawing.Font($lbl.Font, [System.Drawing.FontStyle]::Bold)
$f.Controls.Add($lbl)
$bar = New-Object System.Windows.Forms.ProgressBar
$bar.Location = New-Object System.Drawing.Point(16, 58); $bar.Size = New-Object System.Drawing.Size(556, 20)
$bar.Style = 'Marquee'
$f.Controls.Add($bar)
$list = New-Object System.Windows.Forms.ListBox
$list.Location = New-Object System.Drawing.Point(16, 90); $list.Size = New-Object System.Drawing.Size(556, 158)
$f.Controls.Add($list)
$hint = New-Object System.Windows.Forms.Label
$hint.Location = New-Object System.Drawing.Point(16, 256); $hint.Size = New-Object System.Drawing.Size(556, 34)
$hint.ForeColor = [System.Drawing.Color]::Gray
$hint.Text = 'Installing, please do not close this window. If your security software (360 / Huorong etc.) prompts, please allow this installer.'
$f.Controls.Add($hint)
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 300
$timer.Add_Tick({
    try {
        $lbl.Text = $UISync.Status
        while ($UISync.Lines.Count -gt 0) {
            $item = $UISync.Lines[0]
            $UISync.Lines.RemoveAt(0)
            [void]$list.Items.Add($item)
            while ($list.Items.Count -gt 200) { $list.Items.RemoveAt(0) }
            $list.TopIndex = $list.Items.Count - 1
        }
        if ($UISync.Done) { $timer.Stop(); $f.Close() }
    } catch {}
})
$timer.Start()
[void]$f.ShowDialog()
'@)
        [void]$ui.BeginInvoke()
        $script:UI = @{ Sync = $sync; PS = $ui; RS = $rs }
    } catch {
        Write-Warning ('[offline] progress UI unavailable: ' + $_.Exception.Message)
        $script:UI = $null
    }
}
function Write-Status([string]$msg) {
    Write-Host $msg
    if ($script:UI) {
        try {
            $script:UI.Sync.Status = $msg
            [void]$script:UI.Sync.Lines.Add($msg)
        } catch {}
    }
}
function Stop-InstallProgressUI([string]$finalStatus) {
    if (-not $script:UI) { return }
    try {
        $script:UI.Sync.Status = $finalStatus
        [void]$script:UI.Sync.Lines.Add($finalStatus)
        $script:UI.Sync.Done = $true
        Start-Sleep -Milliseconds 800
    } catch {}
    try { $script:UI.PS.Stop() } catch {}
    try { $script:UI.PS.Dispose() } catch {}
    try { $script:UI.RS.Close() } catch {}
    $script:UI = $null
}
function Stop-InstallTranscript {
    if ($script:TranscriptOn) { try { Stop-Transcript | Out-Null } catch {} ; $script:TranscriptOn = $false }
}

# Fatal-error surface: a MessageBox works even when the console is hidden.
trap {
    $errMsg = $_.Exception.Message
    Write-Host ('[offline] INSTALL FAILED: ' + $errMsg)
    Stop-InstallProgressUI ('Install failed: ' + $errMsg)
    if (-not $sandboxHome) {
        try {
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.MessageBox]::Show(
                'dsh offline bundle install failed:' + [Environment]::NewLine + $errMsg +
                [Environment]::NewLine + [Environment]::NewLine +
                'If your security software (360 / Huorong etc.) blocked the installer, allow it and run the setup again (the installer repairs partial installs).' +
                [Environment]::NewLine + 'Log: ' + $installLog,
                'dsh offline bundle', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
        } catch {}
    }
    Stop-InstallTranscript
    exit 1
}

Start-InstallProgressUI
Write-Status '[offline] starting offline install'

# ---------- 0. Preflight ----------
$manifestPath = Join-Path $BundleDir 'bundle.json'
$manifest = $null
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Write-Status "[offline] bundle: v$($manifest.BundleVersion) node=$($manifest.Node.Version) dsh=$($manifest.Dsh.Version) manager=$($manifest.Manager.Version)"
}
# Two delivery layouts share this installer:
#   Layout B (exe setup): heavy trees (node/dsh/profile-web/wsl) travel as ONE
#     payload archive (payload.tar stored, lzma2-compressed inside the setup
#     by Inno; legacy bundles carry payload.zip). Install-Offline extracts it
#     straight to $TargetRoot with the system tar (single stream, no per-file
#     copy).
#   Layout A (portable zip): the trees sit unpacked beside this script.
$payloadArchive = Join-Path $BundleDir 'payload.tar'
if (-not (Test-Path -LiteralPath $payloadArchive -PathType Leaf)) {
    $payloadArchive = Join-Path $BundleDir 'payload.zip'
}
$treeLayoutB = Test-Path -LiteralPath $payloadArchive -PathType Leaf

# Heads-up BEFORE the heavy I/O: proactive security suites have been observed
# quarantining the freshly extracted trees within seconds (survival check
# below catches it — this warning explains what to do before it happens).
$secEarly = @(Get-Process | Where-Object { $_.Name -match '^(360tray|360safe|360zip|ZhuDongFangYu|HipsDaemon|HipsTray|usysdiag|wsctrl|sysdiag)$' } | ForEach-Object { $_.Name } | Select-Object -Unique)
if ($secEarly.Count -gt 0) {
    Write-Warning ("[offline] security software running (" + ($secEarly -join ', ') + "). If payload files vanish during install, whitelist '" + $TargetRoot + "' (and exit the suite while installing), then run the setup again.")
}
if ($treeLayoutB) {
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Bundle incomplete: bundle.json missing under $BundleDir."
    }
} else {
    foreach ($part in @('node\node.exe', 'dsh\@deepseek-ai\dsh\package.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $BundleDir $part) -PathType Leaf)) {
            throw "Bundle incomplete: $part missing under $BundleDir (rebuild with scripts\Build-Bundle.ps1)."
        }
    }
}
if (-not [Environment]::Is64BitOperatingSystem) { throw 'This bundle targets Windows x64 only.' }

# WebView2 Runtime: informational. Missing runtime is not fatal — the manager
# auto-falls back to the Edge app-window mode (see ManagerService.ResolveWindowBackend).
$wv2Ok = $false
foreach ($probe in @(
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}')) {
    if (Test-Path -LiteralPath $probe) { $wv2Ok = $true; break }
}
if ($wv2Ok) { Write-Status '[offline] WebView2 Runtime: present (embedded window backend active)' }
else { Write-Warning '[offline] WebView2 Runtime not found: the tray manager will fall back to Edge app windows (taskbar shows the Edge icon).' }

# ---------- 1. Portable node + dsh tree ----------
# Same-volume fast path: hard-link the (immutable-at-runtime) node/dsh trees
# from the extracted bundle instead of copying ~1.4 GB twice. Falls back to
# robocopy /MIR on any failure (cross-volume, reparse points, locked files).
function Sync-Dir([string]$src, [string]$dst) {
    if (-not (Test-Path -LiteralPath $src)) { throw "Bundle component missing: $src" }
    $srcAbs = (Resolve-Path -LiteralPath $src).Path
    $srcRoot = [System.IO.Path]::GetPathRoot($srcAbs)
    $dstAbs = [System.IO.Path]::GetFullPath($dst)
    $dstRoot = [System.IO.Path]::GetPathRoot($dstAbs)
    if ($srcRoot -ieq $dstRoot) {
        try {
            if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Recurse -Force }
            [System.IO.Directory]::CreateDirectory($dstAbs) | Out-Null
            # Breadth-first walk creating one hard link per file; directories are
            # never linked (only files). Any reparse point (junction/symlink) in
            # the source aborts the pass so the robocopy fallback handles it.
            $queue = New-Object System.Collections.Generic.Queue[string]
            $queue.Enqueue($srcAbs)
            while ($queue.Count -gt 0) {
                $dir = $queue.Dequeue()
                foreach ($sub in @(Get-ChildItem -LiteralPath $dir -Directory -Force -ErrorAction Stop)) {
                    $rel = $sub.FullName.Substring($srcAbs.Length).TrimStart('\')
                    [System.IO.Directory]::CreateDirectory((Join-Path $dstAbs $rel)) | Out-Null
                    $queue.Enqueue($sub.FullName)
                }
                foreach ($f in @(Get-ChildItem -LiteralPath $dir -File -Force -ErrorAction Stop)) {
                    if ($f.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                        throw 'reparse point in source tree; using copy fallback'
                    }
                    $link = Join-Path $dstAbs $f.FullName.Substring($srcAbs.Length).TrimStart('\')
                    $null = New-Item -ItemType HardLink -Path $link -Target $f.FullName -Force
                }
            }
            Write-Status "[offline] linked $src -> $dst (hard links, same volume)"
            return
        } catch {
            Write-Warning "[offline] hard-link pass failed ($($_.Exception.Message)); falling back to robocopy"
            if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Recurse -Force }
        }
    } else {
        Write-Status "[offline] $src and $dst on different volumes; copying"
    }
    & robocopy.exe $src $dst /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE) copying $src -> $dst" }
}
if (-not $treeLayoutB) {
    # Layout A (portable zip): the trees sit unpacked next to this script.
    Sync-Dir (Join-Path $BundleDir 'node') (Join-Path $TargetRoot 'node')
    Sync-Dir (Join-Path $BundleDir 'dsh') (Join-Path $TargetRoot 'dsh')
    Write-Status "[offline] node + dsh tree -> $TargetRoot"
} else {
    # Layout B: one archive, one pass. Drop stale trees first so files removed
    # in a newer bundle do not linger (mirrors robocopy /MIR semantics).
    [System.IO.Directory]::CreateDirectory($TargetRoot) | Out-Null
    foreach ($rel in @('node', 'dsh', 'profile-web', 'wsl')) {
        $old = Join-Path $TargetRoot $rel
        if (Test-Path -LiteralPath $old) {
            try { Remove-Item -LiteralPath $old -Recurse -Force }
            catch { Write-Warning "[offline] could not remove stale $old ($($_.Exception.Message))" }
        }
    }
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    $isZip = $payloadArchive.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)
    if (Test-Path -LiteralPath $tar -PathType Leaf) {
        # bsdtar auto-detects the container (tar / zip) from the content.
        & $tar -xf $payloadArchive -C $TargetRoot
        if ($LASTEXITCODE -ne 0) { throw "payload extraction failed (tar exit $LASTEXITCODE): $payloadArchive -> $TargetRoot" }
    } elseif ($isZip -and (Get-Command Expand-Archive -ErrorAction SilentlyContinue)) {
        # Very old Win10 without tar.exe: PowerShell zip fallback (slower).
        Expand-Archive -LiteralPath $payloadArchive -DestinationPath $TargetRoot -Force
    } else {
        throw 'System32\tar.exe is required to unpack the payload archive and is missing on this machine.'
    }
    # SURVIVAL CHECK, immediately after extraction. Real-world case: security
    # software with proactive/real-time protection (360 ZhuDongFangYu /
    # HipsDaemon, Huorong) quarantined the freshly extracted node/dsh trees
    # within seconds — tar returned 0, then the files were gone, leaving a
    # half-installed bundle (this failed LATE, after the WSL pass, with a
    # confusing "node.exe is not recognized" error). Fail here, fast, with a
    # named culprit and the exact whitelist path. The payload archive is only
    # deleted AFTER this check so a blocked install can simply be re-run.
    if (-not (Test-Path -LiteralPath (Join-Path $TargetRoot 'node\node.exe') -PathType Leaf)) {
        $sec = @(Get-Process | Where-Object { $_.Name -match '^(360tray|360safe|360zip|ZhuDongFangYu|HipsDaemon|HipsTray|usysdiag|wsctrl|sysdiag)$' } | ForEach-Object { $_.Name } | Select-Object -Unique)
        $secNote = if ($sec.Count -gt 0) { ' Detected security software: ' + ($sec -join ', ') + '. Whitelist the folder "' + $TargetRoot + '" (or exit the suite), then run the setup again.' } else { ' Re-run the setup; if this repeats, whitelist "' + $TargetRoot + '" in your antivirus.' }
        throw ("payload files vanished right after extraction (antivirus quarantine?)." + $secNote)
    }
    try { Remove-Item -LiteralPath $payloadArchive -Force -ErrorAction SilentlyContinue } catch { }
    Write-Status "[offline] payload extracted (node+dsh+profile-web+wsl) -> $TargetRoot (single archive pass)"
}

# ---------- 2. dsh.cmd shim (absolute paths into the installed tree) ----------
$dshPkg = Get-Content -LiteralPath (Join-Path $TargetRoot 'dsh\@deepseek-ai\dsh\package.json') -Raw | ConvertFrom-Json
$bin = $dshPkg.bin
$entry = if ($bin -is [string]) { $bin } else { $bin.dsh }
if (-not $entry) { throw 'Could not resolve the dsh bin entry from package.json.' }
$binDir = Join-Path $TargetRoot 'bin'
[System.IO.Directory]::CreateDirectory($binDir) | Out-Null
$dshCmd = Join-Path $binDir 'dsh.cmd'
$nodeExe = Join-Path $TargetRoot 'node\node.exe'
$entryAbs = Join-Path (Join-Path $TargetRoot 'dsh\@deepseek-ai\dsh') $entry
$cmdBody = "@echo off`r`nsetlocal`r`n`"$nodeExe`" `"$entryAbs`" %*`r`n"
[System.IO.File]::WriteAllText($dshCmd, $cmdBody, (New-Object System.Text.ASCIIEncoding))
Write-Status "[offline] shim: $dshCmd -> node $entry"

# ---------- 3. User PATH (HKCU only; idempotent; type-preserving) ----------
if (-not $NoPath) {
    $envKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    if ($envKey) {
        $kind = $envKey.GetValueKind('Path')
        $cur = [string]$envKey.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $parts = @($cur -split ';' | Where-Object { $_ -ne '' })
        if ($parts -notcontains $binDir) {
            $envKey.SetValue('Path', ($parts + $binDir) -join ';', $kind)
            Write-Status "[offline] user PATH += $binDir (new terminals only)"
        } else {
            Write-Status '[offline] user PATH already contains the bundle bin dir'
        }
        $envKey.Close()
        # Broadcast so new processes started from Explorer pick it up.
        Add-Type -Namespace Win32 -Name Native -MemberDefinition '[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);'
        $result = [UIntPtr]::Zero
        [Win32.Native]::SendMessageTimeout([IntPtr]0xffff, 0x001A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$result) | Out-Null
    }
} else {
    Write-Status '[offline] -NoPath: PATH untouched (the manager still works via DshCommand)'
}

# ---------- 4. Pre-baked profile -> ~/.dsh (sandbox-aware) ----------
$profileSrc = if ($treeLayoutB) { Join-Path $TargetRoot 'profile-web' } else { Join-Path $BundleDir 'profile-web' }
if (Test-Path -LiteralPath $profileSrc) {
    # Sandbox testing (TESTING.md): never touch the real %USERPROFILE%\.dsh.
    $dshHome = if ($sandboxHome) { Join-Path $sandboxHome '.dsh' } else { Join-Path $env:USERPROFILE '.dsh' }
    if (-not (Test-Path -LiteralPath $dshHome)) {
        Copy-Item -LiteralPath $profileSrc -Destination $dshHome -Recurse
        Write-Status "[offline] profile installed -> $dshHome"
    } else {
        # Existing .dsh: fill gaps only — never clobber the user's profiles,
        # settings or credentials (API keys are entered in the WebUI).
        Get-ChildItem -LiteralPath $profileSrc -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($profileSrc.Length + 1)
            $dest = Join-Path $dshHome $rel
            if (-not (Test-Path -LiteralPath $dest)) {
                [System.IO.Directory]::CreateDirectory((Split-Path -Parent $dest)) | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $dest
            }
        }
        Write-Status "[offline] existing $dshHome kept; missing files filled from the bundle"
    }
} else {
    Write-Warning '[offline] bundle has no profile-web\; first dsh start will initialize ~/.dsh itself'
}

# ---------- 4b. Manager plugin package + local pnpm store (portable profile) ----------
# The baked profile records pnpm metadata against the CI runner's store path and
# an absolute file: path for the manager plugin (see Build-Bundle.ps1). Without
# rewiring both for THIS machine, the first `dsh plugin add/update` fails with
# ERR_PNPM_UNEXPECTED_STORE (store path mismatch) or file:D:\a\... not found.
$dshHome = if ($sandboxHome) { Join-Path $sandboxHome '.dsh' } else { Join-Path $env:USERPROFILE '.dsh' }
if (Test-Path -LiteralPath $dshHome) {
    # 4b-1. Place the real manager plugin package at ~/.dsh/manager-pkg — the
    #       target of the profile's `file:../../manager-pkg` dependency. The
    #       shipped node_modules already contains a copy (works offline); this
    #       target is what pnpm re-resolves on the first add/update.
    $managerPkgSrc = if ($treeLayoutB) { Join-Path $TargetRoot 'manager-pkg' } else { Join-Path $BundleDir 'manager-pkg' }
    if (Test-Path -LiteralPath $managerPkgSrc -PathType Container) {
        $managerPkgDest = Join-Path $dshHome 'manager-pkg'
        if (-not (Test-Path -LiteralPath $managerPkgDest)) {
            Copy-Item -LiteralPath $managerPkgSrc -Destination $managerPkgDest -Recurse
            Write-Status "[offline] manager plugin package -> $managerPkgDest"
        } else {
            # Refresh the package files only (never clobber user edits to config
            # inside the plugin).
            Get-ChildItem -LiteralPath $managerPkgSrc -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring($managerPkgSrc.Length + 1)
                $dest = Join-Path $managerPkgDest $rel
                if (-not (Test-Path -LiteralPath $dest)) {
                    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $dest)) | Out-Null
                    Copy-Item -LiteralPath $_.FullName -Destination $dest
                }
            }
            Write-Status "[offline] existing ~/.dsh/manager-pkg kept; missing files filled"
        }
    } else {
        Write-Warning '[offline] bundle has no manager-pkg\; the manager plugin file: dep will not resolve on dsh plugin ops'
    }

    # 4b-2. Rewire pnpm's .modules.yaml to the LOCAL default store so pnpm's
    #       compatibility check passes without a reinstall or network. The bake
    #       stripped the CI store path (see Build-Bundle.ps1); here we write the
    #       value pnpm computes for this machine (`pnpm config get store-dir`).
    $profileWeb = Join-Path $dshHome 'profiles\web'
    $modulesYaml = Join-Path $profileWeb 'node_modules\.modules.yaml'
    $pnpmCmd = Join-Path $TargetRoot 'node\pnpm.cmd'
    if ((Test-Path -LiteralPath $modulesYaml -PathType Leaf) -and (Test-Path -LiteralPath $pnpmCmd -PathType Leaf)) {
        try {
            # `pnpm config get store-dir` returns "undefined" when no explicit
            # store-dir is configured (the default is computed internally);
            # `pnpm store path` prints the actual absolute store location.
            $storeRaw = & $pnpmCmd store path 2>$null | Select-Object -Last 1
            $storeDir = if ($storeRaw) { $storeRaw.Trim() } else { '' }
            if ([string]::IsNullOrEmpty($storeDir)) { throw 'pnpm returned an empty store-dir' }
            $yaml = Get-Content -LiteralPath $modulesYaml -Raw | ConvertFrom-Json
            if ($yaml.PSObject.Properties['storeDir']) { $yaml.PSObject.Properties.Remove('storeDir') }
            if ($yaml.PSObject.Properties['virtualStoreDir']) { $yaml.PSObject.Properties.Remove('virtualStoreDir') }
            $yaml | Add-Member -NotePropertyName 'storeDir' -NotePropertyValue $storeDir -Force
            $yaml | Add-Member -NotePropertyName 'virtualStoreDir' -NotePropertyValue (Join-Path $profileWeb 'node_modules\.pnpm') -Force
            $yaml | ConvertTo-Json -Depth 8 | ForEach-Object { [System.IO.File]::WriteAllText($modulesYaml, $_, (New-Object System.Text.UTF8Encoding($false))) }
            Write-Status "[offline] pnpm store rewired -> $storeDir (local default)"

            # 4b-3. Read-only self-check: pnpm must accept the metadata without
            #       ERR_PNPM_UNEXPECTED_STORE (list never touches the store).
            Push-Location -LiteralPath $profileWeb
            try {
                & $pnpmCmd list --depth 0 2>&1 | Out-Null
                if ($LASTEXITCODE -eq 0) {
                    Write-Status '[offline] pnpm self-check OK (store metadata accepted)'
                } else {
                    Write-Warning "[offline] pnpm self-check failed (exit $LASTEXITCODE); dsh plugin add/update may need one 'pnpm install' on first use"
                }
            } finally {
                Pop-Location
            }
        } catch {
            Write-Warning "[offline] could not rewire pnpm store ($($_.Exception.Message)); dsh plugin add/update may need one 'pnpm install' on first use"
        }
    }
}

# ---------- 5. Tray manager ----------
if (-not $SkipManager) {
    if ($sandboxHome) {
        # Testing layout (TESTING.md): mirror dist into the sandbox app dir, no
        # version downgrade, no real-machine shortcuts or config touched.
        $appRoot = Join-Path $sandboxHome 'AppData\Local\dsh-web-manager\app'
        $exe = Join-Path $appRoot 'dsh-web-manager.exe'
        $bundledExe = Join-Path $BundleDir 'dsh-web-manager\dsh-web-manager.exe'
        function Compare-Version([string]$a, [string]$b) {
            $pa = @($a -split '\.' | ForEach-Object { $n = 0; [void][int]::TryParse($_, [ref]$n); $n })
            $pb = @($b -split '\.' | ForEach-Object { $n = 0; [void][int]::TryParse($_, [ref]$n); $n })
            for ($i = 0; $i -lt [Math]::Max($pa.Count, $pb.Count); $i++) {
                $x = if ($i -lt $pa.Count) { $pa[$i] } else { 0 }
                $y = if ($i -lt $pb.Count) { $pb[$i] } else { 0 }
                if ($x -ne $y) { return if ($x -gt $y) { 1 } else { -1 } }
            }
            return 0
        }
        $installIt = $true
        if (Test-Path -LiteralPath $exe -PathType Leaf) {
            $oldV = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
            $newV = (Get-Item -LiteralPath $bundledExe).VersionInfo.FileVersion
            if ((Compare-Version $oldV $newV) -ge 0) { $installIt = $false; Write-Status "[offline] sandbox manager v$oldV kept (bundle v$newV not newer)" }
        }
        if ($installIt) {
            [System.IO.Directory]::CreateDirectory($appRoot) | Out-Null
            Copy-Item -Path (Join-Path $BundleDir 'dsh-web-manager\*') -Destination $appRoot -Recurse -Force
            Write-Status "[offline] sandbox manager -> $appRoot"
        }
    } else {
        $installer = Join-Path $BundleDir 'dsh-web-manager\Install.ps1'
        if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Manager installer missing: $installer" }
        # Stop a RUNNING tray manager before Install.ps1 overwrites its files:
        # the exe (and its WebView2 DLLs) are locked while the process lives, and
        # Copy-Item on a locked exe throws — with $ErrorActionPreference=Stop that
        # aborted the whole install at the very last step. The control-pipe 'exit'
        # action stops the manager gracefully (it stops its managed dsh too).
        $existingMgr = Join-Path $env:LOCALAPPDATA 'dsh-web-manager\app\dsh-web-manager.exe'
        if ((Test-Path -LiteralPath $existingMgr -PathType Leaf) -and
            (Get-Process -Name 'dsh-web-manager' -ErrorAction SilentlyContinue)) {
            Write-Status '[offline] stopping the running tray manager for the upgrade…'
            Start-Process -FilePath $existingMgr -ArgumentList 'exit' -WindowStyle Hidden
            for ($w = 0; $w -lt 30 -and (Get-Process -Name 'dsh-web-manager' -ErrorAction SilentlyContinue); $w++) { Start-Sleep 1 }
            if (Get-Process -Name 'dsh-web-manager' -ErrorAction SilentlyContinue) {
                Write-Warning '[offline] the tray manager is still running; Install.ps1 may fail on locked files'
            }
        }
        # -SourceDir is REQUIRED here: Install.ps1 defaults its source to
        # <parent-of-its-dir>\dist (repo layout). In the bundle it lives INSIDE
        # the dist copy, so the default resolves to a nonexistent ...\dist\dist.
        $installArgs = @{ SkipLaunch = $true; SourceDir = (Join-Path $BundleDir 'dsh-web-manager') }
        if ($NoShortcut) { $installArgs.NoShortcut = $true }
        & $installer @installArgs
    }
}

# ---------- 6. Config wiring: DshCommand -> bundled shim ----------
# IMPORTANT: this runs BEFORE the WSL step so the Windows-side config is
# always written even when the WSL install fails (the WSL step is optional).
# If DshCommand were only set after the WSL step, a WSL failure with
# $ErrorActionPreference=Stop would leave the config without DshCommand,
# and the manager would not find the bundled dsh binary.
$sharedDir = if ($sandboxHome) { Join-Path $sandboxHome '.dsh-webui' } else { Join-Path $env:USERPROFILE '.dsh-webui' }
$configFile = Join-Path $sharedDir 'config.json'
[System.IO.Directory]::CreateDirectory($sharedDir) | Out-Null
$config = $null
if (Test-Path -LiteralPath $configFile -PathType Leaf) {
    $config = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
} else {
    $example = Join-Path $BundleDir 'dsh-web-manager\config.example.json'
    if (Test-Path -LiteralPath $example -PathType Leaf) {
        $config = Get-Content -LiteralPath $example -Raw | ConvertFrom-Json
    } else {
        $config = New-Object PSObject
    }
}
if (-not ($config.PSObject.Properties['DshCommand'])) {
    $config | Add-Member -MemberType NoteProperty -Name DshCommand -Value ''
}
# Only pin the bundled shim when unset, or when it already points into a
# dsh-bundle tree (upgrade); a deliberate user override wins.
$cur = [string]$config.DshCommand
if ($cur -eq '' -or $cur -like '*\dsh-bundle\*') { $config.DshCommand = $dshCmd }
if (-not ($config.PSObject.Properties['WindowBackend'])) {
	    $config | Add-Member -MemberType NoteProperty -Name WindowBackend -Value 'auto'
	}
	$config | ConvertTo-Json -Depth 8 | ForEach-Object { [System.IO.File]::WriteAllText($configFile, $_, (New-Object System.Text.UTF8Encoding($false))) }
Write-Status "[offline] config wired: DshCommand=$($config.DshCommand) WindowBackend=$($config.WindowBackend) ($configFile)"

# ---------- 5b. WSL side (optional; non-fatal) ----------
# Runs AFTER the Windows config wiring so a WSL failure never blocks the
# Windows-side config (DshCommand) from being written. The WSL step is
# best-effort: failures are warnings, not fatal errors.
$wslInstalledDistro = ''
$wslPayload = if ($treeLayoutB) { Join-Path $TargetRoot 'wsl' } else { Join-Path $BundleDir 'wsl' }
$wslWanted = $false
if ($SkipWsl) {
    Write-Status '[offline] -SkipWsl: WSL side untouched'
} elseif ($WithWsl -or (Test-Path -LiteralPath (Join-Path $wslPayload 'install-wsl.sh') -PathType Leaf)) {
    $wslWanted = $true
}
if ($wslWanted -and $sandboxHome) {
    Write-Status '[offline] sandbox mode: WSL side skipped (real distros only)'
    $wslWanted = $false
}
if ($wslWanted) {
    if (-not (Test-Path -LiteralPath (Join-Path $wslPayload 'install-wsl.sh') -PathType Leaf)) {
        Write-Warning '[offline] bundle has no wsl\ payload; WSL side skipped'
        $wslWanted = $false
    }
}
if ($wslWanted) {
    $wslExe = Join-Path $env:SystemRoot 'System32\wsl.exe'
    if (-not (Test-Path -LiteralPath $wslExe -PathType Leaf)) {
        if ($WithWsl) { Write-Warning '[offline] -WithWsl given but wsl.exe not found; WSL side skipped' }
        else { Write-Status '[offline] wsl.exe not found: WSL side skipped' }
        $wslWanted = $false
    }
}
if ($wslWanted) {
    function Convert-ToWslPath([string]$winPath) {
        $full = [System.IO.Path]::GetFullPath($winPath)
        $drive = $full.Substring(0, 1).ToLowerInvariant()
        return '/mnt/' + $drive + ($full.Substring(2) -replace '\\', '/')
    }
    function Get-WslLines([string[]]$wslArgs) {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $wslExe
        $psi.Arguments = ($wslArgs -join ' ')
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.StandardOutputEncoding = [System.Text.Encoding]::Unicode
        $p = [System.Diagnostics.Process]::Start($psi)
        $out = $p.StandardOutput.ReadToEnd()
        $p.WaitForExit(10000) | Out-Null
        return (@($out -replace "`0", '' -split "`r?`n") | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
    }
    $helpers = @('docker-desktop', 'docker-desktop-data', 'rancher-desktop', 'podman-machine-default')
    $distro = $WslDistro
    if (-not $distro) {
        $running = @(Get-WslLines @('-l', '--running') | Select-Object -Skip 1 | Where-Object { $h = $_ -replace ' \(.+\)$', ''; $helpers -notcontains $h })
        $all     = @(Get-WslLines @('-l', '-q'))
        if ($running.Count -gt 0)      { $distro = ($running[0] -replace ' \(.+\)$', '') }
        elseif ($all.Count -gt 0)      { $distro = $all[0] }
    }
    if (-not $distro) {
        if ($WithWsl) { Write-Warning '[offline] -WithWsl given but no WSL distro found; WSL side skipped' }
        else { Write-Status '[offline] no WSL distro found: WSL side skipped' }
        $wslWanted = $false
    } else {
        Write-Status "[offline] WSL target distro: $distro"
        $srcWsl = Convert-ToWslPath $wslPayload
        $inner = 'cp "' + $srcWsl + '/install-wsl.sh" /tmp/install-wsl.sh && bash /tmp/install-wsl.sh --src "' + $srcWsl + '" --prefix "$HOME/.dsh-bundle"'
        try {
            & $wslExe -d $distro -- bash -c $inner
            if ($LASTEXITCODE -eq 0) {
                $wslInstalledDistro = $distro
                Write-Status "[offline] WSL side installed into '$distro' (~/.dsh-bundle + ~/.local/bin/dsh + profile + companion scripts)"
                # Remember the distro in config so the tray's WSL backend targets it.
                if (Test-Path -LiteralPath $configFile -PathType Leaf) {
                    try {
                        $cfg = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
                        if ($cfg.PSObject.Properties['WslDistro']) {
                            $curDistro = [string]$cfg.WslDistro
                            if ($curDistro -eq '' -or $curDistro -eq $wslInstalledDistro) {
                                $cfg.WslDistro = $wslInstalledDistro
                                $cfg | ConvertTo-Json -Depth 8 | ForEach-Object { [System.IO.File]::WriteAllText($configFile, $_, (New-Object System.Text.UTF8Encoding($false))) }
                            }
                        }
                    } catch { Write-Warning "[offline] could not update WslDistro in config: $($_.Exception.Message)" }
                }
            } else {
                Write-Warning "[offline] WSL-side install script exited $LASTEXITCODE (the 'Failed to start the systemd user session' notice is harmless); WSL side skipped"
            }
        } catch {
            Write-Warning "[offline] WSL-side install failed: $($_.Exception.Message); WSL side skipped"
        }
    }
}

# ---------- 7. Autostart (optional) ----------
if ($AutoStart -and -not $sandboxHome) {
    $runKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
    $runKey.SetValue('dsh-web-manager', '"' + (Join-Path $env:LOCALAPPDATA 'dsh-web-manager\app\dsh-web-manager.exe') + '" open')
    $runKey.Close()
    Write-Status '[offline] autostart enabled (HKCU Run)'
}

# ---------- 8. Verify + launch ----------
$nodeVer = & (Join-Path $TargetRoot 'node\node.exe') --version
Write-Status "[offline] verify: portable node $nodeVer"
$dshVersion = & $dshCmd --version 2>$null
if ($LASTEXITCODE -eq 0 -and $dshVersion) { Write-Status "[offline] verify: dsh $($dshVersion.Trim()) via $dshCmd" }
else { Write-Warning "[offline] dsh --version via shim failed (exit $LASTEXITCODE) — inspect $dshCmd" }

if (-not $SkipLaunch) {
    if ($sandboxHome) {
        Write-Status '[offline] sandbox mode: not auto-launching the tray (start it manually with DSH_WEB_MANAGER_HOME set)'
    } else {
        $exe = Join-Path $env:LOCALAPPDATA 'dsh-web-manager\app\dsh-web-manager.exe'
        if (Test-Path -LiteralPath $exe -PathType Leaf) {
            Start-Process -FilePath $exe -ArgumentList 'open' -WindowStyle Hidden
            Write-Status '[offline] tray manager started (look for the whale icon).'
        }
    }
}

Write-Host ''
Write-Status 'Offline install finished.'
Write-Host "  node/dsh tree : $TargetRoot"
Write-Host "  dsh shim      : $dshCmd"
$profileNote = if ($sandboxHome) { (Join-Path $sandboxHome '.dsh') } else { (Join-Path $env:USERPROFILE '.dsh') }
Write-Host "  profile       : $profileNote"
if (-not $sandboxHome) { Write-Host '  tray manager  : %LOCALAPPDATA%\dsh-web-manager\app (shortcuts on desktop/start menu)' }

# Let the progress window show the final state, then shut everything down.
Stop-InstallProgressUI 'Install finished — the tray manager is starting.'
Stop-InstallTranscript
# Explicit success exit code: a native command run earlier (e.g. the dsh shim
# verify) leaves its exit code in $LASTEXITCODE, which would otherwise leak
# out as the script's exit code and make Inno log a phantom failure.
exit 0
