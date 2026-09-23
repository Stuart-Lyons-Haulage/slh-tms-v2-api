# Stuart Lyons Haulage TMS local disaster-recovery backup

This folder contains the office-server backup job for the TMS.

## What this protects

The primary operational data lives in Azure SQL, not in GitHub. The database contains customer and customer-contact records, drivers, vehicles, trailers, sites, market contacts, staged imports, transport orders, loads and stops, integration mappings, driver-status history, tracking data, geofences, geofence visits and ETA snapshots.

The backup job therefore creates two independent recovery copies:

1. A full Azure SQL BACPAC containing database schema and user data.
2. Local Git mirrors of `slh-tms-api` and `slh-tms-web`.

Azure SQL automated backups remain the first recovery option. The office-server copy is an additional off-cloud recovery layer.

## Backup layout

The default layout below is created under `SLH_TMS_BACKUP_ROOT`:

```text
TMS Backups/
  Daily/
    2026-08-18/
      SLH-TMS_20260818_230000.bacpac
      SLH-TMS_20260818_230000.manifest.json
  Monthly/
    SLH-TMS_2026-08.bacpac
    SLH-TMS_2026-08.manifest.json
  GitHub/
    slh-tms-api.git/
    slh-tms-web.git/
  Logs/
    SLH-TMS_20260818_230000.log
```

Daily copies default to 35 days. The first successful backup in each month is also retained for 12 months. Each database backup is opened as a ZIP/BACPAC integrity check and receives a SHA-256 manifest.

## Security

BACPAC files contain TMS business and personal data and are not an encrypted backup format. The destination volume must therefore be access-controlled and encrypted at rest (for example BitLocker on the Windows Server volume). Restrict the backup directory to the backup service account and authorised TMS/IT administrators.

Do not place passwords, client secrets, SQL connection strings or Azure tokens in this repository or in the Scheduled Task command line.

## Prerequisites on the office server

- Windows Server with a dedicated Windows service account for the backup task.
- PowerShell.
- `SqlPackage` installed and on PATH, or `SLH_TMS_SQLPACKAGE_PATH` pointing to it.
- `Az.Accounts` installed for all users.
- Git installed if repository mirroring is enabled.
- Network access from the office server to Azure SQL on TCP 1433.
- The office public IP permitted by the Azure SQL server firewall, unless a private network path is later introduced.
- A dedicated Microsoft Entra application/service principal for TMS backup only.
- A database user for that service principal with the permissions required to export schema and table data. Grant only the minimum export permissions needed; do not reuse the deployment identity.

## Store the service-principal credential

Run the following once while signed in as the same Windows account that will run the Scheduled Task:

```powershell
New-Item -ItemType Directory -Force C:\ProgramData\SLH\TMS | Out-Null
$credential = Get-Credential -UserName '<BACKUP-APP-CLIENT-ID>' -Message 'Enter the TMS backup app client secret'
$credential | Export-Clixml C:\ProgramData\SLH\TMS\backup-service-principal.xml
```

`Export-Clixml` protects the credential for that Windows identity. Keep the file ACL restricted to the backup service account and administrators.

## Server settings

Set these as machine-level environment variables or pass them as Scheduled Task parameters:

```text
SLH_TMS_SQL_SERVER=<server>.database.windows.net
SLH_TMS_SQL_DATABASE=<database>
SLH_TMS_TENANT_ID=<tenant-guid>
SLH_TMS_BACKUP_CREDENTIAL_PATH=C:\ProgramData\SLH\TMS\backup-service-principal.xml
SLH_TMS_BACKUP_ROOT=D:\SLH Backups\TMS
SLH_TMS_GIT_MIRROR_ROOT=D:\SLH Backups\TMS\GitHub
```

If `SqlPackage` is not on PATH:

```text
SLH_TMS_SQLPACKAGE_PATH=C:\Program Files\Microsoft SQL Server\170\DAC\bin\SqlPackage.exe
```

## First controlled test

Run manually before scheduling:

```powershell
PowerShell.exe -NoProfile -ExecutionPolicy Bypass -File .\Invoke-SlhTmsBackup.ps1
```

A successful test must produce:

- a non-empty `.bacpac` file;
- a matching `.manifest.json` file containing its SHA-256 hash;
- a successful transcript in `Logs`;
- both Git mirrors if Git mirroring is enabled.

Do not create the recurring Scheduled Task until this controlled run has succeeded and a test import/restore of the BACPAC has also been proven in a non-production database.

## Suggested Scheduled Task

After the controlled test, schedule the task once per day outside the busiest planning window, for example 23:00 local time. Run whether the user is logged on or not and run under the dedicated backup service account.

Program:

```text
PowerShell.exe
```

Arguments:

```text
-NoProfile -ExecutionPolicy Bypass -File "C:\Program Files\SLH TMS\Invoke-SlhTmsBackup.ps1"
```

Configure the task to fail visibly and notify IT/management if the script exits non-zero. A backup job that has not succeeded within 26 hours should be treated as an operational exception.

## Restore drill

At least quarterly:

1. Copy one retained BACPAC into an isolated recovery location.
2. Validate its SHA-256 value against the manifest.
3. Import it into a new non-production SQL database.
4. Confirm core counts for customers, drivers, orders, loads and historic records.
5. Record the test date and result.

A backup is only considered proven once a restore drill has succeeded.
