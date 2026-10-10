#Requires -Version 5.1
<#
.SYNOPSIS
    One-command install for Sentinel Core on Windows.

.DESCRIPTION
    The Windows counterpart to install-linux.sh. Builds everything, registers all three
    components as Windows Services, and starts them.

    Services installed:
      SentinelCoreBridge         the alert pipeline           (port 5080)
      SentinelCoreWeb            the dashboard                (port 3320)
      SentinelCoreHomeKit        Apple Home sidecar, optional (needs Node.js)

    Running this on the same machine as Blue Iris is the usual setup - Blue Iris is Windows-only,
    so this avoids needing a second box.

    The HomeKit sidecar is installed whenever Node.js is present, but stays completely off the
    network until HomeKit is switched on in the dashboard.

    Must be run from an elevated (Administrator) PowerShell - registering services requires it.

.PARAMETER InstallDir
    Where the code lives. Defaults to the parent of this script's folder. Services run in place
    from here, so don't move it afterwards without re-running this.

.PARAMETER MainPort
    Port for the alert pipeline. Default 5080.

.PARAMETER WebPort
    Port for the dashboard. Default 3320.

.PARAMETER AssumeYes
    Install missing dependencies without prompting.

.PARAMETER SkipTests
    Skip the test run. Not recommended.

.EXAMPLE
    .\install-windows.ps1

.EXAMPLE
    .\install-windows.ps1 -WebPort 8080 -AssumeYes
#>
[CmdletBinding()]
param(
    [string] $InstallDir,
    [int]    $MainPort = 5080,
    [int]    $WebPort  = 3320,
    [switch] $AssumeYes,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $InstallDir) { $InstallDir = Split-Path -Parent $scriptDir }
$InstallDir = (Resolve-Path $InstallDir).Path

function Write-Step { param([string] $Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Warn { param([string] $Message) Write-Host "WARNING: $Message" -ForegroundColor Yellow }

# ---------------------------------------------------------------------------
# Elevation
# ---------------------------------------------------------------------------

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This must be run from an elevated PowerShell. Right-click PowerShell and pick 'Run as Administrator', then run it again."
}

Write-Step "Installing from $InstallDir"

# ---------------------------------------------------------------------------
# Dependencies
# ---------------------------------------------------------------------------
# Nothing is installed without asking first (or -AssumeYes) - this changes the whole machine,
# not just this folder.

function Test-Command { param([string] $Name) [bool](Get-Command $Name -ErrorAction SilentlyContinue) }

$dependencies = @(
    @{ Name = 'dotnet'; WingetId = 'Microsoft.DotNet.SDK.10'; Description = '.NET 10 SDK      - required, the app is built and run with it' }
    @{ Name = 'ffmpeg'; WingetId = 'Gyan.FFmpeg';             Description = 'ffmpeg           - required for live view, trigger zones and clip recording' }
    @{ Name = 'node';   WingetId = 'OpenJS.NodeJS.LTS';       Description = 'Node.js 22+      - only for Apple HomeKit support; skippable' }
    @{ Name = 'ollama'; WingetId = 'Ollama.Ollama';           Description = 'Ollama           - local AI vision. Skippable if you use a cloud model instead' }
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
                # --disable-interactivity keeps winget from stalling this script on a prompt.
                winget install --id $($dep.WingetId) --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
                if ($LASTEXITCODE -ne 0) { Write-Warn "winget returned $LASTEXITCODE installing $($dep.Name) - continuing." }
            }

            # winget updates the machine PATH, but this already-running process won't see it
            # until we re-read it.
            $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
                        [Environment]::GetEnvironmentVariable('Path', 'User')
        }
        else {
            Write-Host "Skipping dependency install."
        }
    }
}

if (-not (Test-Command 'dotnet')) {
    throw "dotnet is still not available. Install the .NET 10 SDK, open a new PowerShell, and run this again."
}
$dotnetPath = (Get-Command dotnet).Source

if (-not (Test-Command 'ffmpeg')) {
    Write-Warn "ffmpeg is not installed. Live view, trigger zones and clip recording won't work."
}
if (-not (Test-Command 'ollama')) {
    Write-Warn "Ollama is not installed. Either install it and run 'ollama pull gemma3:4b',"
    Write-Warn "or configure a cloud vision provider in the dashboard's setup wizard."
} else {
    # llama-server's RAM prompt cache defaults to 8 GB and never hits on camera images, so it grows
    # until the machine runs out of memory - see docs/DEPLOYMENT.md. Ollama reads the machine
    # environment when it starts, so it takes effect at its next start (sign out and back in, or quit
    # and reopen Ollama from the tray).
    if ([Environment]::GetEnvironmentVariable('LLAMA_ARG_CACHE_RAM', 'Machine') -ne '0') {
        Write-Host "==> Turning off Ollama's RAM prompt cache (takes effect when Ollama next starts)"
        [Environment]::SetEnvironmentVariable('LLAMA_ARG_CACHE_RAM', '0', 'Machine')
    }
    if (-not ((& ollama list 2>$null) -match '^gemma3:4b')) {
        Write-Host "==> Downloading the gemma3:4b vision model (about 3.3 GB)"
        & ollama pull gemma3:4b
        if ($LASTEXITCODE -ne 0) { Write-Warn "'ollama pull gemma3:4b' failed - run it by hand before the first alert." }
    }
}

