# ============================================================
#  Assembles the exe-setup staging dir from a fully built bundle.
#
#  The setup EXE no longer carries the ~200k-file trees as Inno [Files]
#  (per-file extraction + AV scanning made installs crawl). Instead the four
#  heavy trees (node, dsh, profile-web, wsl) are packed into ONE payload.zip
#  and the stage holds only the small components:
#
#    <OutDir>\dsh-offline-bundle\
#      dsh-web-manager\             (manager dist, WebView2 runtime, ...)
#      Install-Offline.ps1
#      Uninstall-Offline.ps1
#      bundle.json
#      payload.zip                  (node + dsh + profile-web + wsl)
#
#  The portable ZIP asset keeps the FULL unpacked bundle (this stage is only
#  for the Inno setup). Install-Offline.ps1 detects payload.zip (Layout B)
#  and streams it straight to the final install root with the system tar.
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File scripts\Prepare-Setup.ps1 `
#        -BundleDir bundle-out\dsh-offline-bundle `
#        -OutDir bundle-out\setup-stage
# ============================================================
[CmdletBinding()]
param(
    [string]$BundleDir = '',   # full bundle (node/dsh/profile-web/wsl/dsh-web-manager + scripts + bundle.json)
    [string]$OutDir = ''       # stage parent; the stage itself = $OutDir\dsh-offline-bundle
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $BundleDir) { $BundleDir = Join-Path $projectRoot 'bundle-out\dsh-offline-bundle' }
if (-not $OutDir) { $OutDir = Join-Path $projectRoot 'bundle-out\setup-stage' }

foreach ($part in @('node\node.exe', 'dsh\@deepseek-ai\dsh\package.json', 'profile-web', 'wsl', 'dsh-web-manager\dsh-web-manager.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $BundleDir $part))) {
        throw "bundle incomplete: $part missing in $BundleDir (run scripts\Build-Bundle.ps1 first)"
    }
}

$stage = Join-Path $OutDir 'dsh-offline-bundle'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
[System.IO.Directory]::CreateDirectory($stage) | Out-Null

# 1. One archive for the four heavy trees (root-level members, like the
#    portable zip). The Windows bsdtar reads/writes long paths without issue.
$payload = Join-Path $stage 'payload.zip'
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
if (-not (Test-Path -LiteralPath $tar -PathType Leaf)) { throw 'System32\tar.exe is required to assemble payload.zip.' }
& $tar -a -c -f $payload -C $BundleDir dsh node profile-web wsl
if ($LASTEXITCODE -ne 0) { throw "payload.zip creation failed (tar exit $LASTEXITCODE)." }

# 2. Small components copied as-is.
Copy-Item -LiteralPath (Join-Path $BundleDir 'dsh-web-manager') -Destination (Join-Path $stage 'dsh-web-manager') -Recurse
foreach ($f in @('Install-Offline.ps1', 'Uninstall-Offline.ps1', 'bundle.json')) {
    Copy-Item -LiteralPath (Join-Path $BundleDir $f) -Destination (Join-Path $stage $f) -Force
}

$size = [math]::Round((Get-ChildItem -LiteralPath $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
$payloadSize = [math]::Round((Get-Item -LiteralPath $payload).Length / 1MB, 1)
Write-Host "[setup-stage] OK: $stage ($size MB total; payload.zip $payloadSize MB)"