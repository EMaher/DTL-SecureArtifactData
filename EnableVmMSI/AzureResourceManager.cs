using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.KeyVault.Models;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.KeyVault.Fluent.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Flurl.Http;
using Flurl;
using Microsoft.Azure.Management.Compute.Fluent;
using Microsoft.Azure.Management.KeyVault.Fluent;

namespace EnableVmMSI
{
  
    public sealed class AzureResourceManager
    {
        
        private KeyVaultClient _kv;
        private IAzure _msiazure;
        private string _accessToken;

        
        public AzureResourceManager(AzureResourceInformation resourceId, KeyVaultInformation kvInfo, ILogger log)
        {
 
            Initialize(resourceId, kvInfo, log).Wait();
            if (!String.IsNullOrEmpty(resourceId.LabName))
            {
                AddIMSIToVMAsync(resourceId, kvInfo, log).Wait();
            }
            
        }
        /*
         * Input: AzureResourceInformation, KeyVaultInformation, Logger
         * Get the necessary credential information for VM management and KeyVault access.
         */
        private async Task Initialize(AzureResourceInformation resourceInfo, KeyVaultInformation vault, ILogger log)
        {
            
            // Get the keyvault client
            var azureServiceTokenProvider = new AzureServiceTokenProvider();
            _kv = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(azureServiceTokenProvider.KeyVaultTokenCallback));

            _accessToken = await azureServiceTokenProvider.GetAccessTokenAsync("https://management.azure.com/");

            // Get the LabResourceGroup
            resourceInfo.LabResourceGroup = ParseLabResourceGroup(resourceInfo.ResourceUri);
            resourceInfo.LabName = await GetLabName(resourceInfo, log);

            // Get the management credentials
            MSILoginInformation msiInfo = new MSILoginInformation(MSIResourceType.AppService);
            AzureCredentials _msiazureCred = SdkContext.AzureCredentialsFactory.FromMSI(msiInfo,AzureEnvironment.AzureGlobalCloud);

            _msiazure = Azure.Authenticate(_msiazureCred).WithSubscription(resourceInfo.SubscriptionId);

        }

        // Parse the Lab resource group from the resource id
        private string ParseLabResourceGroup(string resourceId)
        {
            int first = (resourceId.IndexOf("resourceGroups/") + 15);
            return resourceId.Substring(first, resourceId.IndexOf("/", first) - first);

        }

        // Get the lab with the resource group that the CSE is executed in
        private async Task<string> GetLabName(AzureResourceInformation resourceInfo, Microsoft.Extensions.Logging.ILogger log)
        {
            try
            {
                string[] expandProperty = new string[] { "api-version=2018-10-15-preview" };

                log.LogInformation("[EnableVmMSIFunction] Getting Lab Name");

                var response = await new Url($"https://management.azure.com/subscriptions/{resourceInfo.SubscriptionId}/providers/Microsoft.DevTestLab/labs")
                        .WithOAuthBearerToken(_accessToken)
                        .SetQueryParams(expandProperty)
                        .GetStringAsync();

                //log.LogInformation("[EnableVmMSIFunction] After Get Lab URL: " + DateTime.Now.ToString() + " : " + response.ToString());
               
                JObject vmsObject = JObject.Parse(response);

                //log.LogInformation("[EnableVmMSIFunction] After Parsing objects:" + DateTime.Now.ToString() + ":" + vmsObject);

                JArray vms = (JArray)vmsObject.SelectToken("value");

                //log.LogInformation("[EnableVmMSIFunction] After Parsing VMs: " + DateTime.Now.ToString() + ":" + vms.Count.ToString());

                foreach (JToken lab in vms.Children())
                {

                    int first = 0;
                    string labRg = "";
                    // The vmCreationResourceGroupId is the property where the VMs are created.
                    JToken rgId = lab.SelectToken("$.properties.vmCreationResourceGroupId");
                    //log.LogInformation("[EnableVmMSIFunction] RG Id: " + DateTime.Now.ToString() + ":" + rgId);
                   
                    if (rgId != null)
                    {
                        first = (rgId.ToString().IndexOf("resourceGroups/") + 15);
                        labRg = rgId.ToString().Substring(first, (rgId.ToString().Length - first));

                        if (labRg == resourceInfo.LabResourceGroup)
                        {
                            return lab.SelectToken("name").ToString();
                        }
                    }

                }
            }
            catch (Exception e)
            {
                log.LogInformation(e.Message);
            }
            return null;

        }


