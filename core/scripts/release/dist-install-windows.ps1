#Requires -Version 5.1
<#
.SYNOPSIS
    Installs a prebuilt Sentinel Core release as Windows Services.

.DESCRIPTION
    This script ships inside the release .zip, next to the SentinelCore\ and SentinelCore.Web\
    folders. Unlike ..\install-windows.ps1 (which builds from a git checkout and needs the
    .NET SDK), this registers the self-contained binaries already sitting next to this script
    directly - no SDK, no build, no git required on the target machine.

    Services installed:
      SentinelCoreBridge   the alert pipeline           (port 5080)
      SentinelCoreWeb      the dashboard                (port 3320, and 3443 for HTTPS)
      SentinelCoreHomeKit  Apple Home sidecar, optional (needs Node.js)

    The same command upgrades an existing install: unpack the newer .zip over the old directory
    and re-run this. It detects the existing install, backs the configuration up, and restarts
    onto the new binaries rather than treating it as a first-time setup. Nothing in the archive
    shares a name with a file that holds user state, so the unpack step itself cannot destroy
    anything - see publish-release.sh.

    Must be run from an elevated (Administrator) PowerShell.

.PARAMETER InstallDir
    Where the release lives. Defaults to this script's own folder.

.PARAMETER MainPort
    Port for the alert pipeline. Default 5080.

.PARAMETER WebPort
    Port for the dashboard. Default 3320.

.PARAMETER AssumeYes
    Install missing dependencies (ffmpeg/Node/Ollama) without prompting.

.EXAMPLE
    .\install.ps1

.EXAMPLE
    .\install.ps1 -WebPort 8080 -AssumeYes
#>
[CmdletBinding()]
param(
    [string] $InstallDir,
    [int]    $MainPort = 5080,
    [int]    $WebPort  = 3320,
    [switch] $AssumeYes
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $InstallDir) { $InstallDir = $scriptDir }
$InstallDir = (Resolve-Path $InstallDir).Path

function Write-Step { param([string] $Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Warn { param([string] $Message) Write-Host "WARNING: $Message" -ForegroundColor Yellow }

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This must be run from an elevated PowerShell. Right-click PowerShell and pick 'Run as Administrator', then run it again."
}

$bridgeExe = Join-Path $InstallDir 'SentinelCore\SentinelCore.exe'
$webExe    = Join-Path $InstallDir 'SentinelCore.Web\SentinelCore.Web.exe'
foreach ($required in @($bridgeExe, $webExe)) {
    if (-not (Test-Path $required)) {
        throw "Expected a prebuilt binary at $required - is this script still next to the SentinelCore\ and SentinelCore.Web\ folders from the release .zip?"
    }
}

Write-Step "Installing from $InstallDir"

# ---------------------------------------------------------------------------
# Dependencies - ffmpeg/Node/Ollama via winget, same as ..\install-windows.ps1. The runtime
# itself is already bundled into the .exe files above, so the .NET SDK is NOT needed here.
# ---------------------------------------------------------------------------

function Test-Command { param([string] $Name) [bool](Get-Command $Name -ErrorAction SilentlyContinue) }

$dependencies = @(
    @{ Name = 'ffmpeg'; WingetId = 'Gyan.FFmpeg';       Description = 'ffmpeg - required for live view, trigger zones and clip recording' }
    @{ Name = 'node';   WingetId = 'OpenJS.NodeJS.LTS'; Description = 'Node.js 22+ - only for Apple HomeKit support; skippable' }
    @{ Name = 'ollama'; WingetId = 'Ollama.Ollama';     Description = 'Ollama - local AI vision. Skippable if you use a cloud model instead' }
)

$missing = @($dependencies | Where-Object { -not (Test-Command $_.Name) })

if ($missing.Count -gt 0) {
    Write-Host "The following are not installed:"
    foreach ($dep in $missing) { Write-Host "  - $($dep.Description)" }
    Write-Host ""

    if (-not (Test-Command 'winget')) {
        Write-Warn "winget isn't available, so these can't be installed automatically."
        Write-Warn "Install them by hand and run this again. winget ships with App Installer from the Microsoft Store."
    }
    else {
        $install = $AssumeYes
        if (-not $install) {
            $reply = Read-Host "Install them now with winget? [y/N]"
            $install = $reply -match '^[Yy]$'
        }

        if ($install) {
            foreach ($dep in $missing) {
                Write-Step "Installing $($dep.Name)"
                winget install --id $($dep.WingetId) --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
                if ($LASTEXITCODE -ne 0) { Write-Warn "winget returned $LASTEXITCODE installing $($dep.Name) - continuing." }
            }
            $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
                        [Environment]::GetEnvironmentVariable('Path', 'User')
        }
        else {
            Write-Host "Skipping dependency install."
        }
    }
}

if (-not (Test-Command 'ffmpeg')) { Write-Warn "ffmpeg is not installed. Live view, trigger zones and clip recording won't work." }
if (-not (Test-Command 'ollama')) {
    Write-Warn "Ollama is not installed. Either install it and run 'ollama pull gemma3:4b',"
    Write-Warn "or configure a cloud vision provider in the dashboard's setup wizard."
}

$nodePath = $null
$homeKitDir = Join-Path $InstallDir 'homekit-bridge'
if ((Test-Command 'node') -and (Test-Path $homeKitDir)) {
    $nodePath = (Get-Command node).Source
    Write-Step "Installing HomeKit sidecar dependencies"
    Push-Location $homeKitDir
    try {
        & npm install --omit=dev --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { Write-Warn "npm install returned $LASTEXITCODE - HomeKit may not work." }
    }
    finally { Pop-Location }
}

# ---------------------------------------------------------------------------
# Services - same New-Service approach as ..\install-windows.ps1, but pointed straight at the
# self-contained .exe files instead of `dotnet foo.dll`.
# ---------------------------------------------------------------------------

function Install-AppService {
    param(
        [string] $Name,
        [string] $DisplayName,
        [string] $Description,
        [string] $BinaryPath,
        [hashtable] $EnvironmentVariables = @{},
        [string[]] $DependsOn = @()
    )

    Write-Step "Installing service $Name"

    if (Get-Service -Name $Name -ErrorAction SilentlyContinue) {
        Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
        & sc.exe delete $Name | Out-Null
        Start-Sleep -Seconds 2
    }

    $newServiceArguments = @{
        Name           = $Name
        BinaryPathName = $BinaryPath
        DisplayName    = $DisplayName
        Description    = $Description
        StartupType    = 'Automatic'
    }
    if ($DependsOn.Count -gt 0) { $newServiceArguments['DependsOn'] = $DependsOn }
    New-Service @newServiceArguments | Out-Null

    & sc.exe failure $Name reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

    if ($EnvironmentVariables.Count -gt 0) {
        $registryKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
        $entries = $EnvironmentVariables.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }
        New-ItemProperty -Path $registryKey -Name 'Environment' -PropertyType MultiString -Value $entries -Force | Out-Null
    }
}

