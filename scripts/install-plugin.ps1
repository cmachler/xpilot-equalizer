<#
.SYNOPSIS
  Installs the XpilotAudioEqualizer plugin into xPilot's Plugins folder.
.DESCRIPTION
  Copies XpilotAudioEqualizer.dll plus the three System.Reflection.Emit*
  facade DLLs (resolved from the installed .NET 10 shared runtime) into
  %LOCALAPPDATA%\org.vatsim.xpilot\Plugins\XpilotAudioEqualizer\.
  Run after: dotnet publish src/XpilotAudioEqualizer/XpilotAudioEqualizer.csproj -c Release -o publish
#>
param(
  [string]$SourceDir = (Join-Path (Split-Path $PSScriptRoot -Parent) "publish"),
  [string]$PluginsRoot = (Join-Path $env:LOCALAPPDATA "org.vatsim.xpilot\Plugins")
)

$ErrorActionPreference = "Stop"
$dll = Join-Path $SourceDir "XpilotAudioEqualizer.dll"
if (-not (Test-Path $dll)) {
  throw "XpilotAudioEqualizer.dll not found in $SourceDir. Run dotnet publish first."
}

# Resolve the installed .NET 10 shared runtime directory.
$dotnetRoot = $null
if ($env:DOTNET_ROOT -and (Test-Path $env:DOTNET_ROOT)) {
  $dotnetRoot = $env:DOTNET_ROOT
} elseif (Get-Command dotnet -ErrorAction SilentlyContinue) {
  try { $dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source } catch { }
} 
if (-not $dotnetRoot -or -not (Test-Path $dotnetRoot)) {
  $dotnetRoot = "C:\Program Files\dotnet"
}
if (-not (Test-Path $dotnetRoot)) {
  throw "Installed .NET runtime not found (DOTNET_ROOT=$env:DOTNET_ROOT)."
}

$appRoot = Join-Path $dotnetRoot "shared\Microsoft.NETCore.App"
if (-not (Test-Path $appRoot)) {
  throw "Microsoft.NETCore.App shared runtime not found under $appRoot."
}
$runtimeDir = Get-ChildItem -Path $appRoot -Directory -Filter "10.*" |
  Sort-Object Name -Descending |
  Select-Object -First 1 -ExpandProperty FullName
if (-not $runtimeDir) {
  throw "No .NET 10.* shared runtime found under $appRoot."
}

$dest = Join-Path $PluginsRoot "XpilotAudioEqualizer"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item $dll (Join-Path $dest "XpilotAudioEqualizer.dll") -Force

$emitFiles = @(
  "System.Reflection.Emit.dll",
  "System.Reflection.Emit.ILGeneration.dll",
  "System.Reflection.Emit.Lightweight.dll",
  "System.Reflection.Primitives.dll"
)
foreach ($f in $emitFiles) {
  $src = Join-Path $runtimeDir $f
  if (-not (Test-Path $src)) {
    throw "Required facade not found in runtime: $src"
  }
  Copy-Item $src (Join-Path $dest $f) -Force
}

Write-Host "Installed to $dest\XpilotAudioEqualizer.dll"
Write-Host "Copied Emit facades from $runtimeDir"
Write-Host "Restart xPilot, then type .eq to open settings."