        // Enable the IMSI on the Vm and add the IMSI id to the keyvault access policy
        public async Task AddIMSIToVMAsync(AzureResourceInformation resourceInfo, KeyVaultInformation vault, ILogger log)
        {
            // Handle multiple VMs in the same lab
            List<string> allVms = await GetArtifactInfoAsync(resourceInfo, log);

            if (allVms.Count > 0)
            {
                foreach (string vmResourceId in allVms)
                {
                    if (!string.IsNullOrWhiteSpace(vmResourceId))
                    {
                        try
                        {
                            log.LogInformation($"[EnableVmMSIFunction] Found vm ({vmResourceId}) with appropriate artifact being applied. ");

                            //Enable MSI for vm
                            IVirtualMachine vm = await _msiazure.VirtualMachines.GetByIdAsync(vmResourceId);
                            await EnableManagedIdentity(vm, log);
                            
                            // ApplyKevault Access.
                            IVault _keyVault = _msiazure.Vaults.GetByResourceGroup(vault.KeyVaultResourceGroup, vault.KeyVaultName);
                            await ApplyKeyvaultPolicy(_keyVault, vm, log);

                            //automatically remove after set time
                            TimeSpan timeSpan = new TimeSpan(0, 20, 0);
                            log.LogInformation($"[EnableVmMSIFunction] Waiting {timeSpan.ToString()} until removing MSI.");
                            await Task.Delay(timeSpan);
                            await RemoveKeyVaultAccess(_keyVault, vm, log);
                            await DisableMSI(vm, log);
                        }
                        catch (Exception e) {
                            log.LogInformation("[EnableVmMSIFunction][Error] " + e.Message);
                        }
                    }
                }
            }
        }

        private async Task ApplyKeyvaultPolicy(IVault kv, IVirtualMachine vm, ILogger log)
        {

            log.LogInformation("[EnableVmMSIFunction] Add KeyVault Policy Started: " + DateTime.Now.ToString());
            // Add access policy
            await kv.Update()
                .DefineAccessPolicy()
                    .ForObjectId(vm.SystemAssignedManagedServiceIdentityPrincipalId)
                    .AllowSecretPermissions(SecretPermissions.Get)
                .Attach()
                .ApplyAsync();
           log.LogInformation("[EnableVmMSIFunction] Add KeyVault Policy Completed: " + DateTime.Now.ToString());

        }

        private async Task<Boolean> EnableManagedIdentity(IVirtualMachine vm, ILogger log) {

            log.LogInformation($"[EnableVmMSIFunction] Enable MSI start: {DateTime.Now.ToString()}: vm= {vm}, enabled?={vm.IsManagedServiceIdentityEnabled}");
            if (!vm.IsManagedServiceIdentityEnabled)
            {
                // Don't await this call as issue where hangs, handle manually below
                vm.Update().WithSystemAssignedManagedServiceIdentity().ApplyAsync();
               
                // Handle await manually.
                TimeSpan timeSpan = new TimeSpan(0, 0, 10);
                int counter = 0;
                await Task.Delay(timeSpan);
                while (counter < 20 && ((!vm.IsManagedServiceIdentityEnabled) || (String.IsNullOrEmpty(vm.SystemAssignedManagedServiceIdentityPrincipalId))))
                {
                    counter++;
                    await Task.Delay(timeSpan);
                    log.LogInformation("[EnableVmMSIFunction] Enable MSI loop: " + DateTime.Now.ToString() + ": counter=" + counter);
                    await vm.RefreshAsync();
                }

            }

            await vm.RefreshAsync();
            log.LogInformation($"[EnableVmMSIFunction] Enable MSI end: {DateTime.Now.ToString()}: vm= {vm}, enabled?={vm.IsManagedServiceIdentityEnabled}");
            return vm.IsManagedServiceIdentityEnabled;
        }


