$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$appPublish = Join-Path $root 'artifacts\MUX.Virtual'
$driverProject = Join-Path $root 'driver\MUX.Virtual.Display\MUX.Virtual.Display.vcxproj'
$driverBuildRoot = Join-Path $root 'driver\MUX.Virtual.Display\bin\x64\Release'
$driverPublish = Join-Path $appPublish 'Driver'

Push-Location $root
try {
    Write-Host 'Validating MUX.Core...' -ForegroundColor Cyan
    dotnet run --project .\tests\MUX.Core.Tests\MUX.Core.Tests.csproj -c Release

    Write-Host 'Building MUX Virtual controller...' -ForegroundColor Cyan
    dotnet restore .\src\MUX.Virtual.App\MUX.Virtual.App.csproj
    dotnet build .\src\MUX.Virtual.App\MUX.Virtual.App.csproj -c Release --no-restore

    if (Test-Path $appPublish) { Remove-Item $appPublish -Recurse -Force }
    dotnet publish .\src\MUX.Virtual.App\MUX.Virtual.App.csproj `
        -c Release -r win-x64 --self-contained true -o $appPublish

    if (-not (Get-Command nuget -ErrorAction SilentlyContinue)) {
        throw 'nuget.exe is required to restore the Windows Driver Kit packages.'
    }

    if (-not (Get-Command msbuild -ErrorAction SilentlyContinue)) {
        throw 'MSBuild with Visual Studio 2026 C++ build tools is required to build the MUX virtual display driver.'
    }

    Write-Host 'Restoring Windows Driver Kit packages...' -ForegroundColor Cyan
    nuget restore .\driver\packages.config -PackagesDirectory .\driver\packages

    Write-Host 'Building MUX IddCx virtual display driver...' -ForegroundColor Cyan
    msbuild $driverProject /m /p:Configuration=Release /p:Platform=x64

    New-Item -ItemType Directory -Path $driverPublish -Force | Out-Null

    foreach ($name in @('MUXVirtualDisplay.dll', 'MUXVirtualDisplay.inf', 'MUXVirtualDisplay.cat')) {
        $candidate = Get-ChildItem $driverBuildRoot -Recurse -Filter $name |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if (-not $candidate) {
            throw "Driver build did not produce $name."
        }

        Copy-Item $candidate.FullName (Join-Path $driverPublish $name) -Force
    }

    Copy-Item .\docs\VIRTUAL-DISPLAYS.md (Join-Path $appPublish 'VIRTUAL-DISPLAYS.md') -Force

    $zip = Join-Path $root 'artifacts\MUX-Virtual-win-x64.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$appPublish\*" -DestinationPath $zip -CompressionLevel Optimal

    Write-Host "Built: $zip" -ForegroundColor Green
}
finally {
    Pop-Location
}
