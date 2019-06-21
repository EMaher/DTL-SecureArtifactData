# DTL-SecureArtifactData
This sample shows how to create an artifact that can access a keyvault that contains necessary information to mount and Azure Files share on the lab virtual machine.

The system is set up so each developer can mount his/her file share.  However, since account storage keys are used when performing the mount operation, this is a logical separation between developers and not a security separation.

## Setup
1. Fork this repository.
1. Change the default value for `githubRepoUrl` in `Deployment/DeploySystem - NoSP.json` to be the newly created repository.
1. Run the deploy.ps1.  This will created system to enable artifact to run with access to specific Azure Resources.  Resources created include:
    - EventGrid subscription to fire EnableVMMSI function.
    - EnableVMMSI  azure function, which gives the lab virtual machine access to necessary secrets to mount the Azure file share.
    - {baseSystemName}kv - Keyvault to hold secrets that need to be accessed by artifact.
    - DevTest Lab instance
1. Add artifact repository to created in first step.  See instrunctions at https://docs.microsoft.com/en-us/azure/lab-services/add-artifact-repository. 
1. Change the hard-coded value for `$KeyVault` in `/Artifacts/windows-user-drive/user-drive.ps1` and `/Artifacts/windows-user-drive/artifact.ps1` to be '{baseSystemName}kv'.  Push change to repository.
1. Create storage account to be used for developer file shares.
    - Create file shares for each developer. Add tag with developer name for tracking later.
1. Add secret to keyvault 'DevFilesStorageAccountName' to '{baseSystemName}kv' which is the name of the storage account that holds developer file shares.
1. Add secret to keyvault 'DevFilesStorageAccountKey' to '{baseSystemName}kv' which is key to the storage account that holds developer file shares.

## How to use
1. Developer must apply the 'Create User Mounted Drive' artifact to their lab virtual machine.
2. After artifact is applied, developer must log into his/her machine then run `C:\DeveloperDrive\user-drive.ps1`.  
     This operation should be done immediately after applying the artifact as the virtual machine will only be given access the key vault temporarily.

## Troubleshooting Tips
- The EnableVmMsi Azure Function automatically revokes the system managed identity for the virtual machine after a set time.  Try extending this timeout if logs indicate artifact is not completing before managed identity is removed.  Be aware of maximum timeouts for functions, which is based on the type of hosting plan.
- Make sure access key for file share doesn't have leading slashes.  




