<#
.SYNOPSIS
  Removes Fluxbloom as the active screensaver and deletes the installed copy.
.PARAMETER Purge
  Also deletes your saved settings and logs (%APPDATA%\Fluxbloom).
#>
param([switch]$Purge)
$ErrorActionPreference = 'Stop'

$key = 'HKCU:\Control Panel\Desktop'
$current = (Get-ItemProperty -Path $key -Name 'SCRNSAVE.EXE' -ErrorAction SilentlyContinue).'SCRNSAVE.EXE'
if ($current -and $current -like '*Fluxbloom.scr') {
    Remove-ItemProperty -Path $key -Name 'SCRNSAVE.EXE' -ErrorAction SilentlyContinue   # back to "(None)"
    Start-Process rundll32.exe -ArgumentList 'user32.dll,UpdatePerUserSystemParameters' -Wait -WindowStyle Hidden
    Write-Host "Cleared the active screensaver setting that pointed at Fluxbloom."
}

$userCopy = Join-Path $env:LOCALAPPDATA 'Fluxbloom\Fluxbloom.scr'
if (Test-Path $userCopy) { Remove-Item $userCopy -Force; Write-Host "Deleted $userCopy" }

$sysCopy = Join-Path $env:SystemRoot 'System32\Fluxbloom.scr'
if (Test-Path $sysCopy) {
    try { Remove-Item $sysCopy -Force; Write-Host "Deleted $sysCopy" }
    catch { Write-Warning "Could not delete $sysCopy (run elevated to remove the system-wide copy)." }
}

if ($Purge) {
    $data = Join-Path $env:APPDATA 'Fluxbloom'
    if (Test-Path $data) { Remove-Item $data -Recurse -Force; Write-Host "Deleted $data" }
}
