# Builds AIUsage.exe with the C# compiler that ships inside Windows (.NET Framework 4.x),
# then the installer dist\AIUsage-Setup-<version>.exe with NSIS (if makensis is available).
#
#   powershell -ExecutionPolicy Bypass -File .\build.ps1             exe + installer
#   powershell -ExecutionPolicy Bypass -File .\build.ps1 -NoInstaller
#
# Code signing (optional): set SIGN_PFX_BASE64 and SIGN_PFX_PASSWORD (a PFX code-signing
# certificate, base64) and both AIUsage.exe and the installer are signed with signtool.

[CmdletBinding()]
param([switch] $NoInstaller)

$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$plugin = Join-Path $root 'com.saeedsh.aiusage.sdPlugin'
$exe    = Join-Path $plugin 'AIUsage.exe'
$dist   = Join-Path $root 'dist'

$version = (Get-Content (Join-Path $plugin 'manifest.json') -Raw | ConvertFrom-Json).Version

# ---- compile -------------------------------------------------------------
$csc = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'csc.exe (.NET Framework 4.x) was not found on this PC.' }

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$arguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', "/out:$exe",
               "/win32icon:$(Join-Path $root 'installer\app.ico')",
               '/reference:System.Web.Extensions.dll', '/reference:System.Drawing.dll',
               '/reference:System.Management.dll') + $sources
& $csc @arguments
if ($LASTEXITCODE -ne 0) { throw "compilation failed ($LASTEXITCODE)" }
Write-Output "built $exe"

# ---- optional signing ----------------------------------------------------
function Invoke-Sign([string] $file) {
    if (-not $env:SIGN_PFX_BASE64) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found (Windows SDK).' }
    $pfx = Join-Path $env:TEMP 'aiusage-sign.pfx'
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:SIGN_PFX_BASE64))
    try {
        & $signtool.FullName sign /f $pfx /p $env:SIGN_PFX_PASSWORD /fd SHA256 /td SHA256 `
            /tr http://timestamp.digicert.com /d 'AI Usage for StreamDock' $file
        if ($LASTEXITCODE -ne 0) { throw "signing $file failed" }
    } finally { Remove-Item $pfx -Force }
    Write-Output "signed $file"
}
Invoke-Sign $exe

if ($NoInstaller) { return }

# ---- installer -----------------------------------------------------------
$makensis = (Get-Command makensis -ErrorAction SilentlyContinue).Source
if (-not $makensis) {
    $makensis = @("${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe") |
                Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $makensis) { Write-Warning 'NSIS (makensis) not found - skipping the installer. Install it from https://nsis.sourceforge.io'; return }

New-Item -ItemType Directory -Force $dist | Out-Null
Push-Location (Join-Path $root 'installer')
try {
    & $makensis /V2 "/DVERSION=$version" "/DSRC=$plugin" "/DOUT=$dist" AIUsage.nsi
    if ($LASTEXITCODE -ne 0) { throw "makensis failed ($LASTEXITCODE)" }
} finally { Pop-Location }
$setup = Join-Path $dist "AIUsage-Setup-$version.exe"
Invoke-Sign $setup
Write-Output "built $setup"
