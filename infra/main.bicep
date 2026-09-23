param location string = 'uksouth'
param appName string
param sqlServerName string
param sqlAdminLogin string
@secure() param sqlAdminPassword string
param entraTenantId string
param entraAudience string
param entraAllowedDomain string = 'lyonshaulage.com'
param entraLegacyAllowedDomain string = 'stuartlyonshaulage.co.uk'

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = { name: '${appName}-plan' location: location sku: { name: 'B1' tier: 'Basic' } properties: { reserved: true } }
resource app 'Microsoft.Web/sites@2023-12-01' = {
  name: appName
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      alwaysOn: true
      healthCheckPath: '/health'
      appSettings: [
        { name: 'Entra__TenantId', value: entraTenantId }
        { name: 'Entra__Audience', value: entraAudience }
        { name: 'Entra__AllowedDomains__0', value: entraAllowedDomain }
        { name: 'Entra__AllowedDomains__1', value: entraLegacyAllowedDomain }
      ]
    }
  }
}
resource sql 'Microsoft.Sql/servers@2023-08-01-preview' = { name: sqlServerName location: location properties: { administratorLogin: sqlAdminLogin administratorLoginPassword: sqlAdminPassword minimalTlsVersion: '1.2' publicNetworkAccess: 'Enabled' } }
resource db 'Microsoft.Sql/servers/databases@2023-08-01-preview' = { parent: sql name: 'slh-tms' location: location sku: { name: 'Basic' tier: 'Basic' capacity: 5 } }
output apiHost string = app.properties.defaultHostName
output healthUrl string = 'https://${app.properties.defaultHostName}/health'
