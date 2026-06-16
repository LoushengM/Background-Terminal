[CmdletBinding()]
param(
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Release',

  [ValidateSet('win-x64')]
  [string]$RuntimeIdentifier = 'win-x64',

  [string]$WixPath,

  [switch]$NoRestore,

  [switch]$SuppressValidation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-RepoRoot {
  Split-Path -Parent $PSScriptRoot
}

function Get-WixToolPath {
  param(
    [string]$RequestedPath
  )

  if ($RequestedPath) {
    if (-not (Test-Path -LiteralPath (Join-Path $RequestedPath 'candle.exe'))) {
      throw "WiX candle.exe was not found at '$RequestedPath'."
    }

    if (-not (Test-Path -LiteralPath (Join-Path $RequestedPath 'light.exe'))) {
      throw "WiX light.exe was not found at '$RequestedPath'."
    }

    return (Resolve-Path -LiteralPath $RequestedPath).Path
  }

  $candleCommand = Get-Command candle.exe -ErrorAction SilentlyContinue
  $lightCommand = Get-Command light.exe -ErrorAction SilentlyContinue
  if ($candleCommand -and $lightCommand) {
    $candlePath = Split-Path -Parent $candleCommand.Source
    $lightPath = Split-Path -Parent $lightCommand.Source
    if ($candlePath -eq $lightPath) {
      return $candlePath
    }
  }

  $candidatePaths = @(
    'C:\Program Files (x86)\WiX Toolset v3.11\bin'
    'C:\Program Files\WiX Toolset v3.11\bin'
    (Join-Path $env:TEMP 'codex-wix\wix.3.11.2\tools')
  )

  foreach ($candidatePath in $candidatePaths) {
    if ((Test-Path -LiteralPath (Join-Path $candidatePath 'candle.exe')) -and
        (Test-Path -LiteralPath (Join-Path $candidatePath 'light.exe'))) {
      return $candidatePath
    }
  }

  throw 'WiX v3.11 candle.exe and light.exe were not found. Install WiX or pass -WixPath to a local WiX v3.11 bin directory.'
}

function ConvertTo-WixIdentifier {
  param(
    [string]$Value,
    [string]$Prefix
  )

  $normalized = $Value -replace '[^A-Za-z0-9_]', '_'
  if ($normalized.Length -gt 0 -and $normalized[0] -match '^[0-9]$') {
    $normalized = "_$normalized"
  }

  return "$Prefix$normalized"
}

function New-DeterministicGuid {
  param(
    [string]$Value
  )

  $sha1 = [System.Security.Cryptography.SHA1]::Create()
  try {
    $hash = $sha1.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value))
  }
  finally {
    $sha1.Dispose()
  }

  [byte[]]$guidBytes = $hash[0..15]
  $guidBytes[7] = ($guidBytes[7] -band 0x0F) -bor 0x50
  $guidBytes[8] = ($guidBytes[8] -band 0x3F) -bor 0x80
  return [System.Guid]::new($guidBytes)
}

function Escape-WixAttribute {
  param(
    [string]$Value
  )

  return [System.Security.SecurityElement]::Escape($Value)
}

$repoRoot = Resolve-RepoRoot
$setupDir = $PSScriptRoot
$appProject = Join-Path $repoRoot 'Background-Terminal\Background-Terminal.csproj'
$appPublishDir = Join-Path $repoRoot "Background-Terminal\bin\$Configuration\net10.0-windows\$RuntimeIdentifier\publish"
$harvestPath = Join-Path $setupDir 'HarvestedFiles.wxs'
$objDir = Join-Path $setupDir "obj\$Configuration"
$msiPath = Join-Path $setupDir 'Background_Terminal_Setup.msi'

$env:NUGET_SIGNATURE_VERIFICATION = 'false'

