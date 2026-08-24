# ============================================================
#  dsh-offline-bundle builder (run on an ONLINE Windows build
#  machine). Produces a fully self-contained offline installer:
#
#    bundle-out/dsh-offline-bundle/
#      node/               portable Node (win-x64 zip from nodejs.org / npmmirror)
#      dsh/                @deepseek-ai/dsh npm tree (hoisted layout)
#      profile-web/        pre-baked ~/.dsh (first-run init + manager plugin)
#      dsh-web-manager/    this repo's dist (tray manager + WebView2 backend)
#      Install-Offline.ps1 / Uninstall-Offline.ps1 / bundle.json
#
#  The bake step is gated: the profile is only packaged after a
#  start with dead proxies succeeds ("断网启动通过才算烘焙成功").
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File scripts\Build-Bundle.ps1
#    powershell -ExecutionPolicy Bypass -File scripts\Build-Bundle.ps1 -DshVersion 1.2.3 -SkipProfile
# ============================================================
[CmdletBinding()]
param(
    [string]$NodeVersion = '24.19.0',
    [string]$DshVersion = 'latest',          # npm version spec (@<spec> accepted)
    [ValidateSet('npmmirror', 'nodejs')]
    [string]$NodeSource = 'npmmirror',
    [string]$OutDir = '',
    [int]$BakePort = 3205,                   # sandbox port for the profile bake (never 3080/3081)
    [switch]$SkipNode,                       # reuse an existing bundle node\ dir
    [switch]$SkipDsh,                        # reuse an existing bundle dsh\ dir
    [switch]$SkipProfile,                    # skip the ~/.dsh bake (debug only)
    [switch]$SkipManager,                    # skip Build.ps1 + dist copy (debug only)
    [string]$WslPayloadDir = '',             # prebuilt WSL payload (from Build-Bundle-Wsl.sh); merged into bundle\wsl\
    [string]$ExtraPlugins = 'dshmarket'      # extra plugins pre-installed in the baked profile (space-separated; '' = none)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $projectRoot 'bundle-out' }
$bundle = Join-Path $OutDir 'dsh-offline-bundle'
$nodeDir = Join-Path $bundle 'node'
$dshDir = Join-Path $bundle 'dsh'
$profileDir = Join-Path $bundle 'profile-web'
$managerDir = Join-Path $bundle 'dsh-web-manager'

function Get-Http($url, $dest) {
    Write-Host "[bundle] GET $url"
    Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
}

# ---------- 1. Portable Node ----------
if (-not $SkipNode) {
    $zipName = "node-v$NodeVersion-win-x64.zip"
    $nodeUrl = if ($NodeSource -eq 'npmmirror') {
        "https://cdn.npmmirror.com/binaries/node/v$NodeVersion/$zipName"
    } else {
        "https://nodejs.org/dist/v$NodeVersion/$zipName"
    }
    if (Test-Path -LiteralPath $nodeDir) { Remove-Item -LiteralPath $nodeDir -Recurse -Force }
    [System.IO.Directory]::CreateDirectory($bundle) | Out-Null
    $zipPath = Join-Path $OutDir $zipName
    if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) { Get-Http $nodeUrl $zipPath }
    $extractDir = Join-Path $OutDir 'node-extract'
    if (Test-Path -LiteralPath $extractDir) { Remove-Item -LiteralPath $extractDir -Recurse -Force }
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir
    $inner = Get-ChildItem -LiteralPath $extractDir -Directory | Select-Object -First 1
    if (-not $inner) { throw 'Node zip extraction produced no folder.' }
    Move-Item -LiteralPath $inner.FullName -Destination $nodeDir
    Remove-Item -LiteralPath $extractDir -Recurse -Force
    $nodeExe = Join-Path $nodeDir 'node.exe'
    if (-not (Test-Path -LiteralPath $nodeExe -PathType Leaf)) { throw "node.exe missing after extract: $nodeExe" }
    Write-Host "[bundle] node $(& $nodeExe --version) -> $nodeDir"
}

