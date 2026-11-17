$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "v4\QuantumRealityShiftV4.csproj"
$dist = Join-Path $root "dist\QuantumRealityShiftV4"
$zip  = Join-Path $root "QuantumRealityShiftV4_Release.zip"

Write-Host "Building QuantumRealityShiftV4 (Release)..." -ForegroundColor Cyan
dotnet build $proj -c Release --no-incremental

if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
if (Test-Path $zip)  { Remove-Item -Force $zip }
New-Item -ItemType Directory -Path $dist | Out-Null

$releaseOut = Join-Path $root "v4\bin\Release\net48"
Copy-Item (Join-Path $releaseOut "QuantumRealityShiftV4.dll") $dist
Copy-Item (Join-Path $releaseOut "Newtonsoft.Json.dll") $dist
Copy-Item (Join-Path $root "src\lib\LemonUI.SHVDN3.dll") $dist
Copy-Item (Join-Path $root "v4\QuantumWeather.example.ini") $dist
Copy-Item (Join-Path $root "v4\README_V4.md") (Join-Path $dist "README.md")

Compress-Archive -Path "$dist\*" -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Packaged clean V4 release zip: $zip" -ForegroundColor Green
Get-ChildItem $dist | Select-Object Name, Length
