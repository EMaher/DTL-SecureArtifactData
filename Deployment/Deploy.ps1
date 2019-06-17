#Requires -Modules Az.Resources
#Requires -Version 5.1

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
The name of the system
.PARAMETER SystemLocation
The location for the system
.NOTES
The script assumes that a lab does not exists

#>


[CmdletBinding()]
Param(
    [Parameter(Mandatory=$false)][string] $subscriptionId,
    [Parameter(Mandatory=$true)][string] $devTestLabName,
    [Parameter(Mandatory=$true)][string] $devTestLabRG,
    [Parameter(Mandatory=$true)][string] $systemRG,
    [Parameter(Mandatory=$true)][string] $baseSystemName,
    [Parameter(Mandatory=$true)][string] $systemLocation
)


#Install-Module -Name Az -AllowClobber -Force
#Install-Module -Name Az.Resources -AllowClobber -Force

#Import-Module -Name Az
#Import-Module -Name Az.Resources

#Login-AzAccount

#Get context information
if ($subscriptionId -eq ""){
	$subInformation = (Get-AzContext).Subscription
}else{
	$subInformation = Set-AzContext -Subscription $subscriptionId
}

if ($subInformation -eq $null){
	Write-Error "Could not get subscription information.  Run 'Set-AzContext' or pass in SubscriptionId after running Login-AzAccount"
	return
}
Write-Output $"Using context '$(Get-AzContext).Name'"

# Create the resource group  
Write-Verbose "Creating resource groups for lab and system, if needed"
if ((Get-AzResourceGroup -name $devTestLabRG -ErrorAction:SilentlyContinue) -eq $null){
	New-AzResourceGroup -Name $devTestLabRG -Location $systemLocation 
}
if ((Get-AzResourceGroup -name $systemRG -ErrorAction:SilentlyContinue) -eq $null){
	New-AzResourceGroup -Name $systemRG -Location $systemLocation 
}

$systemlocalFile = Join-Path $PSScriptRoot -ChildPath "DeploySystem - NoSP.json"
$lablocalFile = Join-Path $PSScriptRoot -ChildPath "DeployDTLab - NoSP.json"
$gridlocalFile = Join-Path $PSScriptRoot -ChildPath "DeployEventGrid.json"

$keyVaultName = $baseSystemName + "kv"

# Create System
Write-Output "Creating System"
$deployName = $baseSystemName + "system"
Write-Verbose "Deploying System to $systemRG"
$functionAppName = $baseSystemName + 'app'
$systemDeployResult = New-AzResourceGroupDeployment -Name $deployName -ResourceGroupName $systemRG -TemplateFile $systemlocalFile -devTestLabName $devTestLabName -keyVaultName $keyVaultName -appName $functionAppName
#Add output information to resource group
Write-Verbose "Giving function app priveledges to lab and system resource groups"
New-AzRoleAssignment -ObjectId $($systemDeployResult.Outputs.functionAppPrincipalId.Value) -RoleDefinitionName "Contributor" -Scope /subscriptions/$($subInformation.Id)/resourceGroups/$devTestLabRG
New-AzRoleAssignment -ObjectId $($systemDeployResult.Outputs.functionAppPrincipalId.Value) -RoleDefinitionName "Contributor" -Scope /subscriptions/$($subInformation.Id)/resourceGroups/$systemRG

$deployName = $baseSystemName + "lab"
# Create Lab
Write-Output "Creating lab"
Write-Verbose "Deploying lab"
$labDeployResult = New-AzResourceGroupDeployment -Name $deployName -ResourceGroupName $devTestLabRG -TemplateFile $lablocalFile -devTestLabName $devTestLabName

# Get FunctionApp masterkey and create event grid connection.
Write-Verbose "Getting masterkey to FunctionApp"

$azureRmProfile = [Microsoft.Azure.Commands.Common.Authentication.Abstractions.AzureRmProfileProvider]::Instance.Profile
$profileClient = New-Object Microsoft.Azure.Commands.ResourceManager.Common.RMProfileClient($azureRmProfile)
$token = $profileClient.AcquireAccessToken($subInformation.TenantId)
$accessToken = $token.AccessToken

$accessTokenHeader = @{ "Authorization" = "Bearer " + $accessToken }
$adminBearerTokenUri = "https://management.azure.com/subscriptions/$($subInformation.Id)/resourceGroups/$systemRG/providers/Microsoft.Web/sites/$functionAppName/functions/admin/token?api-version=2016-08-01"
$adminBearerToken = Invoke-RestMethod -Method Get -Uri $adminBearerTokenUri -Headers $accessTokenHeader

$masterKeyUri = "https://$functionAppName.azurewebsites.net/admin/host/systemkeys/_master"
$adminTokenHeader = @{ "Authorization" = "Bearer " + $adminBearerToken }

$masterKeys = Invoke-RestMethod -Method Get -Uri $masterKeyUri -Headers $adminTokenHeader

Write-Output "Creating Event grid"
$functionEndPoint = "https://$($baseSystemName)app.azurewebsites.net/runtime/webhooks/EventGrid?functionName=EnableVmMSIFunction&code=$($masterKeys.value)"
Write-Verbose "Function EndPoint: $functionEndPoint"
New-AzDeployment -Location $systemLocation -TemplateFile $gridlocalFile -eventSubName $($baseSystemName + "grid") -endpoint $functionEndPoint

Write-Output "Completed."