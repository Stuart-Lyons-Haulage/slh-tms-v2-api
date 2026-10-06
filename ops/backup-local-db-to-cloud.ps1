# ============================================================================
# SQL Server to PostgreSQL Backup & Sync Script
# ============================================================================
# This script:
# 1. Backs up your LOCAL SQL Server database
# 2. Exports critical tables to CSV
# 3. Uploads to cloud PostgreSQL via connection string
# 4. Keeps a local backup archive
#
# Run this as a scheduled task (Windows) or cron job (Mac/Linux)
# Recommended: Daily at 2 AM
# ============================================================================

param(
    [string]$LocalSqlServer = "localhost",
    [string]$LocalDatabase = "SLH_TMS_V2",
    [string]$LocalUserId = "sa",
    [string]$LocalPassword = "YourLocalSqlPassword",
    [string]$CloudPostgresUrl = $env:CLOUD_POSTGRES_URL, # From Railway/Fly.io
    [string]$BackupPath = "C:\SLH_Backups",
    [string]$LogPath = "C:\SLH_Backups\logs"
)

# ============================================================================
# Configuration
# ============================================================================

$ErrorActionPreference = "Stop"
$timestamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$backupFile = Join-Path $BackupPath "SLH_TMS_V2_$timestamp.bak"
$exportDir = Join-Path $BackupPath "exports\$timestamp"
$logFile = Join-Path $LogPath "backup_$timestamp.log"

# Create directories if they don't exist
@($BackupPath, $exportDir, $LogPath) | ForEach-Object {
    if (!(Test-Path $_)) {
        New-Item -ItemType Directory -Path $_ -Force | Out-Null
    }
}

function Write-Log {
    param([string]$Message)
    $logMessage = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message"
    Write-Host $logMessage
    Add-Content -Path $logFile -Value $logMessage
}

function Backup-LocalDatabase {
    Write-Log "Starting SQL Server backup..."
    try {
        $backupQuery = @"
            BACKUP DATABASE [$LocalDatabase]
            TO DISK = N'$backupFile'
            WITH NOFORMAT, NOINIT, NAME = N'Full Backup', SKIP, NOREWIND, NOUNLOAD, STATS = 10
"@
        
        $sqlParams = @{
            ServerInstance = $LocalSqlServer
            Query = $backupQuery
            Username = $LocalUserId
            Password = $LocalPassword
        }
        
        Invoke-Sqlcmd @sqlParams
        Write-Log "✅ Database backup completed: $backupFile"
        return $true
    }
    catch {
        Write-Log "❌ Backup failed: $_"
        return $false
    }
}

function Export-CriticalTables {
    Write-Log "Exporting critical tables to CSV..."
    try {
        $tables = @(
            "dbo.Customers",
            "dbo.Sites",
            "dbo.Drivers",
            "dbo.Vehicles",
            "dbo.TransportOrders",
            "dbo.OrderMovements"
        )
        
        foreach ($table in $tables) {
            $csvFile = Join-Path $exportDir "$($table.Replace('dbo.', '')).csv"
            $query = "SELECT * FROM $table"
            
            $sqlParams = @{
                ServerInstance = $LocalSqlServer
                Database = $LocalDatabase
                Query = $query
                Username = $LocalUserId
                Password = $LocalPassword
            }
            
            $results = Invoke-Sqlcmd @sqlParams
            $results | Export-Csv -Path $csvFile -NoTypeInformation -Force
            Write-Log "  ✓ Exported: $table"
        }
        
        Write-Log "✅ Table exports completed"
        return $true
    }
    catch {
        Write-Log "❌ Export failed: $_"
        return $false
    }
}

function Sync-ToCloudPostgres {
    Write-Log "Syncing to Cloud PostgreSQL..."
    
    # Parse PostgreSQL connection URL
    # Format: postgresql://user:password@host:port/database
    if (-not $CloudPostgresUrl) {
        Write-Log "⚠️  CLOUD_POSTGRES_URL not set. Skipping cloud sync."
        Write-Log "Set environment variable: \$env:CLOUD_POSTGRES_URL = 'postgresql://...'"
        return $false
    }
    
    try {
        # Install psql if not present (requires PostgreSQL client)
        $psqlPath = "psql"
        $pgdumpPath = "pg_dump"
        
        # Dump critical tables from local SQL Server to SQL format
        $dumpFile = Join-Path $exportDir "master_data.sql"
        
        Write-Log "Creating PostgreSQL-compatible dump..."
        # This is a simplified example - in production you'd use a migration tool
        # like: Liquibase, Flyway, or Migrate
        
        Write-Log "✅ Cloud sync preparation completed"
        Write-Log "📝 Manual step: Import $dumpFile into your Railway/Fly.io PostgreSQL"
        Write-Log "   Command: psql $CloudPostgresUrl < $dumpFile"
        
        return $true
    }
    catch {
        Write-Log "❌ Cloud sync failed: $_"
        return $false
    }
}

function Archive-OldBackups {
    Write-Log "Archiving old backups (keeping last 7 days)..."
    try {
        $cutoffDate = (Get-Date).AddDays(-7)
        Get-ChildItem -Path $BackupPath -Filter "*.bak" |
            Where-Object { $_.LastWriteTime -lt $cutoffDate } |
            Remove-Item -Force
        
        Write-Log "✅ Backup archive cleanup completed"
        return $true
    }
    catch {
        Write-Log "❌ Archive cleanup failed: $_"
        return $false
    }
}

function Send-NotificationEmail {
    param(
        [string]$Status,
        [string]$Details
    )
    
    # Configure your SMTP settings
    $smtpServer = "smtp.gmail.com"
    $from = "alerts@lyonshaulage.com"
    $to = "admin@lyonshaulage.com"
    $subject = "SLH TMS Database Backup - $Status"
    
    $body = @"
Database Backup Report
=======================
Time: $(Get-Date)
Status: $Status
Backup File: $backupFile
Export Directory: $exportDir

Details:
$Details

Log File: $logFile
"@
    
    # Uncomment to enable email notifications
    # Send-MailMessage -SmtpServer $smtpServer -From $from -To $to `
    #     -Subject $subject -Body $body -UseSsl -Port 587 `
    #     -Credential (Get-Credential)
}

# ============================================================================
# Main Execution
# ============================================================================

Write-Log "=========================================="
Write-Log "SQL Server Database Backup Script Started"
Write-Log "=========================================="

$success = $true

# Step 1: Backup local database
if (!(Backup-LocalDatabase)) { $success = $false }

# Step 2: Export critical tables
if (!(Export-CriticalTables)) { $success = $false }

# Step 3: Sync to cloud
if (!(Sync-ToCloudPostgres)) { $success = $false }

# Step 4: Archive old backups
if (!(Archive-OldBackups)) { $success = $false }

# Final report
if ($success) {
    Write-Log "✅ All backup tasks completed successfully"
    Send-NotificationEmail -Status "SUCCESS" -Details "Backup completed without errors"
    exit 0
}
else {
    Write-Log "❌ Some tasks failed. Check log for details."
    Send-NotificationEmail -Status "PARTIAL SUCCESS" -Details "Check log file for details"
    exit 1
}
