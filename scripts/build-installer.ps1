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

# Inno Setup is stricter about ICO containers than WPF/MSBuild. Extract the
# icon Windows actually embedded in the published executable and save a clean
# ICO for the installer executable and Installed Apps surface.
$mainExe = if ($Edition -eq "Standard") {
    Join-Path $SourceDir "MUX.exe"
} else {
    Join-Path $SourceDir "MUX.Virtual.exe"
}
if (-not (Test-Path $mainExe)) {
    throw "Published application executable was not found: $mainExe"
}

Add-Type -AssemblyName System.Drawing
$setupIconPath = Join-Path $OutputDir "mux-setup.ico"
$icon = [System.Drawing.Icon]::ExtractAssociatedIcon($mainExe)
if (-not $icon) {
    throw "Windows could not extract the embedded MUX icon from $mainExe."
}
try {
    $stream = [System.IO.File]::Create($setupIconPath)
    try {
        $icon.Save($stream)
    } finally {
        $stream.Dispose()
    }
} finally {
    $icon.Dispose()
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

& $iscc "/DSourceDir=$SourceDir" "/DOutputDir=$OutputDir" "/DSetupIconPath=$setupIconPath" $iss
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
