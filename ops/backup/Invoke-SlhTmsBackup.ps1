[CmdletBinding()]
param(
    [string]$SqlServer = $env:SLH_TMS_SQL_SERVER,
    [string]$Database = $env:SLH_TMS_SQL_DATABASE,
    [string]$TenantId = $env:SLH_TMS_TENANT_ID,
    [string]$CredentialPath = $env:SLH_TMS_BACKUP_CREDENTIAL_PATH,
    [string]$BackupRoot = $env:SLH_TMS_BACKUP_ROOT,
    [string]$SqlPackagePath = $(if ($env:SLH_TMS_SQLPACKAGE_PATH) { $env:SLH_TMS_SQLPACKAGE_PATH } else { "SqlPackage" }),
    [int]$DailyRetentionDays = 35,
    [int]$MonthlyRetentionMonths = 12,
    [string]$GitMirrorRoot = $env:SLH_TMS_GIT_MIRROR_ROOT,
    [string]$ApiRepoUrl = "https://github.com/Stuart-Lyons-Haulage/slh-tms-api.git",
    [string]$WebRepoUrl = "https://github.com/Stuart-Lyons-Haulage/slh-tms-web.git",
    [switch]$SkipGitMirror
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Require-Value {
    param([string]$Name, [string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "Missing required setting '$Name'. Supply it as a parameter or environment variable."
    }
}

function Convert-SecureStringToPlainText {
    param([Parameter(Mandatory = $true)][Security.SecureString]$SecureString)
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function Invoke-GitMirror {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryUrl,
        [Parameter(Mandatory = $true)][string]$TargetPath
    )

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw "git is not installed or is not available on PATH."
    }

    if (Test-Path (Join-Path $TargetPath "HEAD")) {
        & git -C $TargetPath remote update --prune
        if ($LASTEXITCODE -ne 0) { throw "git remote update failed for $RepositoryUrl" }
    }
    else {
        $parent = Split-Path -Parent $TargetPath
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
        & git clone --mirror $RepositoryUrl $TargetPath
        if ($LASTEXITCODE -ne 0) { throw "git clone --mirror failed for $RepositoryUrl" }
    }
}

Require-Value "SqlServer / SLH_TMS_SQL_SERVER" $SqlServer
Require-Value "Database / SLH_TMS_SQL_DATABASE" $Database
Require-Value "TenantId / SLH_TMS_TENANT_ID" $TenantId
Require-Value "CredentialPath / SLH_TMS_BACKUP_CREDENTIAL_PATH" $CredentialPath
Require-Value "BackupRoot / SLH_TMS_BACKUP_ROOT" $BackupRoot

if (-not (Test-Path $CredentialPath)) {
    throw "The encrypted service-principal credential file does not exist: $CredentialPath"
}
if (-not (Get-Command $SqlPackagePath -ErrorAction SilentlyContinue)) {
    throw "SqlPackage could not be found at '$SqlPackagePath'. Install SqlPackage or set SLH_TMS_SQLPACKAGE_PATH."
}
if (-not (Get-Module -ListAvailable -Name Az.Accounts)) {
    throw "The Az.Accounts PowerShell module is required. Install-Module Az.Accounts -Scope AllUsers"
}

$dailyRoot = Join-Path $BackupRoot "Daily"
$monthlyRoot = Join-Path $BackupRoot "Monthly"
$logRoot = Join-Path $BackupRoot "Logs"
New-Item -ItemType Directory -Force -Path $dailyRoot, $monthlyRoot, $logRoot | Out-Null

$timestamp = Get-Date
$stamp = $timestamp.ToString("yyyyMMdd_HHmmss")
$dayFolder = Join-Path $dailyRoot $timestamp.ToString("yyyy-MM-dd")
New-Item -ItemType Directory -Force -Path $dayFolder | Out-Null

$bacpac = Join-Path $dayFolder "SLH-TMS_$stamp.bacpac"
$manifestPath = Join-Path $dayFolder "SLH-TMS_$stamp.manifest.json"
$logPath = Join-Path $logRoot "SLH-TMS_$stamp.log"

Start-Transcript -Path $logPath -Force | Out-Null
try {
    Write-Host "Starting Stuart Lyons Haulage TMS backup at $($timestamp.ToString('u'))"
    Write-Host "Target: $bacpac"

    $servicePrincipalCredential = Import-Clixml -Path $CredentialPath
    if ($servicePrincipalCredential -isnot [System.Management.Automation.PSCredential]) {
        throw "CredentialPath must contain a PSCredential exported with Export-Clixml."
    }

    Import-Module Az.Accounts -ErrorAction Stop
    $connectParameters = @{
        ServicePrincipal = $true
        Tenant = $TenantId
        Credential = $servicePrincipalCredential
        Scope = "Process"
    }
    Connect-AzAccount @connectParameters | Out-Null

    $accessTokenResult = Get-AzAccessToken -ResourceUrl "https://database.windows.net/"
    $accessToken = if ($accessTokenResult.Token -is [Security.SecureString]) {
        Convert-SecureStringToPlainText $accessTokenResult.Token
    }
    else {
        [string]$accessTokenResult.Token
    }

    if ([string]::IsNullOrWhiteSpace($accessToken)) {
        throw "Azure SQL access token acquisition returned an empty token."
    }

    $sourceConnectionString = "Server=tcp:$SqlServer,1433;Initial Catalog=$Database;Encrypt=True;TrustServerCertificate=False;MultipleActiveResultSets=False;Connection Timeout=30;"
    $sqlPackageArguments = @(
        "/Action:Export",
        "/TargetFile:$bacpac",
        "/SourceConnectionString:$sourceConnectionString",
        "/AccessToken:$accessToken"
    )
    & $SqlPackagePath @sqlPackageArguments

    if ($LASTEXITCODE -ne 0) {
        throw "SqlPackage export failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path $bacpac)) {
        throw "SqlPackage completed without producing the expected BACPAC file."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($bacpac)
    try {
        $entryCount = $archive.Entries.Count
        if ($entryCount -lt 2) { throw "BACPAC integrity check failed: archive contains too few entries." }
    }
    finally {
        $archive.Dispose()
    }

    $hash = Get-FileHash -Path $bacpac -Algorithm SHA256
    $file = Get-Item $bacpac
    $manifest = [ordered]@{
        schema = "slh-tms-local-backup-manifest-v1"
        createdAtUtc = (Get-Date).ToUniversalTime().ToString("o")
        sqlServer = $SqlServer
        database = $Database
        bacpacFile = $file.Name
        bytes = $file.Length
        sha256 = $hash.Hash
        archiveEntries = $entryCount
        dailyRetentionDays = $DailyRetentionDays
        monthlyRetentionMonths = $MonthlyRetentionMonths
        gitMirrorsRequested = (-not $SkipGitMirror.IsPresent)
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding UTF8

    Write-Host "BACPAC export passed archive and SHA-256 integrity checks."

    $monthKey = $timestamp.ToString("yyyy-MM")
    $monthlyBacpac = Join-Path $monthlyRoot "SLH-TMS_$monthKey.bacpac"
    $monthlyManifest = Join-Path $monthlyRoot "SLH-TMS_$monthKey.manifest.json"
    if (-not (Test-Path $monthlyBacpac)) {
        Copy-Item $bacpac $monthlyBacpac
        Copy-Item $manifestPath $monthlyManifest
        Write-Host "Created monthly retained copy for $monthKey."
    }

    if (-not $SkipGitMirror.IsPresent) {
        if ([string]::IsNullOrWhiteSpace($GitMirrorRoot)) {
            $GitMirrorRoot = Join-Path $BackupRoot "GitHub"
        }
        New-Item -ItemType Directory -Force -Path $GitMirrorRoot | Out-Null
        Invoke-GitMirror -RepositoryUrl $ApiRepoUrl -TargetPath (Join-Path $GitMirrorRoot "slh-tms-api.git")
        Invoke-GitMirror -RepositoryUrl $WebRepoUrl -TargetPath (Join-Path $GitMirrorRoot "slh-tms-web.git")
        Write-Host "Git repository mirrors updated."
    }

    $dailyCutoff = (Get-Date).AddDays(-[Math]::Abs($DailyRetentionDays))
    Get-ChildItem -Path $dailyRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt $dailyCutoff } |
        ForEach-Object {
            Write-Host "Pruning expired daily backup folder $($_.FullName)"
            Remove-Item -Recurse -Force $_.FullName
        }

    $monthlyCutoff = (Get-Date).AddMonths(-[Math]::Abs($MonthlyRetentionMonths))
    Get-ChildItem -Path $monthlyRoot -File -Filter "SLH-TMS_*.bacpac" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt $monthlyCutoff } |
        ForEach-Object {
            $baseName = [IO.Path]::GetFileNameWithoutExtension($_.Name)
            Write-Host "Pruning expired monthly backup $($_.FullName)"
            Remove-Item -Force $_.FullName
            $sidecar = Join-Path $monthlyRoot "$baseName.manifest.json"
            if (Test-Path $sidecar) { Remove-Item -Force $sidecar }
        }

    Write-Host "SLH TMS backup completed successfully."
}
catch {
    Write-Error $_
    exit 1
}
finally {
    Disconnect-AzAccount -Scope Process -ErrorAction SilentlyContinue | Out-Null
    Stop-Transcript -ErrorAction SilentlyContinue | Out-Null
}
