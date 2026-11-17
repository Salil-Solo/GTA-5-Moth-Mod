# Builds QuantumRealityShiftV2 in Release mode and creates a clean, shareable ZIP
# without any private API keys or third-party game loaders.
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "v2\QuantumRealityShiftV2.csproj"
$binDir = Join-Path $root "v2\bin\Release\net48"
$lemonDll = Join-Path $root "src\lib\LemonUI.SHVDN3.dll"
$stageDir = Join-Path $root "dist\QuantumRealityShiftV2"
$zipPath = Join-Path $root "QuantumRealityShiftV2_Release.zip"

Write-Host "Building QuantumRealityShiftV2 (Release)..." -ForegroundColor Cyan
dotnet build $proj -c Release --no-incremental
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

Copy-Item (Join-Path $binDir "QuantumRealityShiftV2.dll") $stageDir
Copy-Item (Join-Path $binDir "Newtonsoft.Json.dll") $stageDir
Copy-Item $lemonDll $stageDir
Copy-Item (Join-Path $root "v2\QuantumWeather.example.ini") $stageDir
Copy-Item (Join-Path $root "v2\README_V2.md") (Join-Path $stageDir "README.md")

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$stageDir\*" -DestinationPath $zipPath -CompressionLevel Optimal
Remove-Item (Join-Path $root "dist") -Recurse -Force

Write-Host "Created clean release package: $zipPath" -ForegroundColor Green
Get-Item $zipPath | Select-Object Name, Length
