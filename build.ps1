# AxiomCode.TwinCAT.CodeAnaliser - Build Script
param(
    [switch]$Release,
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$projectPath = "src\AxiomCode.TwinCAT.CodeAnaliser\AxiomCode.TwinCAT.CodeAnaliser.csproj"
$config = if ($Release) { "Release" } else { "Debug" }

Write-Host "Building AxiomCode.TwinCAT.CodeAnaliser ($config)..." -ForegroundColor Cyan

# Restore
dotnet restore $projectPath
if ($LASTEXITCODE -ne 0) { throw "Restore failed" }

# Build
dotnet build $projectPath -c $config --no-restore
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

Write-Host "Build successful!" -ForegroundColor Green

if ($Publish) {
    Write-Host "Publishing..." -ForegroundColor Cyan
    dotnet publish $projectPath -c Release --self-contained true -r win-x64 -o "publish"
    if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
    Write-Host "Published to: publish\" -ForegroundColor Green
    Write-Host "Executable: publish\AxiomCode.TwinCAT.CodeAnaliser.exe" -ForegroundColor Green
}

# Show output location
$outputDir = "src\AxiomCode.TwinCAT.CodeAnaliser\bin\$config\net8.0"
Write-Host "`nOutput: $outputDir\AxiomCode.TwinCAT.CodeAnaliser.exe" -ForegroundColor Yellow
