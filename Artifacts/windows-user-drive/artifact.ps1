[CmdletBinding()]
param
(
    [Parameter(Mandatory=$true)][string]$fileShareName
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

trap {
    # NOTE: This trap will handle all errors. There should be no need to use a catch below in this
    #       script, unless you want to ignore a specific error.
    $message = $error[0].Exception.Message
    if ($message) {
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

Write-Output "Copy file to know location"
New-Item -Path "$env:SystemDrive\" -Name "DeveloperDrive" -ItemType Directory -Force

$origScriptLocation = Join-Path $PSScriptRoot  "user-drive.ps1"
$devScriptLocation =  "$env:SystemDrive\DeveloperDrive\user-drive.ps1"

Copy-Item -Path  $origScriptLocation -Destination $devScriptLocation



(Get-Content $devScriptLocation).replace('[[sharename]]', $fileShareName) | Set-Content $devScriptLocation