$nodePath = $null
if (Test-Command 'node') { $nodePath = (Get-Command node).Source }

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------

Write-Step "Clearing stale build servers (works around an intermittent CLR crash seen on Linux)"
& dotnet build-server shutdown *> $null

Write-Step "Building (Release)"
& dotnet build $InstallDir -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# appsettings.json is untracked live configuration (the dashboard writes every credential into
# it); a fresh clone has only the checked-in, secret-free appsettings.defaults.json. Create the
# live file before either service starts, because the dashboard reads it too. Never overwrite one.
$mainSettings = Join-Path $InstallDir "SentinelCore\appsettings.json"
if (-not (Test-Path $mainSettings)) {
    Write-Step "Creating SentinelCore\appsettings.json from appsettings.defaults.json"
    Copy-Item (Join-Path $InstallDir "SentinelCore\appsettings.defaults.json") $mainSettings
}

if (-not $SkipTests) {
    Write-Step "Running tests"
    & dotnet test $InstallDir -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed - not touching the services." }
}
else {
    Write-Step "Skipping tests (-SkipTests)"
}

if ($nodePath) {
    Write-Step "Installing HomeKit sidecar dependencies"
    Push-Location (Join-Path $InstallDir 'homekit-bridge')
    try {
        & npm install --omit=dev --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { Write-Warn "npm install returned $LASTEXITCODE - HomeKit may not work." }
    }
    finally { Pop-Location }
}
else {
    Write-Step "Skipping HomeKit sidecar (Node.js not found - install Node 22+ and re-run to add it)"
}

# ---------------------------------------------------------------------------
# Services
# ---------------------------------------------------------------------------
# The .NET hosts call UseWindowsService(), so the same binaries that run under systemd on Linux
# run as a native Windows Service here with no separate build.

# No WorkingDirectory parameter on purpose: a Windows service's working directory is
# System32 and can't be set through New-Service. Nothing here needs it - the .NET hosts get an
# explicit --contentRoot, and the HomeKit sidecar resolves its own paths from __dirname.
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
        # Stop before rewriting, or the binary path change won't take and the old one keeps running.
        Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
        & sc.exe delete $Name | Out-Null
        Start-Sleep -Seconds 2
    }

    # New-Service rather than sc.exe: it handles quoting of paths containing spaces itself
    # (C:\Program Files\dotnet\... otherwise silently produces a broken service), and reports
    # failures as catchable errors instead of an exit code that's easy to miss.
    $newServiceArguments = @{
        Name           = $Name
        BinaryPathName = $BinaryPath
        DisplayName    = $DisplayName
        Description    = $Description
        StartupType    = 'Automatic'
    }
    if ($DependsOn.Count -gt 0) { $newServiceArguments['DependsOn'] = $DependsOn }
    New-Service @newServiceArguments | Out-Null

    # Restart on failure, matching Restart=always in the systemd units. No cmdlet covers this,
    # so it stays sc.exe - the arguments here are all fixed, with no paths to mis-quote.
    & sc.exe failure $Name reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

    # sc.exe has no way to set the working directory or environment, so those go in the registry
    # where the service control manager reads them.
    # New-Service can't set environment variables, so they go in the registry where the service
    # control manager reads them: a REG_MULTI_SZ "Environment" value under the service's key.
    if ($EnvironmentVariables.Count -gt 0) {
        $registryKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
        $entries = $EnvironmentVariables.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }
        New-ItemProperty -Path $registryKey -Name 'Environment' -PropertyType MultiString -Value $entries -Force | Out-Null
    }
}

$mainDll = Join-Path $InstallDir 'SentinelCore\bin\Release\net10.0\SentinelCore.dll'
$webDll  = Join-Path $InstallDir 'SentinelCore.Web\bin\Release\net10.0\SentinelCore.Web.dll'
$mainDir = Join-Path $InstallDir 'SentinelCore'
$webDir  = Join-Path $InstallDir 'SentinelCore.Web'

foreach ($required in @($mainDll, $webDll)) {
    if (-not (Test-Path $required)) { throw "Expected build output missing: $required" }
}

