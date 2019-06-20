# DTL-SecureArtifactData
This sample shows how to create an artifact that can access a keyvault that contains necessary information to mount and Azure Files share on the lab virtual machine.

## EnableVmMSI
Contains the function app code to enable the VM identity and add the Key Vault access policy.

## Setup
1. Fork this repository.
1. Change the default value for `githubRepoUrl` in `Deployment/DeploySystem - NoSP.json` to be the newly created repository.

1. Run the deploy.ps1.  This will created system to enable artifact to run with access to specific Azure Resources.  Resources created include:
    - EventGrid subscription to fire EnableVMMSI function.
    - EnableVMMSI 
    - {baseSystemName}kv - Keyvault to hold secrets that need to be accessed by artifact.
1. Add artifact repository to created in first step.
1. Change the hard-coded value for `$KeyVault` in `/Artifacts/windows-user-drive/artifact.ps1` to be '{baseSystemName}kv'.  Push change to repository.
1. Create storage account to be used for developer file shares.
    - Create file shares for each developer. Add tag with developer name for tracking later.
1. Add secret to keyvault 'DevFilesStorageAccountName' to '{baseSystemName}kv' which is the name of the storage account that hold developer file shares.
1. Add secret to keyvault 'DevFilesStorageAccountKey' to '{baseSystemName}kv' which is key to the storage account that hold developer file shares.


1. Modify DevTest Lab instance to have secret with developer fileshare name.




##Troubleshooting Tips
- The EnableVmMsi Azure Function automatically revokes the system managed identity for the virtual machine after a set time.  Try extending this timeout if logs indicate artifact is not completing before managed identity is removed.  Be aware of maximum timeouts for functions, which is based on the type of hosting plan.




