@description('Azure Container Apps managed environment resource ID')
param containerAppsEnvironmentId string
@description('User-assigned identity resource ID used for ACR and Key Vault')
param jobIdentityResourceId string
@description('ACR server, for example slhtmsacrprod.azurecr.io')
param registryServer string
@description('Immutable Jobs image, for example slhtmsacrprod.azurecr.io/slh-tms-jobs:<sha>')
param jobImage string
@description('Key Vault secret URI for SQL connection string')
param tmsDbSecretUri string
@description('Key Vault secret URI for RoadTech/Tacho base URL')
param dotBaseUrlSecretUri string
@description('Key Vault secret URI for RoadTech/Tacho API key')
param dotApiKeySecretUri string
@description('Key Vault secret URI for RoadTech company code')
param dotCompanyCodeSecretUri string
@description('Key Vault secret URI for RoadTech username')
param dotUsernameSecretUri string
@description('Key Vault secret URI for RoadTech password')
param dotPasswordSecretUri string
@description('Key Vault secret URI for Sage HR base URL')
param sageBaseUrlSecretUri string
@description('Key Vault secret URI for Sage HR API key')
param sageApiKeySecretUri string
@description('Key Vault secret URI for Fleetio base URL')
param fleetioBaseUrlSecretUri string
@description('Key Vault secret URI for Fleetio API key')
param fleetioApiKeySecretUri string
@description('Key Vault secret URI for Fleetio account token')
param fleetioAccountTokenSecretUri string
@description('Key Vault secret URI for TV wallboard key used by ETA calculation endpoint')
param tvWallboardKeySecretUri string
@description('Production API base URL')
param tmsApiBaseUrl string = 'https://slh-tms-api-prod.gentlepond-08dba66b.uksouth.azurecontainerapps.io'

@description('Enable the optional SLH Driver Register Graph sync job. Leave false until Graph access and Key Vault settings are in place.')
param sharePointDriverRegisterEnabled bool = false
@description('SharePoint site ID')
param sharePointSiteId string = 'stuartlyonshaulage.sharepoint.com,e350e872-0b2b-43a1-8569-270045690fa5,b94ef994-16aa-443b-888b-419b014bbd1f'
@description('SLH Driver Register list ID')
param sharePointListId string = '13f444a7-9b8f-4445-93db-cad93804a1e8'

// Dedicated identity for Microsoft Graph access; keep it separate from the shared TMS job identity.
resource sharePointGraphIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'slh-tms-sharepoint-driver-sync'
  location: resourceGroup().location
}

var jobs = [
  { name: 'slh-tms-job-tachomaster', kind: 'tachomaster', cron: '*/5 * * * *', timeout: 4200 }
  { name: 'slh-tms-job-fleetio', kind: 'fleetio', cron: '5 * * * *', timeout: 3300 }
  // ACA Job cron is UTC. Trigger both possible UTC equivalents of 05:30 Europe/London;
  // SageHrScheduledJob uses the durable sagehrsync ledger and executes only once per London date.
  { name: 'slh-tms-job-sagehr', kind: 'sagehr', cron: '30 4,5 * * *', timeout: 2700 }
  { name: 'slh-tms-job-eta', kind: 'eta', cron: '*/5 * * * *', timeout: 600 }
]

