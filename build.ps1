<#
.SYNOPSIS
  Builds dist\Fluxbloom.scr (single file) from source.
.DESCRIPTION
  Requires the .NET 8 SDK. If you used the portable SDK that lives in .\tools\dotnet (git-ignored), it is used automatically.
  Default output is framework-dependent (~2.3 MB, needs the .NET 8 Desktop Runtime x64 on the PC).
  -SelfContained bundles the runtime (~150 MB, runs on any x64 Windows 10/11 without installing .NET).
#>
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$local = Join-Path $PSScriptRoot 'tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path $local) { $env:DOTNET_ROOT = Join-Path $PSScriptRoot 'tools\dotnet'; $local } else { 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

Remove-Item publish, dist -Recurse -Force -ErrorAction SilentlyContinue
$sc = if ($SelfContained) { 'true' } else { 'false' }
& $dotnet publish -c Release -r win-x64 --self-contained $sc -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -p:EnableCompressionInSingleFile=$sc -o publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

New-Item -ItemType Directory -Force dist | Out-Null
Copy-Item publish\Fluxbloom.exe dist\Fluxbloom.scr -Force
Copy-Item publish\Fluxbloom.exe dist\Fluxbloom.exe -Force
Write-Host ("Built dist\Fluxbloom.scr ({0:N1} MB). Test: .\dist\Fluxbloom.exe /w   Install: .\install.ps1" -f ((Get-Item dist\Fluxbloom.scr).Length / 1MB))