# Certificates
#
# The dashboard generates its own HTTPS certificate authority and server key on first start, into
# a "certs" folder beside the two components. On Linux and macOS it chmods that folder to 700 and
# the key to 600 itself; it can't do the equivalent here, because Windows permissions are ACLs
# rather than a mode and the .NET APIs for them are Windows-only.
#
# That matters because of where the key ends up. A folder created directly under C:\ inherits
# "BUILTIN\Users: ReadAndExecute" from the drive root, so an install to C:\SentinelCore - which
# -InstallDir invites - would leave the server's private key readable by every account on the
# machine. Creating the folder here with an explicit ACL first means the app finds it already
# locked down, and its Directory.CreateDirectory leaves an existing folder's permissions alone.
#
# SIDs rather than names: BUILTIN\Administrators is localised, S-1-5-32-544 is not.
$certDir = Join-Path $InstallDir 'certs'
Write-Step "Securing $certDir"
New-Item -ItemType Directory -Force -Path $certDir | Out-Null
& icacls $certDir /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Couldn't restrict permissions on $certDir - refusing to continue and leave a private key world-readable."
}

Install-AppService -Name 'SentinelCoreBridge' `
    -DisplayName 'Sentinel Core - Alert Pipeline' `
    -Description 'Receives Blue Iris alerts, analyses them with AI, and sends notifications.' `
    -BinaryPath "`"$dotnetPath`" `"$mainDll`" --contentRoot `"$mainDir`"" `
    -EnvironmentVariables @{
        'ASPNETCORE_ENVIRONMENT' = 'Production'
        'ASPNETCORE_URLS'        = "http://0.0.0.0:$MainPort"
        'DOTNET_NOLOGO'          = 'true'
    }

Install-AppService -Name 'SentinelCoreWeb' `
    -DisplayName 'Sentinel Core - Dashboard' `
    -Description 'Web dashboard for the alert feed, cameras, trigger zones and settings.' `
    -BinaryPath "`"$dotnetPath`" `"$webDll`" --contentRoot `"$webDir`"" `
    -DependsOn @('SentinelCoreBridge') `
    -EnvironmentVariables @{
        'ASPNETCORE_ENVIRONMENT' = 'Production'
        'ASPNETCORE_URLS'        = "http://0.0.0.0:$WebPort"
    }

if ($nodePath) {
    $homeKitDir      = Join-Path $InstallDir 'homekit-bridge'
    $homeKitEntry    = Join-Path $homeKitDir 'index.js'
    $mainSettingsPath = Join-Path $mainDir 'appsettings.json'

    Install-AppService -Name 'SentinelCoreHomeKit' `
        -DisplayName 'Sentinel Core - HomeKit Sidecar' `
        -Description 'Exposes cameras to Apple Home as motion sensors. Idle until HomeKit is enabled in the dashboard.' `
        -BinaryPath "`"$nodePath`" `"$homeKitEntry`"" `
        -EnvironmentVariables @{
            'NODE_ENV'               = 'production'
            'HOMEKIT_MAIN_SETTINGS'  = $mainSettingsPath
        }
}

Write-Step "Starting services"
Start-Service -Name 'SentinelCoreBridge'
Start-Service -Name 'SentinelCoreWeb'
if ($nodePath) { Start-Service -Name 'SentinelCoreHomeKit' }

Start-Sleep -Seconds 5

Write-Host ""
Write-Step "Status"
$serviceNames = @('SentinelCoreBridge', 'SentinelCoreWeb')
if ($nodePath) { $serviceNames += 'SentinelCoreHomeKit' }
Get-Service -Name $serviceNames | Format-Table -AutoSize Name, Status, StartType

Write-Host ""
Write-Step "Health checks"
foreach ($check in @(
    @{ Label = "main app     ($MainPort)"; Url = "http://127.0.0.1:$MainPort/health/live" }
    @{ Label = "web dashboard($WebPort)";  Url = "http://127.0.0.1:$WebPort/api/health" }
)) {
    try {
        $response = Invoke-WebRequest -Uri $check.Url -UseBasicParsing -TimeoutSec 10
        Write-Host "$($check.Label): $($response.StatusCode)"
    }
    catch {
        Write-Host "$($check.Label): unreachable" -ForegroundColor Yellow
    }
}

Write-Host @"

==> Done. Open the dashboard at http://<this-machine's-LAN-IP>:$WebPort
    The setup wizard walks through the rest (API key, vision backend, cameras, notifications).

    One thing this script deliberately does NOT do, because it depends on your network:

    Firewall. These ports are LAN-only by design - never expose them to the internet.
    From an elevated PowerShell:
      New-NetFirewallRule -DisplayName 'Sentinel Core alerts' -Direction Inbound -LocalPort $MainPort -Protocol TCP -Action Allow
      New-NetFirewallRule -DisplayName 'Sentinel Core dashboard' -Direction Inbound -LocalPort $WebPort -Protocol TCP -Action Allow
    Plus, only if you turn HomeKit on (iPhones connect to this port directly):
      New-NetFirewallRule -DisplayName 'Sentinel Core HomeKit' -Direction Inbound -LocalPort 51826 -Protocol TCP -Action Allow

    To update later: git pull, then re-run this script.
"@