Write-Host "Publishing $RuntimeIdentifier $Configuration output to $appPublishDir"
if (Test-Path -LiteralPath $appPublishDir) {
  Remove-Item -LiteralPath $appPublishDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $appPublishDir | Out-Null
$publishArgs = @(
  'publish', $appProject,
  '--configuration', $Configuration,
  '--runtime', $RuntimeIdentifier,
  '--self-contained', 'true',
  '-p:PublishSingleFile=true',
  '-p:IncludeNativeLibrariesForSelfExtract=true',
  '-p:ContinuousIntegrationBuild=true',
  '-p:Deterministic=true',
  '-p:DebugType=portable'
)
if ($NoRestore) {
  $publishArgs += '--no-restore'
}
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
  throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$files = Get-ChildItem -LiteralPath $appPublishDir -File | Sort-Object Name, FullName
$filteredFiles = $files | Where-Object { $_.Extension -ne '.pdb' }

$lines = @(
  '<?xml version="1.0" encoding="utf-8"?>'
  '<Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">'
  '  <Fragment>'
  '    <ComponentGroup Id="ProductComponents" Directory="INSTALLFOLDER">'
)

foreach ($file in $filteredFiles) {
  $relativePath = $file.Name
  $componentId = ConvertTo-WixIdentifier -Value $relativePath -Prefix 'Comp_'
  $fileId = ConvertTo-WixIdentifier -Value $relativePath -Prefix 'File_'
  $componentGuid = New-DeterministicGuid -Value "Background-Terminal-Setup|$relativePath"

  $lines += '      <Component Id="' + $componentId + '" Guid="' + $componentGuid + '" Win64="yes">'
  $lines += '        <File Id="' + $fileId + '" Name="' + (Escape-WixAttribute $file.Name) + '" Source="$(var.BuildSourceDir)\' + (Escape-WixAttribute $relativePath) + '" KeyPath="yes" />'
  $lines += '      </Component>'
}

$lines += @(
  '    </ComponentGroup>'
  '  </Fragment>'
  '</Wix>'
)

[System.IO.File]::WriteAllText($harvestPath, ($lines -join "`r`n") + "`r`n", [System.Text.UTF8Encoding]::new($false))
Write-Host "Regenerated $harvestPath without .pdb files."

$wixPathResolved = Get-WixToolPath -RequestedPath $WixPath
Write-Host "Using WiX tools from $wixPathResolved"

if (Test-Path -LiteralPath $objDir) {
  Remove-Item -LiteralPath $objDir -Recurse -Force
}
if (Test-Path -LiteralPath (Join-Path $setupDir 'bin')) {
  Remove-Item -LiteralPath (Join-Path $setupDir 'bin') -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $objDir | Out-Null

$candleExe = Join-Path $wixPathResolved 'candle.exe'
$lightExe = Join-Path $wixPathResolved 'light.exe'

$candleOut = $objDir + '\'
$candleArgs = @(
  '-nologo'
  '-arch', 'x64'
  '-dPlatform=x64'
  "-dBuildSourceDir=$appPublishDir"
  '-out', $candleOut
  (Join-Path $setupDir 'Product.wxs')
  $harvestPath
)
Push-Location $setupDir
try {
  & $candleExe @candleArgs
  if ($LASTEXITCODE -ne 0) {
    throw "candle.exe failed with exit code $LASTEXITCODE."
  }

  $lightArgs = @(
    '-nologo'
    '-out', $msiPath
    (Join-Path $objDir 'Product.wixobj')
    (Join-Path $objDir 'HarvestedFiles.wixobj')
  )
  if ($SuppressValidation) {
    $lightArgs = @('-sval') + $lightArgs
  }
  if (Test-Path -LiteralPath $msiPath) {
    Remove-Item -LiteralPath $msiPath -Force
  }
  & $lightExe @lightArgs
  if ($LASTEXITCODE -ne 0) {
    throw "light.exe failed with exit code $LASTEXITCODE."
  }
}
finally {
  Pop-Location
}

Write-Host "Built $msiPath"
