<#
.SYNOPSIS
  Installs the XpilotAudioEqualizer plugin into xPilot's Plugins folder.
.DESCRIPTION
  Copies XpilotAudioEqualizer.dll next to normalizer-config.json into
  %LOCALAPPDATA%\org.vatsim.xpilot\Plugins\XpilotAudioEqualizer\.
  Run after: dotnet publish src/XpilotAudioEqualizer/XpilotAudioEqualizer.csproj -c Release -o publish
#>
param(
  [string]$SourceDir = (Join-Path $PSScriptRoot "publish"),
  [string]$PluginsRoot = (Join-Path $env:LOCALAPPDATA "org.vatsim.xpilot\Plugins")
)

$ErrorActionPreference = "Stop"
$dll = Join-Path $SourceDir "XpilotAudioEqualizer.dll"
if (-not (Test-Path $dll)) {
  throw "XpilotAudioEqualizer.dll not found in $SourceDir. Run dotnet publish first."
}
$dest = Join-Path $PluginsRoot "XpilotAudioEqualizer"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item $dll (Join-Path $dest "XpilotAudioEqualizer.dll") -Force
Write-Host "Installed to $dest\XpilotAudioEqualizer.dll"
Write-Host "Restart xPilot, then type .eq to open settings."