$mainDir = Join-Path $InstallDir 'SentinelCore'
$webDir  = Join-Path $InstallDir 'SentinelCore.Web'

# ---------------------------------------------------------------------------
# Fresh install or upgrade
# ---------------------------------------------------------------------------
# The archive ships its configuration as appsettings.defaults.json and contains no
# appsettings.json at all, so unpacking a new release over an existing install leaves that
# install's real settings - cameras, credentials, alert channels, whether setup is finished -
# exactly as they were. All that's left for this script is to create appsettings.json from the
# defaults when there genuinely isn't one, and never to touch it when there is. Settings a new
# version introduced are merged into the existing file by the apps themselves on their next
# start, add-only; see ConfigDefaultsSeeder.

$mainSettings = Join-Path $mainDir 'appsettings.json'
$webSettings  = Join-Path $webDir  'appsettings.json'
$isUpgrade    = Test-Path $mainSettings

function Initialize-Settings {
    param([string] $Directory, [string] $Name)

    $settings = Join-Path $Directory 'appsettings.json'
    if (Test-Path $settings) {
        Write-Host "    $Name\appsettings.json already exists - left untouched"
        return
    }

    $defaults = Join-Path $Directory 'appsettings.defaults.json'
    if (-not (Test-Path $defaults)) {
        throw "Expected $defaults in the release bundle - the .zip looks incomplete."
    }
    Copy-Item $defaults $settings
    Write-Host "    $Name\appsettings.json created from the shipped defaults"
}

$backupDir = $null
if ($isUpgrade) {
    $backupDir = Join-Path $InstallDir ("backups\" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Write-Step "Existing install found - upgrading"
    # Only the configuration is copied. The databases and captured images under SentinelCore\data
    # can run to gigabytes, and duplicating them on every upgrade would fill the disk - they are
    # also never at risk here, since nothing in the archive is named like them. To capture cameras
    # and settings in one portable file before upgrading, use Settings -> Backup in the dashboard.
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    Copy-Item $mainSettings (Join-Path $backupDir 'SentinelCore.appsettings.json')
    if (Test-Path $webSettings) { Copy-Item $webSettings (Join-Path $backupDir 'SentinelCore.Web.appsettings.json') }
    Write-Host "    Configuration backed up to $backupDir"
}
else {
    Write-Step "No existing install found - setting up from scratch"
}

Write-Step "Configuration"
Initialize-Settings -Directory $mainDir -Name 'SentinelCore'
Initialize-Settings -Directory $webDir  -Name 'SentinelCore.Web'

Install-AppService -Name 'SentinelCoreBridge' `
    -DisplayName 'Sentinel Core - Alert Pipeline' `
    -Description 'Receives Blue Iris alerts, analyses them with AI, and sends notifications.' `
    -BinaryPath "`"$bridgeExe`" --contentRoot `"$mainDir`"" `
    -EnvironmentVariables @{
        'ASPNETCORE_ENVIRONMENT' = 'Production'
        'ASPNETCORE_URLS'        = "http://0.0.0.0:$MainPort"
    }

Install-AppService -Name 'SentinelCoreWeb' `
    -DisplayName 'Sentinel Core - Dashboard' `
    -Description 'Web dashboard for the alert feed, cameras, trigger zones and settings.' `
    -BinaryPath "`"$webExe`" --contentRoot `"$webDir`"" `
    -DependsOn @('SentinelCoreBridge') `
    -EnvironmentVariables @{
        'ASPNETCORE_ENVIRONMENT' = 'Production'
        'ASPNETCORE_URLS'        = "http://0.0.0.0:$WebPort"
    }

if ($nodePath) {
    $homeKitEntry     = Join-Path $homeKitDir 'index.js'
    $mainSettingsPath = Join-Path $mainDir 'appsettings.json'

    Install-AppService -Name 'SentinelCoreHomeKit' `
        -DisplayName 'Sentinel Core - HomeKit Sidecar' `
        -Description 'Exposes cameras to Apple Home as motion sensors. Idle until HomeKit is enabled in the dashboard.' `
        -BinaryPath "`"$nodePath`" `"$homeKitEntry`"" `
        -EnvironmentVariables @{
            'NODE_ENV'              = 'production'
            'HOMEKIT_MAIN_SETTINGS' = $mainSettingsPath
        }
}

