$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'artifacts\MUX'

Push-Location $root
try {
    Write-Host 'Validating MUX.Core...' -ForegroundColor Cyan
    dotnet run --project .\tests\MUX.Core.Tests\MUX.Core.Tests.csproj -c Release

    Write-Host 'Building MUX...' -ForegroundColor Cyan
    dotnet restore .\MUX.sln
    dotnet build .\src\MUX.App\MUX.App.csproj -c Release --no-restore

    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    dotnet publish .\src\MUX.App\MUX.App.csproj -c Release -r win-x64 --self-contained true -o $publish

    $zip = Join-Path $root 'artifacts\MUX-win-x64.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$publish\*" -DestinationPath $zip -CompressionLevel Optimal

    Write-Host "Built: $zip" -ForegroundColor Green
}
finally {
    Pop-Location
}
