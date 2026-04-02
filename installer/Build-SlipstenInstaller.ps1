param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.1",
    [string]$Runtime = "win-x64",
    [ValidateSet("self-contained", "framework-dependent")]
    [string]$DeploymentMode = "self-contained"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
$projectPath = Join-Path $repoRoot "src\Slipsten\Slipsten.csproj"
$publishDir = Join-Path $PSScriptRoot "artifacts\publish"
$installerScript = Join-Path $PSScriptRoot "Slipsten.iss"

if (Test-Path $publishDir) {
    Get-ChildItem $publishDir -Force -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

$isSelfContained = $DeploymentMode -eq "self-contained"

dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained $isSelfContained `
    -p:WindowsAppSDKSelfContained=$isSelfContained `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -o $publishDir

$innoCandidates = @(
    "$env:ProgramFiles (x86)\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)

$iscc = $innoCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host "Publish completed to $publishDir"
    Write-Host "Deployment mode: $DeploymentMode"
    Write-Host "Install Inno Setup 6 to build the installer, then re-run this script."
    exit 0
}

& $iscc "/DMyAppVersion=$Version" "/DPublishDir=$publishDir" $installerScript
