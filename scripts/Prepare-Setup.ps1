# ============================================================
#  Assembles the exe-setup staging dir from a fully built bundle.
#
#  The setup EXE no longer carries the ~200k-file trees as Inno [Files]
#  (per-file extraction + AV scanning made installs crawl). Instead the four
#  heavy trees (node, dsh, profile-web, wsl) are packed into ONE UNCOMPRESSED
#  payload.tar and Inno's lzma2 compresses it inside the setup EXE:
#
#    <OutDir>\dsh-offline-bundle\
#      dsh-web-manager\             (manager dist, WebView2 runtime, ...)
#      Install-Offline.ps1
#      Uninstall-Offline.ps1
#      bundle.json
#      payload.tar                  (node + dsh + profile-web + wsl, stored)
#
#  Why stored tar + Inno lzma2 instead of a pre-compressed zip:
#    - lzma2 roughly halves the setup size vs a stored deflate zip
#      (~220-260 MB vs ~500 MB for the same tree);
#    - the Windows system bsdtar has no built-in zstd/xz (it shells out to an
#      external binary that target machines don't have), so zst/xz payloads
#      are NOT an option; deflate is already-compressed data that lzma2
#      cannot shrink further.
#    - trade-off accepted by design: lzma2 decompression + the tar pass make
#      the install take a bit longer than the stored-zip variant.
#
#  The portable ZIP asset keeps the FULL unpacked bundle (this stage is only
#  for the Inno setup). Install-Offline.ps1 detects payload.tar / payload.zip
#  (Layout B) and streams it straight to the final install root with the
#  system tar.
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

# 1. One UNCOMPRESSED tar for the four heavy trees (root-level members).
#    No -a flag: a plain stored tar; Inno's solid lzma2 does the compression
#    inside the setup EXE. bsdtar reads/writes long paths via pax headers.
$payload = Join-Path $stage 'payload.tar'
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
if (-not (Test-Path -LiteralPath $tar -PathType Leaf)) { throw 'System32\tar.exe is required to assemble payload.tar.' }
& $tar -c -f $payload -C $BundleDir dsh node profile-web wsl
if ($LASTEXITCODE -ne 0) { throw "payload.tar creation failed (tar exit $LASTEXITCODE)." }

# 2. Small components copied as-is.
Copy-Item -LiteralPath (Join-Path $BundleDir 'dsh-web-manager') -Destination (Join-Path $stage 'dsh-web-manager') -Recurse
foreach ($f in @('Install-Offline.ps1', 'Uninstall-Offline.ps1', 'bundle.json')) {
    Copy-Item -LiteralPath (Join-Path $BundleDir $f) -Destination (Join-Path $stage $f) -Force
}

$size = [math]::Round((Get-ChildItem -LiteralPath $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
$payloadSize = [math]::Round((Get-Item -LiteralPath $payload).Length / 1MB, 1)
Write-Host "[setup-stage] OK: $stage ($size MB total; payload.tar $payloadSize MB stored, lzma2-compressed by ISCC)"
