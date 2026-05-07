# ============================================================
#  DS3OS - Automatic Installer for Dark Souls 2 SOTFS
#  Downloads the latest release from this fork (Galidar/DSSeamlessCoop)
#  Auto-detects WAN/LAN IPs and applies the preconfigured config.json
# ============================================================

[CmdletBinding()]
param(
    [string]$Repo = 'Galidar/DSSeamlessCoop',
    [string]$AssetName = 'windows.zip'
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Host.UI.RawUI.WindowTitle = 'DS3OS - Setup'

function Write-Section {
    param([string]$Step, [string]$Text)
    Write-Host ""
    Write-Host "[$Step] $Text" -ForegroundColor Cyan
}

function Write-Err {
    param([string]$Text)
    Write-Host "[ERROR] $Text" -ForegroundColor Red
}

function Get-PublicIP {
    try {
        return (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 10).ToString().Trim()
    } catch {
        return $null
    }
}

function Get-LocalIP {
    # Prefer the interface that has a default gateway (the one going to the internet)
    try {
        $ip = (Get-NetIPConfiguration | Where-Object { $null -ne $_.IPv4DefaultGateway } | Select-Object -First 1).IPv4Address.IPAddress
        if ($ip) { return $ip.Trim() }
    } catch { }

    # Fallback: any DHCP/Manual non-loopback non-APIPA IPv4
    try {
        $ip = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
               Where-Object { ($_.PrefixOrigin -eq 'Dhcp' -or $_.PrefixOrigin -eq 'Manual') -and $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' } |
               Select-Object -First 1).IPAddress
        if ($ip) { return $ip.Trim() }
    } catch { }

    return $null
}

function Get-LatestReleaseUrl {
    param([string]$Repo, [string]$AssetName)
    try {
        $headers = @{ 'User-Agent' = 'DSSeamlessCoop-Setup' }
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers -TimeoutSec 15
        $asset = $release.assets | Where-Object { $_.name -eq $AssetName } | Select-Object -First 1
        if ($null -eq $asset) { return $null }
        return $asset.browser_download_url
    } catch {
        return $null
    }
}

# ----------------------------------------------------------------

Write-Host "============================================================" -ForegroundColor Yellow
Write-Host " Installing DS3OS for Dark Souls 2 SOTFS..." -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Yellow

# Step 1: Detect IPs
Write-Section '1/5' 'Detecting IPs automatically...'
$WanIp = Get-PublicIP
$LanIp = Get-LocalIP

if (-not $WanIp) {
    Write-Err 'Could not get public IP. Check your internet connection.'
    exit 1
}
if (-not $LanIp) {
    Write-Err 'Could not detect local IP.'
    exit 1
}

Write-Host "      Public IP : $WanIp"
Write-Host "      Local IP  : $LanIp"

# Step 2: Resolve latest release URL
Write-Section '2/5' "Resolving latest release from $Repo via GitHub API..."
$DownloadUrl = Get-LatestReleaseUrl -Repo $Repo -AssetName $AssetName

if (-not $DownloadUrl) {
    Write-Err "No '$AssetName' asset found in the latest release of $Repo."
    Write-Host ""
    Write-Host "Possible reasons:" -ForegroundColor Yellow
    Write-Host "  - The repo has no releases yet (run the GitHub Actions 'Release' workflow first)"
    Write-Host "  - The repo is private (this script only works with public repos)"
    Write-Host "  - Network/API problem"
    Write-Host ""
    Write-Host "To create the first release:" -ForegroundColor Yellow
    Write-Host "  1. Go to https://github.com/$Repo/actions"
    Write-Host "  2. Run the 'Release' workflow"
    Write-Host "  3. Wait for it to finish (~10-15 min) then run setup again"
    exit 1
}

Write-Host "      URL: $DownloadUrl"

# Step 3: Download
Write-Section '3/5' 'Downloading binaries...'
$ZipFile = Join-Path $ScriptDir 'windows.zip'
try {
    Invoke-WebRequest -Uri $DownloadUrl -OutFile $ZipFile -UseBasicParsing
} catch {
    Write-Err "Download failed: $_"
    exit 1
}
if (-not (Test-Path $ZipFile)) {
    Write-Err 'Download failed (zip not found after request).'
    exit 1
}

# Step 4: Extract
Write-Section '4/5' 'Extracting files...'
try {
    Expand-Archive -Path $ZipFile -DestinationPath $ScriptDir -Force
} catch {
    Write-Err "Extraction failed: $_"
    exit 1
}

# Step 5: Apply config with detected IPs
Write-Section '5/5' 'Applying configuration with detected IPs...'
$ConfigTemplate = Join-Path $ScriptDir 'config.json'
$ConfigTarget = Join-Path $ScriptDir 'Server\Saved\default\config.json'

if (-not (Test-Path $ConfigTemplate)) {
    Write-Err "Template not found: $ConfigTemplate"
    exit 1
}

$ConfigDir = Split-Path -Parent $ConfigTarget
if (-not (Test-Path $ConfigDir)) { New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null }

$content = Get-Content -Path $ConfigTemplate -Raw
$content = $content -replace '__WAN_IP__', $WanIp
$content = $content -replace '__LAN_IP__', $LanIp
Set-Content -Path $ConfigTarget -Value $content -NoNewline -Encoding UTF8

# Cleanup
Remove-Item -Path $ZipFile -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host " Installation completed successfully." -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Configuration applied with your current IPs:"
Write-Host "  - Public IP : $WanIp"
Write-Host "  - Local IP  : $LanIp"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host '  1. Right click > "Run as administrator" on 1-Setup-Firewall.bat'
Write-Host '  2. Double click 2-Start-Server.bat'
Write-Host '  3. Right click > "Run as administrator" on 3-Start-Loader.bat'
Write-Host ""
Write-Host "NOTE: If your public IP changes later, run Update-IPs.bat."
