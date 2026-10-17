#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Bulk import master data from CSV files into SLH TMS via REST API.

.DESCRIPTION
    This script reads Vehicles.csv, Trailers.csv, Sites.csv, and Fuel_Cards.csv
    from the current directory and POST them to the TMS API import endpoints.

    Requires: PowerShell 7+ or 5.1 with appropriate execution policy.

.PARAMETER ApiBaseUrl
    Base URL of the TMS API (default: http://localhost:5000)

.PARAMETER TokenEndpoint
    Entra ID token endpoint for OAuth2 (default: https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token)

.PARAMETER ClientId
    Entra ID application (client) ID

.PARAMETER ClientSecret
    Entra ID application secret (or use Azure CLI authentication)

.PARAMETER TenantId
    Entra ID tenant ID

.PARAMETER CsvPath
    Path to folder containing CSV files (default: current directory)

.EXAMPLE
    # Import using Azure CLI (interactive)
    ./import-master-data.ps1 -ApiBaseUrl "https://slh-tms-api-prod.azurewebsites.net"

.EXAMPLE
    # Import using client credentials
    ./import-master-data.ps1 `
        -ApiBaseUrl "http://localhost:5000" `
        -ClientId "00000000-0000-0000-0000-000000000000" `
        -ClientSecret "secret" `
        -TenantId "common"
#>

param(
    [string]$ApiBaseUrl = "http://localhost:5000",
    [string]$CsvPath = (Get-Location),
    [string]$ClientId,
    [string]$ClientSecret,
    [string]$TenantId,
    [string]$TokenEndpoint
)

$ErrorActionPreference = "Stop"

function Write-Status {
    param([string]$Message, [switch]$Success, [switch]$Error)
    $color = if ($Success) { "Green" } elseif ($Error) { "Red" } else { "Cyan" }
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $Message" -ForegroundColor $color
}

function Get-AccessToken {
    if ($ClientSecret -and $TenantId) {
        Write-Status "Authenticating via OAuth2 client credentials..."

        if (-not $TokenEndpoint) {
            $TokenEndpoint = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"
        }

        $tokenResponse = Invoke-RestMethod `
            -Uri $TokenEndpoint `
            -Method Post `
            -ContentType "application/x-www-form-urlencoded" `
            -Body @{
                client_id     = $ClientId
                client_secret = $ClientSecret
                scope         = "api://SET_API_APPLICATION_CLIENT_ID/.default"
                grant_type    = "client_credentials"
            } -ErrorAction Stop

        return $tokenResponse.access_token
    }
    else {
        Write-Status "Using Azure CLI for authentication (interactive)..."
        $azcliToken = az account get-access-token --scope "api://SET_API_APPLICATION_CLIENT_ID/.default" --query "accessToken" -o tsv
        if (-not $azcliToken) {
            throw "Failed to get access token via Azure CLI. Ensure you are logged in: az login"
        }
        return $azcliToken
    }
}

function Import-CsvFile {
    param([string]$FilePath, [string]$Endpoint, [string]$Token)

    $fileName = Split-Path $FilePath -Leaf
    if (-not (Test-Path $FilePath)) {
        Write-Status "File not found: $fileName" -Error
        return $false
    }

    Write-Status "Importing $fileName..."

    try {
        $fileContent = Get-Item $FilePath
        $response = Invoke-RestMethod `
            -Uri "$ApiBaseUrl/api/v1/admin/import/$Endpoint" `
            -Method Post `
            -Headers @{ Authorization = "Bearer $Token" } `
            -Form @{ file = $fileContent } `
            -ErrorAction Stop

        Write-Status "  ✓ Requested: $($response.requestedRowCount) rows" -Success
        Write-Status "    ✓ Inserted: $($response.insertedRows) rows" -Success
        Write-Status "    ⊘ Skipped: $($response.skippedRows) rows" -Success

        if ($response.errors -and $response.errors.Count -gt 0) {
            Write-Status "    ⚠ Errors ($($response.errors.Count)):" -Error
            $response.errors | ForEach-Object { Write-Host "      - $_" -ForegroundColor Yellow }
        }

        return $response.insertedRows -gt 0
    }
    catch {
        Write-Status "Failed to import $fileName : $_" -Error
        return $false
    }
}

Write-Host ""
Write-Status "╔═══════════════════════════════════════════════════════════╗" -Success
Write-Status "║           SLH TMS Master Data CSV Importer                ║" -Success
Write-Status "╚═══════════════════════════════════════════════════════════╝" -Success
Write-Host ""

Write-Status "Configuration:"
Write-Host "  API Base URL: $ApiBaseUrl"
Write-Host "  CSV Path: $CsvPath"
Write-Host ""

# Get access token
$token = Get-AccessToken
Write-Status "✓ Authentication successful" -Success

Write-Host ""
Write-Status "Starting bulk import..."

Write-Host ""

$results = @{
    vehicles   = Import-CsvFile -FilePath "$CsvPath/Vehicles.csv" -Endpoint "vehicles" -Token $token
    trailers   = Import-CsvFile -FilePath "$CsvPath/Trailers.csv" -Endpoint "trailers" -Token $token
    sites      = Import-CsvFile -FilePath "$CsvPath/Sites.csv" -Endpoint "sites" -Token $token
    fuelCards  = Import-CsvFile -FilePath "$CsvPath/Fuel_Cards.csv" -Endpoint "fuel-cards" -Token $token
}

Write-Host ""
Write-Status "╔═══════════════════════════════════════════════════════════╗" -Success
Write-Status "║                    Import Summary                         ║" -Success
Write-Status "╚═══════════════════════════════════════════════════════════╝" -Success
Write-Host ""

$successCount = ($results.Values | Where-Object { $_ -eq $true }).Count
Write-Status "Results: $successCount / 4 tables imported successfully"

if ($successCount -eq 4) {
    Write-Status "✓ All master data imported successfully!" -Success
    exit 0
}
else {
    Write-Status "⚠ Some tables failed. Review errors above." -Error
    exit 1
}
