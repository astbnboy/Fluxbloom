<#
.SYNOPSIS
  Installs Fluxbloom as your Windows screensaver.

.DESCRIPTION
  Default (no admin needed): copies Fluxbloom.scr to %LOCALAPPDATA%\Fluxbloom and points your user's
  screensaver setting (HKCU\Control Panel\Desktop\SCRNSAVE.EXE) at it. Windows will run it on idle.

  -System (needs an elevated PowerShell): copies Fluxbloom.scr to C:\Windows\System32 so it also appears by
  name in the "Screen Saver Settings" drop-down list.

  Nothing here touches GPU clocks, power limits, drivers, or your lock-screen/"on resume" setting.

.PARAMETER TimeoutMinutes
  Optional idle time before the screensaver starts. Leave at 0 to keep your current timeout.

.EXAMPLE
  .\install.ps1 -TimeoutMinutes 10
#>
param(
    [switch]$System,
    [int]$TimeoutMinutes = 0,
    [string]$Source = (Join-Path $PSScriptRoot 'dist\Fluxbloom.scr')
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Source)) { throw "Fluxbloom.scr not found at '$Source'. Run .\build.ps1 first." }

if ($System) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) { throw "-System needs an elevated (Run as administrator) PowerShell." }
    $target = Join-Path $env:SystemRoot 'System32\Fluxbloom.scr'
} else {
    $dir = Join-Path $env:LOCALAPPDATA 'Fluxbloom'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $target = Join-Path $dir 'Fluxbloom.scr'
}

Copy-Item $Source $target -Force
$key = 'HKCU:\Control Panel\Desktop'
Set-ItemProperty -Path $key -Name 'SCRNSAVE.EXE' -Value $target
Set-ItemProperty -Path $key -Name 'ScreenSaveActive' -Value '1'
if ($TimeoutMinutes -gt 0) { Set-ItemProperty -Path $key -Name 'ScreenSaveTimeOut' -Value ([string]($TimeoutMinutes * 60)) }

# ask Windows to re-read the per-user setting now
Start-Process rundll32.exe -ArgumentList 'user32.dll,UpdatePerUserSystemParameters' -Wait -WindowStyle Hidden

Write-Host "Installed: $target"
Write-Host "Configure:   & '$target' /c        (or Settings > Personalization > Lock screen > Screen saver settings > Settings...)"
Write-Host "Test now:    & '$target' /s        (move the mouse or press a key to exit)"
Write-Host "Note: the 'On resume, display logon screen' checkbox in Screen Saver Settings is a separate Windows setting; this script does not change it."
