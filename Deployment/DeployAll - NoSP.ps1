<#
The MIT License (MIT)
Copyright (c) Microsoft Corporation  
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.  

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE. 

.SYNOPSIS
This script creates a new environment in the lab using an existing environment template.
.PARAMETER SubscriptionId
The subscription ID that is to be deployed to.
.PARAMETER LabName
The name of the DevTest Lab.
.PARAMETER BaseSystemName
The name of the Domain join system
.PARAMETER SystemLocation
The location for the Domain join system
.PARAMETER ServicePrincipalPassword
The password for the service principal
.PARAMETER ArtifactRepo
The DevTest Lab artifact repository uri.
.PARAMETER ArtifactRepoPAT
The access token for the DevTest Lab artifact repository
.NOTES
The script assumes that a lab does not exists

#>


Param(
    [Parameter(Mandatory=$true)][string] $subscriptionId,
    [Parameter(Mandatory=$true)][string] $devTestLabName,
    [Parameter(Mandatory=$true)][string] $devTestLabRG,
    [Parameter(Mandatory=$true)][string] $systemRG,
    [Parameter(Mandatory=$true)][string] $baseSystemName,
    [Parameter(Mandatory=$true)][string] $systemLocation,
    [Parameter(Mandatory=$true)][string] $domainJoinId,
    [Parameter(Mandatory=$true)][securestring] $domainJoinSecret
)


Install-Module -Name Az -AllowClobber -Force
Install-Module -Name Az.Resources -AllowClobber -Force

Import-Module -Name Az
Import-Module -Name Az.Resources


Login-AzAccount

$subInformation = Set-AzContext -Subscription $subscriptionId

# Create the resource group 
New-AzResourceGroup -Name $devTestLabRG -Location $systemLocation
New-AzResourceGroup -Name $systemRG -Location $systemLocation

$systemlocalFile = Join-Path $PSScriptRoot -ChildPath "DeploySystem - NoSP.json"
$lablocalFile = Join-Path $PSScriptRoot -ChildPath "DeployDTLab - NoSP.json"
$gridlocalFile = Join-Path $PSScriptRoot -ChildPath "DeployEventGrid.json"

$keyVaultName = $baseSystemName + "kv"

# Create System
$deployName = $baseSystemName + "system"
$systemDeployResult = New-AzResourceGroupDeployment -Name $deployName -ResourceGroupName $systemRG -TemplateFile $systemlocalFile -devTestLabName $devTestLabName -keyVaultName $keyVaultName -appName $($baseSystemName + 'app') -DomainJoinId $domainJoinId -DomainJoinSecret $domainJoinSecret

#Add output information to resource group
$roleResult = New-AzRoleAssignment -ObjectId $($systemDeployResult.Outputs.functionAppPrincipalId.Value) -RoleDefinitionName "Contributor" -Scope /subscriptions/$($subInformation.Subscription.Id)/resourceGroups/$devTestLabRG
$roleResult = New-AzRoleAssignment -ObjectId $($systemDeployResult.Outputs.functionAppPrincipalId.Value) -RoleDefinitionName "Contributor" -Scope /subscriptions/$($subInformation.Subscription.Id)/resourceGroups/$systemRG

$deployName = $baseSystemName + "lab"
# Create Lab
$labDeployResult = New-AzResourceGroupDeployment -Name $deployName -ResourceGroupName $devTestLabRG -TemplateFile $lablocalFile -devTestLabName $devTestLabName


# Get FunctionApp masterkey and create event grid connection.
$deployName = $baseSystemName + "eventgrid"


$azureRmProfile = [Microsoft.Azure.Commands.Common.Authentication.Abstractions.AzureRmProfileProvider]::Instance.Profile
$profileClient = New-Object Microsoft.Azure.Commands.ResourceManager.Common.RMProfileClient($azureRmProfile)
$token = $profileClient.AcquireAccessToken($subInformation.Tenant.Id)
$accessToken = $token.AccessToken

$accessTokenHeader = @{ "Authorization" = "Bearer " + $accessToken }

$azureRmBaseUri = "https://management.azure.com"
$azureRmApiVersion = "2016-08-01"
$azureRmResourceId = "/subscriptions/$subscriptionId/resourceGroups/$systemRG/providers/Microsoft.Web/sites/$($baseSystemName + 'app')"
$azureRmAdminBearerTokenEndpoint = "/functions/admin/token"
$adminBearerTokenUri = $azureRmBaseUri + $azureRmResourceId + $azureRmAdminBearerTokenEndpoint + "?api-version=" + $azureRmApiVersion

$adminBearerToken = Invoke-RestMethod -Method Get -Uri $adminBearerTokenUri -Headers $accessTokenHeader

$functionAppBaseUri = "https://$($baseSystemName + 'app').azurewebsites.net/admin"

$masterKeyEndpoint = "/host/systemkeys/_master"
$masterKeyUri = $functionAppBaseUri + $masterKeyEndpoint

$adminTokenHeader = @{ "Authorization" = "Bearer " + $adminBearerToken }

$masterKeys = Invoke-RestMethod -Method Get -Uri $masterKeyUri -Headers $adminTokenHeader

$funcUri = $('https://' + $baseSystemName + 'app.azurewebsites.net/runtime/webhooks/EventGrid?functionName=EnableVmMSIFunction&code=' + $($masterKeys.value))

#+++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++

$deployEventGrid = New-AzDeployment -Name $deployName -Location $systemLocation -TemplateFile $gridlocalFile  -eventSubname $($baseSystemName + "grid") -endpoint $funcUri

Write-Output "Completed."