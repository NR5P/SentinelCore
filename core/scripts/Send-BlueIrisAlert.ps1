param(
    [Parameter(Mandatory = $true)][string]$ImagePath,
    [string]$Camera = "unknown",
    [string]$AlertId = "",
    [string]$Profile = "default",
    [string]$BridgeUrl = "http://192.168.1.50:5080",
    [Parameter(Mandatory = $true)][string]$ApiKey
)

$ErrorActionPreference = "Stop"
$uri = "$($BridgeUrl.TrimEnd('/'))/api/blueiris/alert"

if (-not (Test-Path -LiteralPath $ImagePath)) {
    throw "Blue Iris alert image does not exist: $ImagePath"
}

$headers = @{ "X-Api-Key" = $ApiKey }
$form = @{
    image      = Get-Item -LiteralPath $ImagePath
    camera     = $Camera
    alertId    = $AlertId
    profile    = $Profile
    occurredAt = [DateTimeOffset]::Now.ToString("o")
}

$lastError = $null
for ($attempt = 1; $attempt -le 3; $attempt++) {
    try {
        $response = Invoke-RestMethod -Method Post -Uri $uri -Headers $headers -Form $form -TimeoutSec 30
        $response | ConvertTo-Json -Depth 5 | Write-Output
        exit 0
    }
    catch {
        $lastError = $_
        if ($attempt -lt 3) { Start-Sleep -Seconds ([Math]::Pow(2, $attempt)) }
    }
}
throw $lastError
