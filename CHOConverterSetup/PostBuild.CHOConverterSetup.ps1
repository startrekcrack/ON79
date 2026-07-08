[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$MsiPath,

  [Parameter(Mandatory = $true)]
  [string]$TargetDir,

  [Parameter(Mandatory = $true)]
  [string]$CHOConverterOutputDir,

  [string]$SignToolExe,
  [string]$CabarcExe,
  [string]$TimeStampUrl,
  [string]$SigningCertSha1
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Info([string]$Message) {
  Write-Host "PostBuild: $Message" -ForegroundColor Cyan
}

if (-not (Test-Path -LiteralPath $MsiPath)) {
  throw "MSI not found: $MsiPath"
}

if (-not (Test-Path -LiteralPath $TargetDir)) {
  throw "TargetDir not found: $TargetDir"
}

$msiBaseName = [System.IO.Path]::GetFileNameWithoutExtension($MsiPath)
$cabPath = Join-Path $TargetDir "$msiBaseName.cab"
$versionXml = Join-Path $CHOConverterOutputDir 'choconvertersetupversion.xml'
$versionJson = Join-Path $CHOConverterOutputDir 'choconvertersetupversion.json'

if ($SignToolExe -and (Test-Path -LiteralPath $SignToolExe)) {
  if (-not $TimeStampUrl) {
    throw 'TimeStampUrl is empty.'
  }
  if (-not $SigningCertSha1) {
    throw 'SigningCertSha1 is empty.'
  }

  Write-Info 'signing MSI'
  & $SignToolExe sign /v /tr $TimeStampUrl /fd sha256 /td sha256 /sha1 $SigningCertSha1 $MsiPath
  if ($LASTEXITCODE -ne 0) { throw "signtool MSI failed with exit code $LASTEXITCODE" }
} else {
  Write-Info 'skipping MSI signing (signtool not found)'
}

if ($CabarcExe -and (Test-Path -LiteralPath $CabarcExe)) {
  Write-Info 'creating CAB'
  & $CabarcExe n $cabPath $MsiPath
  if ($LASTEXITCODE -ne 0) { throw "cabarc failed with exit code $LASTEXITCODE" }

  if ($SignToolExe -and (Test-Path -LiteralPath $SignToolExe)) {
    Write-Info 'signing CAB'
    & $SignToolExe sign /v /tr $TimeStampUrl /fd sha256 /td sha256 /sha1 $SigningCertSha1 $cabPath
    if ($LASTEXITCODE -ne 0) { throw "signtool CAB failed with exit code $LASTEXITCODE" }
  } else {
    Write-Info 'skipping CAB signing (signtool not found)'
  }
} else {
  Write-Info 'skipping CAB creation (cabarc not found)'
}

Write-Info 'copying version files beside MSI (if present)'
if (Test-Path -LiteralPath $versionXml) {
  Copy-Item -LiteralPath $versionXml -Destination $TargetDir -Force
}
if (Test-Path -LiteralPath $versionJson) {
  Copy-Item -LiteralPath $versionJson -Destination $TargetDir -Force
}
