<#
.SYNOPSIS
  Builds dist\XpilotAudioEqualizer.msi for the xPilot Audio Normalizer plugin.
.DESCRIPTION
  Publishes the plugin, stages the DLL plus the four Emit facade DLLs resolved
  from the installed .NET 10 shared runtime into dist\XpilotAudioEqualizer\,
  ensures the WiX v4 CLI is available, and invokes `wix build`.
#>
param(
  [string]$Configuration = "Release",
  [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\XpilotAudioEqualizer\XpilotAudioEqualizer.csproj"
$publishDir = Join-Path $root "publish"
$distDir = Join-Path $root "dist"
$stageDir = Join-Path $distDir "XpilotAudioEqualizer"
$wxs = Join-Path $root "installer\XpilotAudioEqualizer.wxs"
$msi = Join-Path $distDir "XpilotAudioEqualizer.msi"

# 1. Publish plugin.
Write-Host "Publishing plugin..."
& dotnet publish $project -c $Configuration -o $publishDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# 2. Resolve .NET 10 shared runtime.
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

# 3. Stage files.
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
$dll = Join-Path $publishDir "XpilotAudioEqualizer.dll"
if (-not (Test-Path $dll)) { throw "XpilotAudioEqualizer.dll not found in $publishDir." }
Copy-Item $dll (Join-Path $stageDir "XpilotAudioEqualizer.dll") -Force

$emitFiles = @(
  "System.Reflection.Emit.dll",
  "System.Reflection.Emit.ILGeneration.dll",
  "System.Reflection.Emit.Lightweight.dll",
  "System.Reflection.Primitives.dll"
)
foreach ($f in $emitFiles) {
  $src = Join-Path $runtimeDir $f
  if (-not (Test-Path $src)) { throw "Required facade not found in runtime: $src" }
  Copy-Item $src (Join-Path $stageDir $f) -Force
}
Write-Host "Staged files in $stageDir (facades from $runtimeDir)"

# 4. Ensure WiX CLI.
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
  Write-Host "WiX CLI not found; installing..."
  & dotnet tool install --global wix
  if ($LASTEXITCODE -ne 0) { throw "Failed to install WiX CLI. Run: dotnet tool install --global wix" }
}
# accept EULA non-interactively if needed
& wix eula accept wix7 2>$null | Out-Null

# ensure util extension cached
$ext = & wix extension list -g 2>$null
if ($ext -notmatch "WixToolset.Util.wixext") {
  & wix extension add -g WixToolset.Util.wixext | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "Failed to cache WixToolset.Util.wixext." }
}
if ($ext -notmatch "WixToolset.UI.wixext") {
  & wix extension add -g WixToolset.UI.wixext | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "Failed to cache WixToolset.UI.wixext." }
}

# 5. Build MSI.
Write-Host "Building MSI..."
& wix build $wxs -d "SourceDir=$stageDir" -d "Version=$Version" -o $msi -ext WixToolset.Util.wixext -ext WixToolset.UI.wixext
if ($LASTEXITCODE -ne 0) { throw "wix build failed." }
if (-not (Test-Path $msi)) { throw "MSI not produced: $msi" }
$size = (Get-Item $msi).Length
if ($size -le 0) { throw "MSI is empty: $msi" }

Write-Host ""
Write-Host "Built: $msi ($size bytes)"
Write-Host "Install:   msiexec /i `"$msi`" /qn"
Write-Host "Uninstall: msiexec /x `"$msi`" /qn"
Write-Host "Or uninstall via Add/Remove Programs (xPilot Audio Normalizer)."
