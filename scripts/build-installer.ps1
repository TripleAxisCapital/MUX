param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Standard", "Virtual")]
    [string]$Edition,
    [string]$SourceDir,
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\artifacts\installers")
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $SourceDir = if ($Edition -eq "Standard") {
        Join-Path $repoRoot "artifacts\MUX"
    } else {
        Join-Path $repoRoot "artifacts\MUX.Virtual"
    }
}

$SourceDir = (Resolve-Path $SourceDir).Path
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path

$iss = if ($Edition -eq "Standard") {
    Join-Path $repoRoot "installer\MUX-Standard.iss"
} else {
    Join-Path $repoRoot "installer\MUX-Virtual.iss"
}

$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
$isccCandidates = @(
    (Join-Path $programFilesX86 "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { $_ -and (Test-Path $_) }

if (-not $isccCandidates) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        $isccCandidates = @($command.Source)
    }
}

if (-not $isccCandidates) {
    throw "Inno Setup 6 compiler (ISCC.exe) was not found. Install Inno Setup 6 and retry."
}

$iscc = $isccCandidates | Select-Object -First 1
Write-Host "Building MUX $Edition installer with $iscc"

& $iscc "/DSourceDir=$SourceDir" "/DOutputDir=$OutputDir" $iss
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$expected = if ($Edition -eq "Standard") {
    Join-Path $OutputDir "MUX-Standard-Setup-x64.exe"
} else {
    Join-Path $OutputDir "MUX-Virtual-Setup-x64.exe"
}

if (-not (Test-Path $expected)) {
    throw "Installer build completed without producing $expected."
}

Write-Host "Installer: $expected"
