#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds a self-contained Sentinel Core for Windows into publish/.

.DESCRIPTION
    Self-contained on purpose: the resulting folder runs on a PC with no .NET installed at all.
    A framework-dependent build is smaller, but it fails at launch with "You must install .NET to
    run this application" on any machine without the .NET 8 *Desktop* Runtime specifically - and
    that is a confusing first impression for something whose whole job is to be trusted to be
    running.

    Not single-file: WPF's own assemblies do not all tolerate being bundled, and a folder is easier
    to update in place anyway.

.PARAMETER Runtime
    The runtime identifier to build for. Defaults to win-x64.

.PARAMETER Output
    Where to write the build. Defaults to ./publish.
#>
[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$Output = "publish"
)

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

$dotnet = "dotnet"
if (-not (Get-Command $dotnet -ErrorAction SilentlyContinue)) {
    # A per-user SDK install (dotnet-install.ps1) is not on PATH by default.
    $local = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    if (Test-Path $local) {
        $dotnet = $local
        $env:DOTNET_ROOT = Split-Path $local
    }
    else {
        throw "dotnet was not found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0"
    }
}

if (Test-Path $Output) {
    Remove-Item -Recurse -Force $Output
}

Write-Host "Testing..." -ForegroundColor Cyan
& $dotnet test --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Tests failed; not publishing." }

Write-Host "Publishing $Runtime to $Output ..." -ForegroundColor Cyan
& $dotnet publish SentinelCore.App/SentinelCore.App.csproj `
    --nologo `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $Output `
    -p:DebugType=none `
    -p:SatelliteResourceLanguages=en
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$size = (Get-ChildItem -Recurse $Output | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ""
Write-Host ("Done. {0:N0} MB in {1}" -f $size, (Resolve-Path $Output)) -ForegroundColor Green
Write-Host "Run SentinelCore.exe from that folder. No .NET install needed on the target PC."
Write-Host "Live video additionally needs the Edge WebView2 runtime, which ships with Windows 11"
Write-Host "and current Windows 10; everything else works without it."
