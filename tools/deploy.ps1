param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('net48', 'net7.0-windows')]
    [string]$TargetFramework = 'net48'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$rhinoProjectDir = Join-Path $repoRoot 'src\GhCopilot.Rhino'
$ghProjectDir = Join-Path $repoRoot 'src\GhCopilot.Gh'

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

function Copy-BuildArtifacts {
    param(
        [string]$SourceDir,
        [string]$DestDir
    )

    if (-not (Test-Path $SourceDir)) {
        throw "Build output directory does not exist: '$SourceDir'. Run 'dotnet build GhCopilot.sln -c $Configuration' first."
    }

    Assert-NoHostDlls -DirectoryPath $SourceDir

    if (-not (Test-Path $DestDir)) {
        New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
    }

    $items = Get-ChildItem -Path $SourceDir -File | Where-Object {
        $_.Extension -in @('.rhp', '.gha', '.dll', '.pdb', '.json') -and
        ($forbiddenHostDlls -notcontains $_.Name)
    }

    foreach ($item in $items) {
        Copy-Item -Path $item.FullName -Destination (Join-Path $DestDir $item.Name) -Force
    }
}

function Deploy-FrameworkPair {
    param(
        [string]$SourceTfm,
        [string]$DestDir
    )

    if (Test-Path $DestDir) {
        Remove-Item -Path $DestDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $DestDir -Force | Out-Null

    $rhinoBin = Join-Path $rhinoProjectDir "bin\$Configuration\$SourceTfm"
    $ghBin = Join-Path $ghProjectDir "bin\$Configuration\$SourceTfm"

    Copy-BuildArtifacts -SourceDir $rhinoBin -DestDir $DestDir
    Copy-BuildArtifacts -SourceDir $ghBin -DestDir $DestDir

    Assert-NoHostDlls -DirectoryPath $DestDir
}

# 1. Deploy multi-target Yak package structure into %APPDATA%\McNeel\Rhinoceros\packages\8.0\GhCopilot\0.1.0\
$yakRoot = Join-Path $env:APPDATA 'McNeel\Rhinoceros\packages\8.0\GhCopilot\0.1.0'
if (Test-Path $yakRoot) {
    Remove-Item -Path $yakRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $yakRoot -Force | Out-Null

$manifestContent = @"
name: GhCopilot
version: 0.1.0
authors:
- GH Copilot Team
description: AI copilot for Grasshopper and Rhino 8.
keywords:
- grasshopper
- ai
- copilot
"@
Set-Content -Path (Join-Path $yakRoot 'manifest.yml') -Value $manifestContent -Encoding UTF8

Deploy-FrameworkPair -SourceTfm 'net48' -DestDir (Join-Path $yakRoot 'net48')
Deploy-FrameworkPair -SourceTfm 'net7.0-windows' -DestDir (Join-Path $yakRoot 'net7.0')

Get-ChildItem -Path $yakRoot -Recurse -File | Unblock-File

# 2. Deploy selected framework build into %APPDATA%\Grasshopper\Libraries\GhCopilot\
$ghLibDir = Join-Path $env:APPDATA 'Grasshopper\Libraries\GhCopilot'
Deploy-FrameworkPair -SourceTfm $TargetFramework -DestDir $ghLibDir
Get-ChildItem -Path $ghLibDir -Recurse -File | Unblock-File

Write-Output "Deployed multi-target Yak package to: $yakRoot"
Write-Output "Deployed $TargetFramework build to: $ghLibDir"
Write-Output "Note: On the target Rhino 8 machine, keep ONLY one active copy (either the Yak package folder OR Grasshopper\Libraries\GhCopilot) to avoid duplicate GH_AssemblyInfo ID warnings."
