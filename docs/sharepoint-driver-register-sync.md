# SharePoint driver register sync

This optional scheduled job projects the TMS driver master into the SharePoint SLH Driver Register using Microsoft Graph. Sage HR and Tachomaster continue syncing into TMS first; this job reads the consolidated TMS SQL driver table and does not call either provider directly.

## Data mapping

| TMS driver field | SharePoint field | Sync behavior |
| --- | --- | --- |
| DisplayName | Title | Create or update |
| EmployeeNumber | Employee Number | Match key; create or update |
| Active | Driver Status | Writes the configured Active or Inactive label |
| — | Work Email | Never written; remains managed in SharePoint |

Only drivers with both an employee number and display name are considered. Existing rows are matched by normalized employee number. If more than one SharePoint row matches, that driver is skipped and reported in the job summary. No driver rows in SharePoint are deleted.

Before writing any item, the job checks that the configured list contains the required columns and reads the current list. If TMS returns zero drivers, the job fails closed without changing SharePoint.

## Configuration

Set these values in the job's Azure Container Apps configuration or Key Vault references. Do not put credentials in source control.

| Key | Purpose |
| --- | --- |
| TMS_JOB_KIND | Set to sharepoint-drivers for the job container |
| SharePoint:DriverRegister:Enabled | Explicit opt-in. Must be true; defaults to disabled |
| SharePoint:DriverRegister:TenantId | Microsoft Entra tenant ID |
| SharePoint:DriverRegister:ClientId | App registration client ID |
| SharePoint:DriverRegister:ClientSecret | Secret reference for the app registration |
| SharePoint:DriverRegister:SiteId | SharePoint site ID |
| SharePoint:DriverRegister:ListId | SLH Driver Register list ID |
| SharePoint:DriverRegister:EmployeeNumberField | Internal name for Employee Number; defaults to Employee_x0020_Number |
| SharePoint:DriverRegister:DriverStatusField | Internal name for Driver Status; defaults to Driver_x0020_Status |
| SharePoint:DriverRegister:ActiveStatusValue | Status choice for active drivers; defaults to Active |
| SharePoint:DriverRegister:InactiveStatusValue | Status choice for inactive drivers; defaults to Inactive |

Known SharePoint target IDs at implementation time:

- Site: stuartlyonshaulage.sharepoint.com,e350e872-0b2b-43a1-8569-270045690fa5,b94ef994-16aa-443b-888b-419b014bbd1f
- List: 13f444a7-9b8f-4445-93db-cad93804a1e8

The job verifies configured column internal names at runtime before writing. Confirm them against the list schema during deployment, since displayed column labels do not always match Graph internal names.

## Microsoft Graph access

Use an application identity with the narrowest supported list-scoped Graph permission, assigned only to the SLH Driver Register list. Do not grant broad tenant-wide SharePoint write access for this job. The tenant administrator must grant required consent and resource-specific list assignment before enabling the job.

The job requires read access to list columns/items and write access to list items. It requests a client-credentials token for Microsoft Graph; its application secret should be held in Key Vault and injected into the job environment. If the existing TMS Graph identity already has a suitable list-scoped grant, it can be reused after confirming the grant and access model.

## Rollout

1. Confirm the SharePoint internal column names and that Driver Status accepts the configured Active and Inactive values.
2. Confirm the selected app identity has only the required list-scoped access and its secret is available through Key Vault.
3. Build and deploy the Slh.Tms.Jobs image with TMS_JOB_KIND=sharepoint-drivers; keep SharePoint:DriverRegister:Enabled=false.
4. Run once in a controlled environment, review the job summary and rows, then enable the scheduled job through the normal deployment process.

The code change alone does not create an Azure job, grant Graph permissions, enable execution, or write SharePoint data.
