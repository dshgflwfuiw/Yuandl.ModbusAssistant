param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained = $true,
    [switch]$SingleFile = $false,
    [switch]$BuildInstaller = $false
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$ProjectPath = Join-Path $RepoRoot "Yuandl.ModbusAssistant\Yuandl.ModbusAssistant.csproj"
$SolutionPath = Join-Path $RepoRoot "Yuandl.ModbusAssistant.sln"
$PublishDir = Join-Path $RepoRoot "dist\$Runtime"

Write-Host "=== 1/4 Running xUnit Test Suite ===" -ForegroundColor Cyan
dotnet test $SolutionPath --configuration $Configuration

Write-Host "=== 2/4 Publishing WPF Desktop App ($Runtime) ===" -ForegroundColor Cyan
if (Test-Path $PublishDir) {
    Remove-Item -Recurse -Force $PublishDir
}

$publishArgs = @(
    "publish", $ProjectPath,
    "--configuration", $Configuration,
    "--runtime", $Runtime,
    "--output", $PublishDir,
    "--self-contained", ($SelfContained.IsPresent.ToString().ToLowerInvariant()),
    "-p:PublishSingleFile=$($SingleFile.IsPresent.ToString().ToLowerInvariant())",
    "-p:IncludeNativeLibrariesForSelfExtract=$($SingleFile.IsPresent.ToString().ToLowerInvariant())"
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Published binary output: $PublishDir" -ForegroundColor Green

if ($BuildInstaller) {
    Write-Host "=== 3/4 Building Inno Setup Installer ===" -ForegroundColor Cyan
    $iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if (-not $iscc) {
        $defaultIscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        if (Test-Path $defaultIscc) {
            $isccPath = $defaultIscc
        } else {
            throw "未找到 Inno Setup 6 (ISCC.exe)，请安装 Inno Setup 6 或去掉 -BuildInstaller 参数。"
        }
    } else {
        $isccPath = $iscc.Source
    }

    $issFile = Join-Path $PSScriptRoot "Yuandl.ModbusAssistant.iss"
    & $isccPath "/DSourceDir=$PublishDir" $issFile
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC failed with exit code $LASTEXITCODE"
    }
    Write-Host "=== 4/4 Installer created in dist/installer ===" -ForegroundColor Green
}
