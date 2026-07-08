[CmdletBinding()]
param(
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Release',

  [ValidateSet('x64')]
  [string]$Platform = 'x64',

  [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$wixDir    = $PSScriptRoot
$setupRoot = Split-Path -Parent $wixDir     # CHOConverter\
$wixproj   = Join-Path $wixDir 'CHOConverterSetup.wixproj'

if (-not (Test-Path $wixproj)) {
  throw "WiX project not found: $wixproj"
}

if (-not $SkipBuild) {
  $cliProj = Join-Path $setupRoot 'CHOConverterCLI\CHOConverterCLI.csproj'
  $guiProj = Join-Path $setupRoot 'CHOConverterGUI\CHOConverterGUI.csproj'
  $wpfProj = Join-Path $setupRoot 'CHOConverterWPF\CHOConverterWPF.csproj'

  Write-Host "Building CHOConverter projects ($Configuration)..." -ForegroundColor Cyan
  foreach ($proj in @($cliProj, $guiProj, $wpfProj)) {
    if (Test-Path $proj) {
      & dotnet build $proj -c $Configuration
      if ($LASTEXITCODE -ne 0) { throw "dotnet build failed: $proj" }
    }
  }

  # Combine CLI + GUI + WPF outputs into a single folder for WiX harvesting.
  $combinedDir = Join-Path $setupRoot "bin\Combined\$Configuration\net10.0-windows"
  Write-Host "Combining outputs to: $combinedDir" -ForegroundColor Cyan
  New-Item -ItemType Directory -Force -Path $combinedDir | Out-Null

  foreach ($srcSuffix in @('CHOConverterCLI', 'CHOConverterGUI', 'CHOConverterWPF')) {
    $src = Join-Path $setupRoot "$srcSuffix\bin\$Configuration\net10.0-windows"
    if (Test-Path $src) {
      Copy-Item "$src\*" -Destination $combinedDir -Recurse -Force
    }
  }
} else {
  Write-Host "Skipping app build; using existing combined output." -ForegroundColor Yellow
}

Write-Host "Building WiX setup ($Configuration|$Platform)..." -ForegroundColor Cyan
& dotnet build $wixproj -p:Configuration=$Configuration -p:Platform=$Platform --no-incremental
if ($LASTEXITCODE -ne 0) { throw "dotnet build WiX failed with exit code $LASTEXITCODE" }

Write-Host "OK: CHOConverterSetup_$Platform.msi" -ForegroundColor Green