# ---------- 2. dsh npm tree (hoisted) ----------
if (-not $SkipDsh) {
    $nodeExe = Join-Path $nodeDir 'node.exe'
    if (-not (Test-Path -LiteralPath $nodeExe -PathType Leaf)) { throw "node.exe not found (run without -SkipNode first): $nodeExe" }
    $npmCli = Join-Path $nodeDir 'node_modules\npm\bin\npm-cli.js'
    $stage = Join-Path $OutDir 'dsh-stage'
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    [System.IO.Directory]::CreateDirectory($stage) | Out-Null
    # A package.json HERE is required: npm walks up to the nearest one and would
    # otherwise install into the repo root (which owns the manager plugin package).
    [System.IO.File]::WriteAllText((Join-Path $stage 'package.json'), '{"name":"dsh-bundle-stage","private":true}')
    Write-Host "[bundle] npm install @deepseek-ai/dsh@$DshVersion (npmmirror, global-style)"
    # npm installs into the package root nearest the working directory — run it
    # inside $stage, never the repo root.
    Push-Location $stage
    try {
        & $nodeExe $npmCli install "@deepseek-ai/dsh@$DshVersion" --global-style --omit=dev `
            --no-audit --no-fund --loglevel=error --registry=https://registry.npmmirror.com
        if ($LASTEXITCODE -ne 0) { throw "npm install failed with exit code $LASTEXITCODE." }
    } finally { Pop-Location }
    if (Test-Path -LiteralPath $dshDir) { Remove-Item -LiteralPath $dshDir -Recurse -Force }
    Move-Item -LiteralPath (Join-Path $stage 'node_modules') -Destination $dshDir
    Remove-Item -LiteralPath $stage -Recurse -Force
    $dshPkg = Get-Content -LiteralPath (Join-Path $dshDir '@deepseek-ai\dsh\package.json') -Raw | ConvertFrom-Json
    Write-Host "[bundle] dsh $($dshPkg.version) packaged (bin: $($dshPkg.bin | ConvertTo-Json -Compress))"

    # dsh's `plugin` subcommand shells out to pnpm found on PATH. Ship pnpm
    # inside the portable node so the bake (and the offline target machine)
    # never need a global pnpm (clean CI runners have none; exit 127 otherwise).
    & $nodeExe $npmCli install -g pnpm --prefix $nodeDir --no-audit --no-fund --loglevel=error --registry=https://registry.npmmirror.com
    if ($LASTEXITCODE -ne 0) { throw "pnpm install failed with exit code $LASTEXITCODE." }
    # npm's shim placement varies by prefix config; locate the package and make
    # sure a RELOCATABLE pnpm.cmd sits beside node.exe (the bake prepends this
    # dir to PATH, and the bundle tree gets copied to the target machine).
    $pnpmCjs = Join-Path $nodeDir 'node_modules\pnpm\bin\pnpm.cjs'
    if (-not (Test-Path -LiteralPath $pnpmCjs -PathType Leaf)) {
        $gRoot = ((& $nodeExe $npmCli root -g | Out-String).Trim() -split "`r?`n")[0]
        $pnpmCjs = Join-Path $gRoot 'pnpm\bin\pnpm.cjs'
    }
    if (-not (Test-Path -LiteralPath $pnpmCjs -PathType Leaf)) { throw "pnpm package not found after install." }
    $pnpmShim = Join-Path $nodeDir 'pnpm.cmd'
    if (-not (Test-Path -LiteralPath $pnpmShim -PathType Leaf)) {
        if ($pnpmCjs.StartsWith($nodeDir)) {
            # Relocatable: survives the bundle being copied to target machines.
            $rel = $pnpmCjs.Substring($nodeDir.Length + 1)
            [System.IO.File]::WriteAllText($pnpmShim,
                "@echo off`r`n`"%~dp0node.exe`" `"%~dp0$rel`" %*`r`n", (New-Object System.Text.ASCIIEncoding))
        } else {
            # pnpm landed outside the portable node dir; absolute shim still works.
            [System.IO.File]::WriteAllText($pnpmShim,
                "@echo off`r`n`"$nodeExe`" `"$pnpmCjs`" %*`r`n", (New-Object System.Text.ASCIIEncoding))
        }
    }
    Write-Host "[bundle] pnpm $(& $nodeExe $pnpmCjs --version) bundled into the portable node (shim: $pnpmShim)"
}

