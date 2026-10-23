# SharePoint driver register sync

This optional scheduled job projects the TMS driver master into the SharePoint SLH Driver Register using Microsoft Graph. Sage HR and Tachomaster continue syncing into TMS first; this job reads the consolidated TMS SQL driver table and does not call either provider directly.

## Data mapping

| TMS driver field | SharePoint field | Sync behaviour |
| --- | --- | --- |
| DisplayName | Title | Create or update |
| EmployeeNumber | Employee Number | Match key; create or update |
| Active | Driver Status | Writes the configured Active or Inactive label |
| — | Work Email | Never written; remains managed in SharePoint |

Only drivers with both an employee number and display name are considered. Existing rows are matched by normalised employee number. If more than one SharePoint row matches, that driver is skipped. No SharePoint rows are deleted.

Before writing items, the job checks that the configured list contains the required columns and reads the current list. If TMS returns zero drivers, it fails closed without changing SharePoint.

## Configuration

The Container Apps Bicep template creates the optional job and dedicated Graph managed identity. It defaults to disabled. Do not put credentials in source control.

| Setting | Purpose |
| --- | --- |
| sharePointDriverRegisterEnabled | Deployment parameter; must be true to create the scheduled job |
| SharePoint:DriverRegister:Enabled | Runtime opt-in; set by Bicep for the job |
| SharePoint:DriverRegister:ManagedIdentityClientId | Client ID of the dedicated user-assigned identity; set by Bicep |
| SharePoint:DriverRegister:SiteId | SharePoint site ID |
| SharePoint:DriverRegister:ListId | SLH Driver Register list ID |
| SharePoint:DriverRegister:EmployeeNumberField | Internal name for Employee Number; defaults to Employee_x0020_Number |
| SharePoint:DriverRegister:DriverStatusField | Internal name for Driver Status; defaults to Driver_x0020_Status |
| SharePoint:DriverRegister:ActiveStatusValue | Status choice for active drivers; defaults to Active |
| SharePoint:DriverRegister:InactiveStatusValue | Status choice for inactive drivers; defaults to Inactive |

Known SharePoint target IDs:

- Site: stuartlyonshaulage.sharepoint.com,e350e872-0b2b-43a1-8569-270045690fa5,b94ef994-16aa-443b-888b-419b014bbd1f
- List: 13f444a7-9b8f-4445-93db-cad93804a1e8

The job verifies configured column internal names at runtime before writing. Confirm them against the live list schema and confirm that Driver Status accepts Active and Inactive before the first enabled run.

## Microsoft Graph access

The job uses a dedicated Azure user-assigned managed identity, separate from the mailbox polling app and the shared TMS jobs identity. Grant that identity only the required list-scoped Microsoft Graph access to the SLH Driver Register. It needs to read list columns/items and create or update list items. Do not grant broad tenant-wide SharePoint write access.

No Graph client secret is required. Do not reuse the INFO_MAILBOX Graph app for this feed; its access is intended for mailbox polling.

## Rollout

1. Verify the dedicated identity has the list-scoped Graph permission.
2. Verify the shared TMS job identity can retrieve the SQL connection string from Key Vault.
3. Build and deploy the Slh.Tms.Jobs image and Bicep template. Keep sharePointDriverRegisterEnabled false until access and field mappings are checked.
4. Enable the job for a controlled first run, review the summary and resulting list rows, then leave the daily schedule enabled only after validation.

The existing GitHub API deployment workflow deploys the API App Service; it does not deploy this Container Apps Job. No Azure job, Graph permission, or SharePoint data is changed by the code PR alone.