        // Determine the VM that the artifact is being applied to.
        private async Task<List<string>> GetArtifactInfoAsync(AzureResourceInformation resourceInfo, ILogger log)
        {
            List<string> computeIdList = new List<string>();

            string[] expandProperty = new string[] {"$expand=properties($expand=artifacts)", "api-version=2018-10-15-preview"};

            // Get the VMs 
            var response = await new Url($"https://management.azure.com/subscriptions/{resourceInfo.SubscriptionId}/resourceGroups/{resourceInfo.LabResourceGroup}/providers/Microsoft.DevTestLab/labs/{resourceInfo.LabName}/virtualmachines")
                    .WithOAuthBearerToken(_accessToken)
                    .SetQueryParams(expandProperty)
                    .GetStringAsync();

            // Find the vm with the artifact has a status to Installing
            JObject vmsObject = JObject.Parse(response);
            JArray vms = (JArray)vmsObject.SelectToken("value");

            foreach (JToken vm in vms.Children())
            {
                // Check for the artifact and check for installing
                var targetVM = vm.SelectToken("$..artifacts[?(@.artifactTitle == '"+ resourceInfo.ArtifactTitle +"' && @.status == 'Installing')]", false);

                if ((targetVM != null) && (targetVM.HasValues)) 
                {
                    computeIdList.Add(vm.SelectToken("properties.computeId").Value<string>());
                }

            }

            log.LogInformation($"[EnableVmMSIFunction] Found Vms with artifact status of installed.  Compute ids: {String.Join(",", computeIdList)}");
            return computeIdList; 
        }

        // Remove the IMSI from the VM and the KeyVault Access policy
        private async Task RemoveKeyVaultAccess(IVault vault, IVirtualMachine vm, ILogger log)
        {
            try
            {
                log.LogInformation("[EnableVmMSIFunction] Remove Keyvault starting: " + DateTime.Now.ToString());
                // Remove Access policy
                await vault.Update()
                    .WithoutAccessPolicy(vm.SystemAssignedManagedServiceIdentityPrincipalId).ApplyAsync();
                await vault.RefreshAsync();
                log.LogInformation("[EnableVmMSIFunction] Remove Keyvault ending: " + DateTime.Now.ToString());
            }
            catch (Exception e)
            {
                log.LogInformation("[EnableVmMSIFunction] Remove Keyvault Error: " + e.Message);
            }

        }

        private async Task DisableMSI(IVirtualMachine vm, ILogger log)
        {
            try
            {
                log.LogInformation("[EnableVmMSIFunction] Disable MSI starting: " + DateTime.Now.ToString());

                // Remove VM identity
                await vm.Update().WithoutSystemAssignedManagedServiceIdentity().ApplyAsync();
                log.LogInformation("[EnableVmMSIFunction] Disable MSI finished: " + DateTime.Now.ToString());
            }
            catch (Exception e)
            {
                log.LogInformation("[EnableVmMSIFunction] Disable MSI Error: " + e.Message);
            }
        }
    }

    public class AzureResourceInformation
    {
        public string TenantId { get; set; }
        public string SubscriptionId { get; set; }
        public string ResourceUri { get; set; }
        public string LabName { get; set; }
        public string LabResourceGroup { get; set; }
        public string ArtifactTitle { get; set; }
        public string ArtifactFolder { get; set; }
    }

    public class KeyVaultInformation
    {
        public string KeyVaultName { get; set; }
        public string KeyVaultUri { get; set; }
        public string KeyVaultResourceGroup { get; set; }
        //public string KV_SecretName_ServicePrinciple { get; set; }
        //public string KV_SecretName_ServicePrinciplePwd { get; set; }
    }
}