Write-Step "Starting services"
Start-Service -Name 'SentinelCoreBridge'
Start-Service -Name 'SentinelCoreWeb'
if ($nodePath) { Start-Service -Name 'SentinelCoreHomeKit' }

Write-Host ""
Write-Step "Status"
$serviceNames = @('SentinelCoreBridge', 'SentinelCoreWeb')
if ($nodePath) { $serviceNames += 'SentinelCoreHomeKit' }
Get-Service -Name $serviceNames | Format-Table -AutoSize Name, Status, StartType

# Polled rather than a single check after a fixed sleep: first start after an upgrade can be
# slower than a normal restart (the single-file binaries re-extract their native libraries), and
# a spurious "unreachable" here is exactly the sort of thing that sends someone reinstalling a
# working system.
function Wait-ForHealth {
    param([string] $Label, [string] $Url)

    for ($attempt = 1; $attempt -le 30; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -eq 200) {
                Write-Host "${Label}: 200"
                return $true
            }
        }
        catch { }
        Start-Sleep -Seconds 1
    }
    Write-Host "${Label}: unreachable" -ForegroundColor Yellow
    return $false
}

Write-Host ""
Write-Step "Health checks"
$healthy = $true
if (-not (Wait-ForHealth -Label "main app     ($MainPort)" -Url "http://127.0.0.1:$MainPort/health/live")) { $healthy = $false }
if (-not (Wait-ForHealth -Label "web dashboard($WebPort)"  -Url "http://127.0.0.1:$WebPort/api/health"))   { $healthy = $false }

if (-not $healthy) {
    Write-Host ""
    Write-Warn "One or more services did not come up. Check the Windows event log, or run the .exe"
    Write-Warn "directly from its folder to see the startup error."
    if ($isUpgrade) {
        Write-Host ""
        Write-Host "This was an upgrade. To go back to the previous version, unpack the release .zip you"
        Write-Host "were running before over this directory, re-run its install.ps1, and then restore the"
        Write-Host "configuration this script saved:"
        Write-Host "  Copy-Item '$backupDir\SentinelCore.appsettings.json' '$mainSettings' -Force"
        Write-Host "Your cameras, events and captured images were never modified."
    }
    exit 1
}

if ($isUpgrade) {
    Write-Host @"

==> Upgrade complete. The dashboard is at http://<this-machine's-LAN-IP>:$WebPort
    Your settings, cameras, events, captured images and HTTPS certificate all carried over -
    no need to run the setup wizard again, and devices that already trust this server's
    certificate stay trusted.

    Previous configuration saved at:
      $backupDir

    Keep the .zip you were running before this one. It is what you would unpack to go back,
    and it is the only copy of the previous binaries once this archive has replaced them.
"@
}
else {
    Write-Host @"

==> Done. Open the dashboard at http://<this-machine's-LAN-IP>:$WebPort
    The setup wizard walks through the rest (API key, vision backend, cameras, notifications).
    HTTPS is already on too, at the same address on port 3443 - the app generated its own
    certificate on first startup. See docs/DEPLOYMENT.md section 14a to trust it on your other
    devices and drop the browser warning.

    Firewall (depends on your network, not done automatically):
      New-NetFirewallRule -DisplayName 'Sentinel Core alerts' -Direction Inbound -LocalPort $MainPort -Protocol TCP -Action Allow
      New-NetFirewallRule -DisplayName 'Sentinel Core dashboard' -Direction Inbound -LocalPort $WebPort -Protocol TCP -Action Allow

    Updating: this isn't a git checkout - unpack the next release .zip over this directory and
    re-run its install.ps1. It will detect this install, keep your settings and data, back the
    configuration up first, and restart onto the new binaries.
"@
}
