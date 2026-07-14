param(
    [string]$Sts2Path = "",
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

$LocalDotnet = Join-Path $Root ".tools\dotnet\dotnet.exe"
if (Test-Path $LocalDotnet) {
    $env:DOTNET_ROOT = Split-Path -Parent $LocalDotnet
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}

$props = @()
if ($Sts2Path) { $props += "/p:Sts2Path=$Sts2Path" }

dotnet restore .\LoserEatDust.csproj @props
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build .\LoserEatDust.csproj -c $Configuration @props
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "LoserEatDust / 败者食尘 built and installed." -ForegroundColor Green
