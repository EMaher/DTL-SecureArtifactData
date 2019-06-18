[CmdletBinding()]
param
(
)


###################################################################################################
#
# PowerShell configurations
#

# NOTE: Because the $ErrorActionPreference is "Stop", this script will stop on first failure.
#       This is necessary to ensure we capture errors inside the try-catch-finally block.
$ErrorActionPreference = "Stop"

# Ensure we set the working directory to that of the script.
Push-Location $PSScriptRoot

###################################################################################################
#
# Handle all errors in this script.
#

trap
{
    # NOTE: This trap will handle all errors. There should be no need to use a catch below in this
    #       script, unless you want to ignore a specific error.
    $message = $error[0].Exception.Message
    if ($message)
    {
        Write-Host -Object "ERROR: $message" -ForegroundColor Red
    }
    
    # IMPORTANT NOTE: Throwing a terminating error (using $ErrorActionPreference = "Stop") still
    # returns exit code zero from the PowerShell script when using -File. The workaround is to
    # NOT use -File when calling this script and leverage the try-catch-finally block and return
    # a non-zero exit code from the catch block.
    Write-Host 'Artifact failed to apply.'
    exit -1
}

###################################################################################################
#
# Functions used in this script.
#


###################################################################################################
#
# Main execution block.
#
$MaxRetries = 40
$currentRetry = 0

$KeyVaultName = "fileB2kv"



if ($PSVersionTable.PSVersion.Major -lt 3)
{
    throw "The current version of PowerShell is $($PSVersionTable.PSVersion.Major). Prior to running this artifact, ensure you have PowerShell 3 or higher installed."
}
      
	  
$KeyVaultToken = $null
$success = $false
Write-Output "$(Get-Date) Start: Getting token for access to keyvault"
do {
    try
    {

		# Get KeyVault token as the VM identity       
		$response = Invoke-WebRequest -Uri 'http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=https%3A%2F%2Fvault.azure.net' -Method GET -Headers @{Metadata="true"} -UseBasicParsing
		$content = $response.Content | ConvertFrom-Json
		$KeyVaultToken = $content.access_token
		Write-Output "Token: $KeyVaultToken"
		$success = $true
	}
	catch {
        $currentRetry = $currentRetry + 1
        Write-Host "In catch $currentRetry $(Get-Date): $ErrorMessage = $($_.Exception.Message)"
        if ($currentRetry -gt $MaxRetries) {
            Write-Error "Failed Max retries"
            exit
        } else {
            Start-Sleep -Seconds 60
        }
    }
}while (!$success)
Write-Output "$(Get-Date) End: Getting token for access to keyvault"

Write-Output "$(Get-Date) Start: Getting secret from keyvault"
$success = $false
$currentRetry = 0
do{
	try{

		$requestUrl = "https://$KeyVaultName.vault.azure.net/secrets/TestAccountCredential?api-version=2016-10-01"
		Write-Output $requestUrl

        # Get KeyVault value	
        $secret1value = Invoke-WebRequest -Uri $requestUrl -Method GET -Headers @{Authorization="Bearer $KeyVaultToken"} -UseBasicParsing | ConvertFrom-Json | select -expand value
	  Write-Host "KeyVault value: $secret1value"
	$success = $true

    }
    catch {
        $currentRetry = $currentRetry + 1
        Write-Host "In catch $currentRetry $(Get-Date): $ErrorMessage = $($_.Exception.Message)"
        if ($currentRetry -gt $MaxRetries) {
            #throw "Failed Max retries"
            Write-Error "Failed Max retries"
            break
        } else {
            Start-Sleep -Seconds 60
        }
    }
} while (!$success)
Write-Output "$(Get-Date) End: Getting secret from keyvault"