# ---------- 3. Manager dist ----------
if (-not $SkipManager) {
    & (Join-Path $PSScriptRoot 'Build.ps1')
    if (Test-Path -LiteralPath $managerDir) { Remove-Item -LiteralPath $managerDir -Recurse -Force }
    [System.IO.Directory]::CreateDirectory($managerDir) | Out-Null
    Copy-Item -Path (Join-Path $projectRoot 'dist\*') -Destination $managerDir -Recurse -Force
    Write-Host "[bundle] manager dist -> $managerDir"
}

# ---------- 4. Profile bake (isolated HOME, offline-start gated) ----------
$dshEntry = ''
if (-not $SkipProfile) {
    $nodeExe = Join-Path $nodeDir 'node.exe'
    $dshPkgJson = Get-Content -LiteralPath (Join-Path $dshDir '@deepseek-ai\dsh\package.json') -Raw | ConvertFrom-Json
    $bin = $dshPkgJson.bin
    $dshEntry = if ($bin -is [string]) { $bin } else { $bin.dsh }
    if (-not $dshEntry) { throw 'Could not resolve the dsh bin entry from package.json.' }
    $dshEntry = Join-Path (Join-Path $dshDir '@deepseek-ai\dsh') $dshEntry

    $bakeHome = Join-Path $OutDir 'bake-home'
    if (Test-Path -LiteralPath $bakeHome) { Remove-Item -LiteralPath $bakeHome -Recurse -Force }
    foreach ($d in @("$bakeHome\.dsh", "$bakeHome\AppData\Roaming", "$bakeHome\AppData\Local")) {
        [System.IO.Directory]::CreateDirectory($d) | Out-Null
    }

    # Runs the bundled dsh with the bake home fully isolated (USERPROFILE/DSH_HOME
    # point into bake-home) and stdout/stderr captured to bundle-out\bake-*.log.
    # NOTE: `dsh web` is an ALIAS of `--profile web`; the app args follow the
    # launcher flags (dsh --profile web --host ... --port ...), exactly the shape
    # the manager's DshLauncher uses. Never put a `web` subcommand after --profile.
    function Invoke-BakeDsh([string[]]$dshArgs, [bool]$offline, [string]$logTag) {
        $outLog = Join-Path $OutDir ("bake-$logTag.out.log")
        $errLog = Join-Path $OutDir ("bake-$logTag.err.log")
        foreach ($l in @($outLog, $errLog)) {
            if (Test-Path -LiteralPath $l) { Remove-Item -LiteralPath $l -Force }
        }
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $env:ComSpec
        # cmd /d /s /c ""node.exe" "bin.js" <args> > "out.log" 2> "err.log""
        $psi.Arguments = '/d /s /c ""' + (Join-Path $nodeDir 'node.exe') + '" "' + $dshEntry + '" ' +
            ($dshArgs -join ' ') + ' > "' + $outLog + '" 2> "' + $errLog + '""'
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.WorkingDirectory = $bakeHome
        # Portable node FIRST on PATH: the bundled pnpm (dsh's plugin subcommand
        # needs it) and a consistent node for any child process.
        $psi.EnvironmentVariables['Path'] = "$nodeDir;" + $psi.EnvironmentVariables['Path']
        $psi.EnvironmentVariables['USERPROFILE'] = $bakeHome
        $psi.EnvironmentVariables['HOME'] = $bakeHome
        $psi.EnvironmentVariables['APPDATA'] = "$bakeHome\AppData\Roaming"
        $psi.EnvironmentVariables['LOCALAPPDATA'] = "$bakeHome\AppData\Local"
        $psi.EnvironmentVariables['DSH_HOME'] = Join-Path $bakeHome '.dsh'
        if ($offline) {
            # Dead proxies: any outbound HTTP(S) fails fast -> proves the baked
            # profile starts with no network at all.
            $psi.EnvironmentVariables['HTTP_PROXY'] = 'http://127.0.0.1:9'
            $psi.EnvironmentVariables['HTTPS_PROXY'] = 'http://127.0.0.1:9'
            $psi.EnvironmentVariables['NO_PROXY'] = ''
        }
        return @{ Process = [System.Diagnostics.Process]::Start($psi); Out = $outLog; Err = $errLog }
    }

    function Show-BakeLogs([hashtable]$run, [int]$tailLines) {
        foreach ($l in @($run.Out, $run.Err)) {
            if (Test-Path -LiteralPath $l) {
                $lines = Get-Content -LiteralPath $l
                if ($lines) {
                    Write-Host "---- $l (last $tailLines) ----"
                    $lines | Select-Object -Last $tailLines | ForEach-Object { Write-Host "  $_" }
                }
            }
        }
    }

    function Stop-BakeProcess([System.Diagnostics.Process]$p) {
        if ($p -and -not $p.HasExited) {
            & taskkill.exe /PID $p.Id /T /F | Out-Null
            Start-Sleep -Seconds 1
        }
    }

    function Wait-BakePort([int]$seconds) {
        $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
        while ([DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 500
            try {
                $c = New-Object Net.Sockets.TcpClient
                $ar = $c.BeginConnect('127.0.0.1', $BakePort, $null, $null)
                if ($ar.AsyncWaitHandle.WaitOne(400) -and $c.Connected) { $c.Close(); return $true }
                $c.Close()
            } catch { }
        }
        return $false
    }

    # 4a. First-run init (online): let dsh materialize ~/.dsh.
    Write-Host "[bundle] bake: first-run init (dsh --profile web --port $BakePort)"
    $run1 = Invoke-BakeDsh @('--profile', 'web', '--host', '127.0.0.1', '--port', "$BakePort", '--no-open') $false 'firstrun'
    if (-not (Wait-BakePort 30)) {
        Show-BakeLogs $run1 20
        Stop-BakeProcess $run1.Process
        throw 'First-run bake failed: dsh did not serve the port within 30s (logs above).'
    }
    Stop-BakeProcess $run1.Process

    # 4b. Install plugins into the baked profile: the manager plugin first, then
    #     any extra pre-installed plugins (default: dshmarket — the plugin
    #     marketplace; users then install more from inside dsh).
    #     NOTE: `plugin` is a subcommand with its OWN required --profile option
    #     (the launcher-level --profile does not propagate into it).
    $pluginSpecs = @("file:$projectRoot")
    foreach ($extra in @($ExtraPlugins -split ' ')) {
        $t = $extra.Trim()
        if ($t -ne '') { $pluginSpecs += $t }
    }
    $run = $null
    foreach ($spec in $pluginSpecs) {
        $tag = if ($spec.StartsWith('file:')) { 'pluginadd' } else { 'pluginadd-' + ($spec -replace '[^A-Za-z0-9]', '') }
        Write-Host "[bundle] bake: dsh plugin --profile web add $spec"
        $run = Invoke-BakeDsh @('plugin', '--profile', 'web', 'add', $spec) $false $tag
        if (-not $run.Process.WaitForExit(300000)) {
            Stop-BakeProcess $run.Process
            Show-BakeLogs $run 20
            throw "plugin add timed out after 300s ($spec; logs above)."
        }
        if ($run.Process.ExitCode -ne 0) {
            Show-BakeLogs $run 30
            throw "plugin add failed with exit code $($run.Process.ExitCode) ($spec; logs above)."
        }
        Write-Host "[bundle] bake: plugin installed -> $spec"
    }

    # 4c. Offline-start gate: dead proxies, must still serve.
    Write-Host '[bundle] bake: offline-start verification (dead proxies)'
    $run3 = Invoke-BakeDsh @('--profile', 'web', '--host', '127.0.0.1', '--port', "$BakePort", '--no-open') $true 'offline'
    $readyOffline = Wait-BakePort 30
    Stop-BakeProcess $run3.Process
    if (-not $readyOffline) {
        Show-BakeLogs $run3 30
        throw 'Profile bake FAILED the offline-start gate (logs above): the baked profile needs the network on start; do not ship it (see TESTING.md).'
    }

    if (Test-Path -LiteralPath $profileDir) { Remove-Item -LiteralPath $profileDir -Recurse -Force }
    Move-Item -LiteralPath (Join-Path $bakeHome '.dsh') -Destination $profileDir
    Remove-Item -LiteralPath $bakeHome -Recurse -Force
    Write-Host "[bundle] profile baked -> $profileDir (offline start verified)"
}

# ---------- 5. Installers + manifest ----------
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-Offline.ps1') -Destination $bundle -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-Offline.ps1') -Destination $bundle -Force

