# Builds dsh-web-manager.exe with the OS-bundled .NET Framework C# compiler.
# No Visual Studio or NuGet required. Run on any Windows 10/11:
#   powershell -ExecutionPolicy Bypass -File scripts\Build.ps1
[CmdletBinding()]
param([switch]$SkipTests)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $projectRoot 'dist'
$assets = Join-Path $projectRoot 'assets'

$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
    $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
    throw '.NET Framework C# compiler (csc.exe) was not found.'
}
if (-not (Test-Path -LiteralPath (Join-Path $assets 'dsh-webui.ico'))) {
    throw 'assets\dsh-webui.ico is missing.'
}

if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
[System.IO.Directory]::CreateDirectory($dist) | Out-Null
$distAssets = Join-Path $dist 'assets'
[System.IO.Directory]::CreateDirectory($distAssets) | Out-Null

$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$references = @(
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll',
    '/reference:System.Management.dll'
)
$common = @('/nologo', '/noconfig', '/langversion:5', '/platform:anycpu', '/optimize+') + $references

Write-Host "Compiling with $csc ..."
& $csc @common /target:winexe "/win32icon:$assets\dsh-webui.ico" "/out:$dist\dsh-web-manager.exe" @sources
if ($LASTEXITCODE -ne 0) { throw "Application compilation failed with exit code $LASTEXITCODE." }

# Runtime layout next to the exe.
Copy-Item -LiteralPath (Join-Path $projectRoot 'config.example.json') -Destination $dist -Force
Copy-Item -LiteralPath (Join-Path $assets 'dsh-webui.ico') -Destination $distAssets -Force
Copy-Item -LiteralPath (Join-Path $assets 'dsh-webui.svg') -Destination $distAssets -Force
# v2.1: WSL-side companion scripts (wsl-start.sh self-heal launcher, wsl-bootstrap.sh mutual bootstrap).
$distWsl = Join-Path $dist 'wsl'
[System.IO.Directory]::CreateDirectory($distWsl) | Out-Null
if (Test-Path -LiteralPath (Join-Path $projectRoot 'scripts\wsl')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts\wsl\*.sh') -Destination $distWsl -Force
}
foreach ($optional in @('docs', 'LICENSE', 'README.md')) {
    if (Test-Path -LiteralPath (Join-Path $projectRoot $optional)) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $optional) -Destination $dist -Recurse -Force
    }
}
foreach ($optional in @('Install.ps1', 'Uninstall.ps1')) {
    if (Test-Path -LiteralPath (Join-Path $projectRoot (Join-Path 'scripts' $optional))) {
        Copy-Item -LiteralPath (Join-Path $projectRoot (Join-Path 'scripts' $optional)) -Destination $dist -Force
    }
}

Write-Host ''
Get-ChildItem -LiteralPath $dist | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "Build OK: $dist\dsh-web-manager.exe"