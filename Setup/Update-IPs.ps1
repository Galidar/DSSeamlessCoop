# ============================================================
#  DS3OS - Update IPs in config.json
#  Useful when your public IP changes (common with residential ISPs)
# ============================================================

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Host.UI.RawUI.WindowTitle = 'DS3OS - Update IPs'

$ConfigFile = Join-Path $ScriptDir 'Server\Saved\default\config.json'

if (-not (Test-Path $ConfigFile)) {
    Write-Host "[ERROR] Could not find: $ConfigFile" -ForegroundColor Red
    Write-Host "Run setup.bat first to install the server."
    exit 1
}

function Get-PublicIP {
    try {
        return (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 10).ToString().Trim()
    } catch { return $null }
}

function Get-LocalIP {
    try {
        $ip = (Get-NetIPConfiguration | Where-Object { $null -ne $_.IPv4DefaultGateway } | Select-Object -First 1).IPv4Address.IPAddress
        if ($ip) { return $ip.Trim() }
    } catch { }

    try {
        $ip = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
               Where-Object { ($_.PrefixOrigin -eq 'Dhcp' -or $_.PrefixOrigin -eq 'Manual') -and $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' } |
               Select-Object -First 1).IPAddress
        if ($ip) { return $ip.Trim() }
    } catch { }

    return $null
}

Write-Host "============================================================" -ForegroundColor Yellow
Write-Host " Updating DS3OS server IPs..." -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Yellow
Write-Host ""

Write-Host "Detecting current IPs..."
$WanIp = Get-PublicIP
$LanIp = Get-LocalIP

if (-not $WanIp) {
    Write-Host "[ERROR] Could not get public IP. Check your internet connection." -ForegroundColor Red
    exit 1
}
if (-not $LanIp) {
    Write-Host "[ERROR] Could not detect local IP." -ForegroundColor Red
    exit 1
}

Write-Host "  Public IP : $WanIp"
Write-Host "  Local IP  : $LanIp"
Write-Host ""

Write-Host "Applying changes to config.json..."
$content = Get-Content -Path $ConfigFile -Raw
$content = $content -replace '"ServerHostname":\s*"[^"]*"',        ('"ServerHostname": "' + $WanIp + '"')
$content = $content -replace '"ServerPrivateHostname":\s*"[^"]*"', ('"ServerPrivateHostname": "' + $LanIp + '"')
Set-Content -Path $ConfigFile -Value $content -NoNewline -Encoding UTF8

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host " IPs updated successfully." -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "If the server is running, restart it to apply the changes:" -ForegroundColor Yellow
Write-Host "  1. Run 4-Stop-Server.bat"
Write-Host "  2. Run 2-Start-Server.bat"