# Optional WSL payload (built on Linux by scripts/Build-Bundle-Wsl.sh):
# node(linux) + dsh tree + baked profile + install-wsl.sh.
$wslInfo = $null
if ($WslPayloadDir -and (Test-Path -LiteralPath (Join-Path $WslPayloadDir 'install-wsl.sh') -PathType Leaf)) {
    $bundleWsl = Join-Path $bundle 'wsl'
    if (Test-Path -LiteralPath $bundleWsl) { Remove-Item -LiteralPath $bundleWsl -Recurse -Force }
    Copy-Item -LiteralPath $WslPayloadDir -Destination $bundleWsl -Recurse
    $wslManifest = Join-Path $bundleWsl 'wsl-bundle.json'
    if (Test-Path -LiteralPath $wslManifest -PathType Leaf) {
        $wslInfo = Get-Content -LiteralPath $wslManifest -Raw | ConvertFrom-Json
    }
    Write-Host "[bundle] WSL payload merged -> $bundleWsl (node=$($wslInfo.NodeVersion) dsh=$($wslInfo.DshVersion))"
} elseif ($WslPayloadDir) {
    throw "WslPayloadDir given but incomplete: $WslPayloadDir (need install-wsl.sh; run scripts/Build-Bundle-Wsl.sh)"
} else {
    Write-Host '[bundle] no WSL payload (pass -WslPayloadDir, built by scripts/Build-Bundle-Wsl.sh, to embed the WSL side)'
}