resource scheduledJobs 'Microsoft.App/jobs@2024-03-01' = [for job in jobs: {
  name: job.name
  location: resourceGroup().location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${jobIdentityResourceId}': {} }
  }
  properties: {
    environmentId: containerAppsEnvironmentId
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: job.timeout
      replicaRetryLimit: 1
      scheduleTriggerConfig: {
        cronExpression: job.cron
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        { server: registryServer, identity: jobIdentityResourceId }
      ]
      secrets: [
        { name: 'tms-db', keyVaultUrl: tmsDbSecretUri, identity: jobIdentityResourceId }
        { name: 'dot-base-url', keyVaultUrl: dotBaseUrlSecretUri, identity: jobIdentityResourceId }
        { name: 'dot-api-key', keyVaultUrl: dotApiKeySecretUri, identity: jobIdentityResourceId }
        { name: 'dot-company-code', keyVaultUrl: dotCompanyCodeSecretUri, identity: jobIdentityResourceId }
        { name: 'dot-username', keyVaultUrl: dotUsernameSecretUri, identity: jobIdentityResourceId }
        { name: 'dot-password', keyVaultUrl: dotPasswordSecretUri, identity: jobIdentityResourceId }
        { name: 'sage-base-url', keyVaultUrl: sageBaseUrlSecretUri, identity: jobIdentityResourceId }
        { name: 'sage-api-key', keyVaultUrl: sageApiKeySecretUri, identity: jobIdentityResourceId }
        { name: 'fleetio-base-url', keyVaultUrl: fleetioBaseUrlSecretUri, identity: jobIdentityResourceId }
        { name: 'fleetio-api-key', keyVaultUrl: fleetioApiKeySecretUri, identity: jobIdentityResourceId }
        { name: 'fleetio-account-token', keyVaultUrl: fleetioAccountTokenSecretUri, identity: jobIdentityResourceId }
        { name: 'tv-wallboard-key', keyVaultUrl: tvWallboardKeySecretUri, identity: jobIdentityResourceId }
      ]
    }
    template: {
      containers: [
        {
          name: 'tms-job'
          image: jobImage
          env: [
            { name: 'TMS_JOB_KIND', value: job.kind }
            { name: 'ConnectionStrings__TmsDb', secretRef: 'tms-db' }
            { name: 'Tracking__Dot__Enabled', value: 'true' }
            { name: 'Tracking__Dot__BaseUrl', secretRef: 'dot-base-url' }
            { name: 'Tracking__Dot__ApiKey', secretRef: 'dot-api-key' }
            { name: 'Tracking__Dot__CompanyCode', secretRef: 'dot-company-code' }
            { name: 'Tracking__Dot__Username', secretRef: 'dot-username' }
            { name: 'Tracking__Dot__Password', secretRef: 'dot-password' }
            { name: 'Integrations__TachoMaster__Enabled', value: 'true' }
            { name: 'Integrations__TachoMaster__BaseUrl', secretRef: 'dot-base-url' }
            { name: 'Integrations__TachoMaster__ApiKey', secretRef: 'dot-api-key' }
            { name: 'Integrations__TachoMaster__Username', secretRef: 'dot-username' }
            { name: 'Integrations__TachoMaster__Password', secretRef: 'dot-password' }
            { name: 'Integrations__SageHr__Enabled', value: 'true' }
            { name: 'Integrations__SageHr__BaseUrl', secretRef: 'sage-base-url' }
            { name: 'Integrations__SageHr__ApiKey', secretRef: 'sage-api-key' }
            { name: 'Integrations__SageHr__DriverTeamName', value: 'Drivers' }
            { name: 'Integrations__SageHr__DriverPositionKeyword', value: 'Driver' }
            { name: 'Integrations__Fleetio__Enabled', value: 'true' }
            { name: 'Integrations__Fleetio__BaseUrl', secretRef: 'fleetio-base-url' }
            { name: 'Integrations__Fleetio__ApiKey', secretRef: 'fleetio-api-key' }
            { name: 'Integrations__Fleetio__AccountToken', secretRef: 'fleetio-account-token' }
            { name: 'TmsApi__BaseUrl', value: tmsApiBaseUrl }
            { name: 'TvWallboard__AccessKey', secretRef: 'tv-wallboard-key' }
          ]
          resources: { cpu: json('0.5'), memory: '1Gi' }
        }
      ]
    }
  }
}]

resource sharePointDriverRegisterJob 'Microsoft.App/jobs@2024-03-01' = if (sharePointDriverRegisterEnabled) {
  name: 'slh-tms-job-sharepoint-drivers'
  location: resourceGroup().location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${jobIdentityResourceId}': {}
      '${sharePointGraphIdentity.id}': {}
    }
  }
  properties: {
    environmentId: containerAppsEnvironmentId
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 900
      replicaRetryLimit: 1
      scheduleTriggerConfig: {
        // ACA schedules use UTC. 06:00 UTC is after the daily Sage sync in both GMT and BST.
        cronExpression: '0 6 * * *'
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        { server: registryServer, identity: jobIdentityResourceId }
      ]
      secrets: [
        { name: 'tms-db', keyVaultUrl: tmsDbSecretUri, identity: jobIdentityResourceId }
      ]
    }
    template: {
      containers: [
        {
          name: 'tms-job'
          image: jobImage
          env: [
            { name: 'TMS_JOB_KIND', value: 'sharepoint-drivers' }
            { name: 'ConnectionStrings__TmsDb', secretRef: 'tms-db' }
            { name: 'SharePoint__DriverRegister__Enabled', value: 'true' }
            { name: 'SharePoint__DriverRegister__ManagedIdentityClientId', value: sharePointGraphIdentity.properties.clientId }
            { name: 'SharePoint__DriverRegister__SiteId', value: sharePointSiteId }
            { name: 'SharePoint__DriverRegister__ListId', value: sharePointListId }
          ]
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
        }
      ]
    }
  }
}
