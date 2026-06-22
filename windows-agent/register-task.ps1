<#
.SYNOPSIS
  Register the notify-bridge agent to run hidden at logon via Task Scheduler.

.DESCRIPTION
  Resolves the published agent exe, writes a tiny VBS launcher next to it (so no
  console window flashes at logon), and registers a per-user logon task.

.PARAMETER ExePath
  Path to NotifyBridgeAgent.exe. Defaults to the Release publish output.

.EXAMPLE
  # From the windows-agent directory, after:
  #   dotnet publish -c Release -r win-x64 --self-contained false
  .\register-task.ps1
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [string]$TaskName = "NotifyBridgeAgent"
)

$ErrorActionPreference = "Stop"

if (-not $ExePath) {
    $candidates = @(
        ".\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\NotifyBridgeAgent.exe",
        ".\bin\Release\net8.0-windows10.0.19041.0\win-x64\NotifyBridgeAgent.exe",
        ".\bin\Release\net8.0-windows10.0.19041.0\NotifyBridgeAgent.exe"
    )
    $ExePath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $ExePath) {
        throw "Could not find NotifyBridgeAgent.exe. Run 'dotnet publish -c Release -r win-x64 --self-contained false' first, or pass -ExePath."
    }
}

$ExePath = (Resolve-Path $ExePath).Path
$exeDir = Split-Path -Parent $ExePath
Write-Host "Agent exe: $ExePath"

# Remind about config.json (holds endpoint + token).
$cfg = Join-Path $exeDir "config.json"
if (-not (Test-Path $cfg)) {
    Write-Warning "No config.json next to the exe. Copy config.example.json -> config.json and set endpoint/token."
}

# VBS launcher: starts the exe with a hidden window.
$vbsPath = Join-Path $exeDir "launch-hidden.vbs"
$vbs = @"
Set sh = CreateObject("WScript.Shell")
sh.Run """$ExePath""", 0, False
"@
Set-Content -Path $vbsPath -Value $vbs -Encoding ASCII
Write-Host "Launcher: $vbsPath"

# (Re)register the logon task.
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

$action   = New-ScheduledTaskAction -Execute "wscript.exe" -Argument """$vbsPath"""
$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Settings $settings -Principal $principal -Description "Forward Windows notifications to the Linux host" | Out-Null

Write-Host "Registered scheduled task '$TaskName' (runs hidden at logon)."
Write-Host "Start it now with:  Start-ScheduledTask -TaskName $TaskName"
