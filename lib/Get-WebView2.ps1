# ============================================================
#  Fetches the WebView2 SDK assemblies into lib\ so scripts\Build.ps1
#  can compile the embedded-window backend. No NuGet client needed:
#  the .nupkg is a zip; we download it directly and extract by name.
#
#  Produces (exact layout Build.ps1 / ensure-shortcut.ps1 expect):
#    lib\Microsoft.Web.WebView2.Core.dll
#    lib\Microsoft.Web.WebView2.WinForms.dll
#    lib\native\x64\WebView2Loader.dll
#    lib\native\x86\WebView2Loader.dll
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File lib\Get-WebView2.ps1
#    powershell -ExecutionPolicy Bypass -File lib\Get-WebView2.ps1 -Version 1.0.4129.50
# ============================================================
[CmdletBinding()]
param(
    [string]$Version = '1.0.4129.50',
    [string]$NupkgPath = ''    # optional: reuse a locally downloaded .nupkg
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$libDir = $PSScriptRoot
foreach ($f in @(
    (Join-Path $libDir 'Microsoft.Web.WebView2.Core.dll'),
    (Join-Path $libDir 'Microsoft.Web.WebView2.WinForms.dll'))) {
    if (Test-Path -LiteralPath $f -PathType Leaf) {
        Write-Host "[webview2] already present: $(Split-Path -Leaf $f) (delete lib\*.dll + lib\native to re-fetch)"
        return
    }
}

# 1. Obtain the package.
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("webview2-" + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($temp) | Out-Null
$zip = Join-Path $temp 'pkg.zip'
if ($NupkgPath) {
    Copy-Item -LiteralPath $NupkgPath -Destination $zip
} else {
    $url = "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/$Version"
    Write-Host "[webview2] GET $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
}
Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $temp 'pkg')

# 2. Locate the managed assemblies: prefer the newest .NET Framework TFM
#    (net462 > net45 > net35 > netstandard), then any match by name.
function Find-Managed([string]$name) {
    $all = Get-ChildItem -LiteralPath (Join-Path $temp 'pkg') -Recurse -Filter $name -File
    if (-not $all) { throw "Not found in package: $name" }
    foreach ($tfm in @('net462', 'net45', 'net40', 'netstandard2.0')) {
        $hit = $all | Where-Object { $_.FullName -like "*\$tfm\*" } | Select-Object -First 1
        if ($hit) { return $hit }
    }
    return ($all | Select-Object -First 1)
}
$core = Find-Managed 'Microsoft.Web.WebView2.Core.dll'
$winforms = Find-Managed 'Microsoft.Web.WebView2.WinForms.dll'
Copy-Item -LiteralPath $core.FullName -Destination $libDir -Force
Copy-Item -LiteralPath $winforms.FullName -Destination $libDir -Force
Write-Host "[webview2] $(Split-Path -Leaf $core.FullName) <- $($core.Directory.Name)"
Write-Host "[webview2] $(Split-Path -Leaf $winforms.FullName) <- $($winforms.Directory.Name)"

# 3. Native loader (x64 root copy is added by Build.ps1 itself).
foreach ($arch in @('x64', 'x86')) {
    $loader = Get-ChildItem -LiteralPath (Join-Path $temp 'pkg') -Recurse -Filter 'WebView2Loader.dll' -File |
        Where-Object { $_.FullName -like "*win-$arch*" } | Select-Object -First 1
    if (-not $loader) { throw "Native WebView2Loader.dll (win-$arch) not found in package." }
    $dst = Join-Path $libDir "native\$arch"
    [System.IO.Directory]::CreateDirectory($dst) | Out-Null
    Copy-Item -LiteralPath $loader.FullName -Destination $dst -Force
    Write-Host "[webview2] native\$arch\WebView2Loader.dll <- $($loader.Directory.Name)"
}

Remove-Item -LiteralPath $temp -Recurse -Force
Write-Host '[webview2] lib\ is ready; run scripts\Build.ps1 next.'
