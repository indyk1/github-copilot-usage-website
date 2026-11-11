// Copilot Usage Dashboard: App Service (Linux, .NET 10) + Key Vault, restricted to the IP ranges you list.
// Deploy into an existing, empty resource group:  see docs/DEPLOY.md.

targetScope = 'resourceGroup'

@description('Short lowercase prefix for resource names, for example "copilotusage". Letters and digits only.')
@minLength(3)
@maxLength(14)
param namePrefix string

@description('Azure region. Defaults to the resource group\'s region.')
param location string = resourceGroup().location

@description('App Service plan SKU. Always On needs Basic or higher. This app must run on exactly one instance, so a small SKU is enough.')
@allowed([
  'B1'
  'B2'
  'B3'
  'S1'
  'S2'
  'S3'
  'P0v3'
  'P1v3'
  'P2v3'
])
param skuName string = 'B1'

@description('GitHub organization login whose Copilot usage is shown.')
@minLength(1)
param githubOrg string

@description('GitHub access token. Stored in Key Vault, never in App Settings. Pass it on the command line; do not put it in a parameters file.')
@secure()
param githubToken string

@description('AI credits per Copilot seat per month (Copilot Business: 1900).')
@minValue(1)
param creditsPerSeat int = 1900

@description('Hours between GitHub syncs.')
@minValue(1)
param syncIntervalHours int = 4

@description('''
IP ranges (CIDR, for example "203.0.113.0/24" or "198.51.100.7/32") that may reach the site AND its deployment (SCM) site.
REQUIRED, no default, and must contain at least one entry: the app has no login of its own, so this list is its only protection.
To publish the site to the whole internet on purpose, say so explicitly with ["0.0.0.0/0"]. Everyone who can reach the site can then read the data.
''')
@minLength(1)
param allowedIpRanges array

var suffix = take(uniqueString(resourceGroup().id), 6)
var planName = 'plan-${namePrefix}'
var siteName = 'app-${namePrefix}-${suffix}'
var vaultName = take('kv-${namePrefix}-${suffix}', 24)
var tokenSecretName = 'github-token'
var keyVaultSecretsUser = '4633458b-17de-48b0-ad26-e9e4c3ebbd91' // built-in role "Key Vault Secrets User" (read secrets)

var restrictions = [for (range, i) in allowedIpRanges: {
  name: 'allow-${i + 1}'
  action: 'Allow'
  priority: 100 + i
  ipAddress: range
  description: 'Allowed range ${i + 1}'
}]

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  kind: 'linux'
  sku: {
    name: skuName
    capacity: 1
  }
  properties: {
    reserved: true // Linux
    elasticScaleEnabled: false
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 30
  }
}

resource tokenSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: tokenSecretName
  properties: {
    value: githubToken
    contentType: 'GitHub access token for the Copilot usage sync'
  }
}

resource site 'Microsoft.Web/sites@2023-12-01' = {
  name: siteName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    publicNetworkAccess: 'Enabled' // reachability is governed by the access restrictions below
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      numberOfWorkers: 1
      webSocketsEnabled: true // Blazor Server
      http20Enabled: true
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/healthz'
      ipSecurityRestrictionsDefaultAction: 'Deny'
      scmIpSecurityRestrictionsDefaultAction: 'Deny'
      scmIpSecurityRestrictionsUseMain: false
      ipSecurityRestrictions: restrictions
      scmIpSecurityRestrictions: restrictions
    }
  }
}

// The web app's managed identity may read secrets in this vault, and nothing else.
resource readSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, site.id, keyVaultSecretsUser)
  properties: {
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
  }
}

// App Settings. GitHub__Token is a Key Vault reference (no version, so a rotated secret is picked up).
resource appSettings 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: site
  name: 'appsettings'
  dependsOn: [
    readSecrets
    tokenSecret
  ]
  properties: {
    GitHub__Org: githubOrg
    GitHub__Token: '@Microsoft.KeyVault(VaultName=${vault.name};SecretName=${tokenSecretName})'
    Copilot__CreditsPerSeat: string(creditsPerSeat)
    Sync__IntervalHours: string(syncIntervalHours)
    Demo__Enabled: 'false'
    ConnectionStrings__Default: 'Data Source=/home/data/copilot-usage.db'
    WEBSITES_ENABLE_APP_SERVICE_STORAGE: 'true' // keeps /home (the SQLite file) across restarts
  }
}

output webAppName string = site.name
output webAppHostName string = site.properties.defaultHostName
output keyVaultName string = vault.name
output resourceGroupName string = resourceGroup().name