$nodeVer = (& (Join-Path $nodeDir 'node.exe') --version).Trim()
$dshPkgVer = ''
$dshEntryRel = ''
$dshPkgJsonPath = Join-Path $dshDir '@deepseek-ai\dsh\package.json'
if (Test-Path -LiteralPath $dshPkgJsonPath) {
    $dshPkgJson = Get-Content -LiteralPath $dshPkgJsonPath -Raw | ConvertFrom-Json
    $dshPkgVer = $dshPkgJson.version
    $bin = $dshPkgJson.bin
    $dshEntryRel = if ($bin -is [string]) { $bin } else { $bin.dsh }
}
$managerVer = ''
$managerExe = Join-Path $managerDir 'dsh-web-manager.exe'
if (Test-Path -LiteralPath $managerExe) { $managerVer = (Get-Item -LiteralPath $managerExe).VersionInfo.FileVersion }

$manifest = [ordered]@{
    BundleVersion = '1.0.0'
    CreatedAt     = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    Node          = [ordered]@{ Version = $nodeVer; Arch = 'win-x64' }
    Dsh           = [ordered]@{ Package = '@deepseek-ai/dsh'; Version = $dshPkgVer; Entry = $dshEntryRel }
    Profile       = [ordered]@{ Name = 'web'; Source = 'profile-web'; OfflineStartVerified = (-not $SkipProfile) }
    Manager       = [ordered]@{ Version = $managerVer }
}
if ($wslInfo) {
    $manifest['Wsl'] = [ordered]@{
        NodeVersion  = $wslInfo.NodeVersion
        DshVersion   = $wslInfo.DshVersion
        Source       = 'wsl/'
        OfflineStartVerified = $wslInfo.ProfileOfflineStartVerified
    }
}
$manifest | ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $bundle 'bundle.json') -Encoding utf8

$size = [math]::Round((Get-ChildItem -LiteralPath $bundle -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host ''
Write-Host "[bundle] OK: $bundle ($size MB)"
Get-ChildItem -LiteralPath $bundle | Select-Object Name | Format-Table -HideTableHeaders
