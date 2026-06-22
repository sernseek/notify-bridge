<#
.SYNOPSIS
  Build, sign, and install the agent as an MSIX so it has package identity and
  UserNotificationListener.NotificationChanged fires (event-driven, no polling).

.DESCRIPTION
  Publishes the exe, stages it with the manifest + generated logo assets, packs
  with MakeAppx, signs with a self-signed dev cert, trusts the cert, and installs
  the package. Self-elevates for the trust + install steps.

  Requires the Windows 10/11 SDK (MakeAppx.exe + SignTool.exe).

.PARAMETER NoInstall
  Build and sign only; skip trusting the cert and installing (no admin needed).

.EXAMPLE
  .\build-msix.ps1
#>
#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Rid = "win-x64",
    [string]$CertSubject = "CN=notify-bridge-dev",
    [switch]$NoInstall
)
$ErrorActionPreference = "Stop"

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
        [Security.Principal.WindowsBuiltinRole]::Administrator)
}

# Trusting the cert and Add-AppxPackage need admin; self-elevate once.
if (-not $NoInstall -and -not (Test-Admin)) {
    Write-Host "Elevating for cert trust + install..."
    Start-Process powershell -Verb RunAs -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"",
        "-Configuration", $Configuration, "-Rid", $Rid, "-CertSubject", "`"$CertSubject`""
    )
    return
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here

Write-Host "[0/7] stop running agents + clear loose autostart"
# A running agent locks the publish DLL and makes 'dotnet publish' fail; kill any
# instance (loose or packaged) first. Also drop the unpackaged HKCU Run autostart
# — the MSIX StartupTask replaces it, and the loose exe would relock + run without
# package identity.
Get-Process NotifyBridgeAgent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name NotifyBridgeAgent -ErrorAction SilentlyContinue

Write-Host "[1/7] publish"
dotnet publish -c $Configuration -r $Rid --self-contained false | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE) - is an agent still running and locking files?" }
$publish = Join-Path $here "bin\$Configuration\net10.0-windows10.0.19041.0\$Rid\publish"
if (-not (Test-Path $publish)) { throw "publish dir not found: $publish" }

Write-Host "[2/7] stage"
$stage = Join-Path $here "msix-stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item "$publish\*" $stage -Recurse -Force
# The package must not carry the dev config; the agent reads config from
# %USERPROFILE%\.notify-bridge\config.json at runtime.
Remove-Item (Join-Path $stage "config.json") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $stage "config.example.json") -ErrorAction SilentlyContinue
Copy-Item (Join-Path $here "Package\AppxManifest.xml") (Join-Path $stage "AppxManifest.xml") -Force

Write-Host "[3/7] assets"
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $stage "Assets"
New-Item -ItemType Directory -Path $assets | Out-Null
function New-Logo([string]$Path, [int]$W, [int]$H) {
    $bmp = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 30, 30, 46))
    $g.Dispose()
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
New-Logo (Join-Path $assets "Square44x44Logo.png") 44 44
New-Logo (Join-Path $assets "Square150x150Logo.png") 150 150
New-Logo (Join-Path $assets "StoreLogo.png") 50 50

Write-Host "[4/7] locate SDK tools"
$kit = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$makeappx = Get-ChildItem "$kit\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
$signtool = Get-ChildItem "$kit\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw "makeappx.exe not found - install the Windows 10/11 SDK" }
if (-not $signtool) { throw "signtool.exe not found - install the Windows 10/11 SDK" }

Write-Host "[5/7] pack"
$msix = Join-Path $here "NotifyBridgeAgent.msix"
if (Test-Path $msix) { Remove-Item $msix -Force }
& $makeappx.FullName pack /d $stage /p $msix /o | Out-Host
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }

Write-Host "[6/7] cert + sign"
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $CertSubject } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $CertSubject `
        -KeyUsage DigitalSignature -FriendlyName "notify-bridge dev signing" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    Write-Host "  created self-signed cert $($cert.Thumbprint)"
}
& $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint $msix | Out-Host
if ($LASTEXITCODE -ne 0) { throw "signtool failed" }

if ($NoInstall) {
    Write-Host "built + signed (no install): $msix"
    return
}

Write-Host "[7/7] trust cert + install"
$cer = Join-Path $env:TEMP "notify-bridge-dev.cer"
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
Add-AppxPackage -Path $msix
Write-Host ""
Write-Host "installed. Next:"
Write-Host "  1) put config.json in %USERPROFILE%\.notify-bridge\ (endpoint/port/token)"
Write-Host "  2) launch 'Notify Bridge Agent' once from the Start menu to grant"
Write-Host "     notification access; it then auto-starts at logon (StartupTask)."
