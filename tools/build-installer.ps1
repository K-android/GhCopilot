param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$userDotNet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path (Join-Path $userDotNet 'dotnet.exe')) {
    $env:PATH = "$userDotNet;$env:PATH"
}

# 1. Build the solution and multi-target Yak package first (embedded inside the installer EXE).
& (Join-Path $PSScriptRoot 'build-yak.ps1') -Configuration $Configuration -Version $Version
if ($LASTEXITCODE -ne 0) {
    throw "build-yak.ps1 failed."
}

$installerProject = Join-Path $PSScriptRoot 'Installer\GhCopilot.Installer.csproj'
Write-Output "Building standalone Windows installer ($installerProject)..."
dotnet build $installerProject -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Installer build failed with exit code $LASTEXITCODE."
}

$builtExe = Join-Path $PSScriptRoot "Installer\bin\$Configuration\net48\GhCopilot-Setup-$Version.exe"
if (-not (Test-Path $builtExe)) {
    throw "Expected installer binary not found at '$builtExe'."
}

$artifactsDir = Join-Path $repoRoot 'artifacts'
$distDir = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

$yakSource = Join-Path $artifactsDir "ghcopilot-$Version-rh8_0-any.yak"
$exeArtifact = Join-Path $artifactsDir "GhCopilot-Setup-$Version.exe"
$exeDist = Join-Path $distDir "GhCopilot-Setup-$Version.exe"
$yakDist = Join-Path $distDir "ghcopilot-$Version-rh8_0-any.yak"

Copy-Item -Path $builtExe -Destination $exeArtifact -Force
Copy-Item -Path $builtExe -Destination $exeDist -Force
Copy-Item -Path $yakSource -Destination $yakDist -Force

Get-ChildItem -Path $distDir -File | Unblock-File
Unblock-File -Path $exeArtifact

Write-Output "Standalone installer ready: $exeDist"
Write-Output "Yak package ready: $yakDist"
