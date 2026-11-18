param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$slnPath = Join-Path $repoRoot 'GhCopilot.sln'
$rhinoProjectDir = Join-Path $repoRoot 'src\GhCopilot.Rhino'
$ghProjectDir = Join-Path $repoRoot 'src\GhCopilot.Gh'
$artifactsDir = Join-Path $repoRoot 'artifacts'
$stagingDir = Join-Path $artifactsDir 'yak-staging'
$yakOutputFile = Join-Path $artifactsDir "ghcopilot-$Version-rh8_0-any.yak"

$forbiddenHostDlls = @(
    'RhinoCommon.dll',
    'Grasshopper.dll',
    'GH_IO.dll',
    'Eto.dll',
    'Rhino.UI.dll',
    'Ed.Eto.dll'
)

function Assert-NoHostDlls {
    param([string]$DirectoryPath)

    if (-not (Test-Path $DirectoryPath)) {
        return
    }

    $found = Get-ChildItem -Path $DirectoryPath -Recurse -File |
        Where-Object { $forbiddenHostDlls -contains $_.Name }

    if ($found) {
        $names = ($found | ForEach-Object { $_.FullName }) -join '; '
        throw "Forbidden Rhino/Grasshopper host assemblies found in '$DirectoryPath': $names"
    }
}

function Copy-StageFolder {
    param(
        [string]$SourceTfm,
        [string]$DestDir
    )

    New-Item -ItemType Directory -Path $DestDir -Force | Out-Null

    foreach ($projDir in @($rhinoProjectDir, $ghProjectDir)) {
        $binDir = Join-Path $projDir "bin\$Configuration\$SourceTfm"
        if (-not (Test-Path $binDir)) {
            throw "Missing build output folder: $binDir"
        }

        Assert-NoHostDlls -DirectoryPath $binDir

        Get-ChildItem -Path $binDir -File | Where-Object {
            $_.Extension -in @('.rhp', '.gha', '.dll', '.pdb', '.json') -and
            ($forbiddenHostDlls -notcontains $_.Name)
        } | ForEach-Object {
            Copy-Item -Path $_.FullName -Destination (Join-Path $DestDir $_.Name) -Force
        }
    }

    Assert-NoHostDlls -DirectoryPath $DestDir
}

# Ensure user-local .NET SDK takes precedence when available.
$userDotNet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path (Join-Path $userDotNet 'dotnet.exe')) {
    $env:PATH = "$userDotNet;$env:PATH"
}

Write-Output "Building $slnPath ($Configuration)..."
dotnet build $slnPath -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

if (Test-Path $stagingDir) {
    Remove-Item -Path $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

$manifestContent = @"
name: ghcopilot
version: $Version
authors:
- GH Copilot Team
description: AI copilot for Grasshopper and Rhino 8.
url: https://example.local/ghcopilot
keywords:
- grasshopper
- ai
- copilot
"@
Set-Content -Path (Join-Path $stagingDir 'manifest.yml') -Value $manifestContent -Encoding UTF8

Copy-StageFolder -SourceTfm 'net48' -DestDir (Join-Path $stagingDir 'net48')
Copy-StageFolder -SourceTfm 'net7.0-windows' -DestDir (Join-Path $stagingDir 'net7.0')

Assert-NoHostDlls -DirectoryPath $stagingDir

if (Test-Path $yakOutputFile) {
    Remove-Item -Path $yakOutputFile -Force
}

$yakExe = "C:\Program Files\Rhino 8\System\Yak.exe"
if (Test-Path $yakExe) {
    Push-Location $stagingDir
    try {
        & $yakExe build --platform win
        $builtYak = Get-ChildItem -Path $stagingDir -Filter '*.yak' | Select-Object -First 1
        if ($builtYak) {
            Move-Item -Path $builtYak.FullName -Destination $yakOutputFile -Force
        }
    }
    finally {
        Pop-Location
    }
}

if (-not (Test-Path $yakOutputFile)) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stagingDir, $yakOutputFile)
}

Get-ChildItem -Path $stagingDir -Recurse -File | Unblock-File
Unblock-File -Path $yakOutputFile

Write-Output "Multi-target Yak staging folder: $stagingDir"
Write-Output "Built Yak package: $yakOutputFile"
