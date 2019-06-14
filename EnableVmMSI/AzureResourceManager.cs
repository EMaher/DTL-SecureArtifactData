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

                log.LogInformation("[EnableVmMSIFunction] Before Get Lab URL:" + DateTime.Now.ToString());

                var response = await new Url($"https://management.azure.com/subscriptions/{resourceInfo.SubscriptionId}/providers/Microsoft.DevTestLab/labs")
                        .WithOAuthBearerToken(_accessToken)
                        .SetQueryParams(expandProperty)
                        .GetStringAsync();

                log.LogInformation("[EnableVmMSIFunction] After Get Lab URL: " + DateTime.Now.ToString() + " : " + response.ToString());
               
                JObject vmsObject = JObject.Parse(response);

                log.LogInformation("[EnableVmMSIFunction] After Parsing objects:" + DateTime.Now.ToString() + ":" + vmsObject);

                JArray vms = (JArray)vmsObject.SelectToken("value");

                log.LogInformation("[EnableVmMSIFunction] After Parsing VMs: " + DateTime.Now.ToString() + ":" + vms.Count.ToString());

                foreach (JToken lab in vms.Children())
                {

                    int first = 0;
                    string labRg = "";
                    string labName = "";
                    log.LogInformation("[EnableVmMSIFunction] For Each VM: " + DateTime.Now.ToString());
                    // The vmCreationResourceGroupId is the property where the VMs are created.
                    JToken rgId = lab.SelectToken("$.properties.vmCreationResourceGroupId");
                    log.LogInformation("[EnableVmMSIFunction] RG Id: " + DateTime.Now.ToString() + ":" + rgId);
                   
                    if (rgId != null)
                    {
                        first = (rgId.ToString().IndexOf("resourceGroups/") + 15);
                        labRg = rgId.ToString().Substring(first, (rgId.ToString().Length - first));

                        log.LogInformation("[EnableVmMSIFunction] After labName:" + labName + " : " + DateTime.Now.ToString());

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
            List<string> allVms = await GetArtifactInfoAsync(resourceInfo);

            if (allVms.Count > 0)
            {
                foreach (string vmResourceId in allVms)
                {
                    if (!string.IsNullOrWhiteSpace(vmResourceId))
                    {
                        try
                        {
                            var vm = await _msiazure.VirtualMachines.GetByIdAsync(vmResourceId);

                            if (!vm.IsManagedServiceIdentityEnabled)
                            {
                                // Don't await this call as issue where hangs, handle manually below
                                vm.Update().WithSystemAssignedManagedServiceIdentity().ApplyAsync();
                                // Handle await manually.
                                TimeSpan timeSpan = new TimeSpan(0, 0, 10);
                                int counter = 0;
                                await Task.Delay(timeSpan);
                                while ((!vm.IsManagedServiceIdentityEnabled) || (String.IsNullOrEmpty(vm.SystemAssignedManagedServiceIdentityPrincipalId)))
                                {
                                    counter++;
                                    await Task.Delay(timeSpan);
                                    log.LogInformation("[EnableVmMSIFunction] Enable MSI loop: " + DateTime.Now.ToString() + ": counter=" + counter);
                                    await vm.RefreshAsync();
                                    if (counter == 20)
                                    {
                                        break;
                                    }
                                }

                            }

                            await vm.RefreshAsync();
                            log.LogInformation("[EnableVmMSIFunction] MSI applied: " + DateTime.Now.ToString() + ": vm=" + vm);
                            // Get the keyvault
                            var _keyVault = _msiazure.Vaults.GetByResourceGroup(vault.KeyVaultResourceGroup, vault.KeyVaultName);
                            log.LogInformation("[EnableVmMSIFunction] Add KeyVault: " + DateTime.Now.ToString());
                            // Add access policy
                            await _keyVault.Update()
                                .DefineAccessPolicy()
                                    .ForObjectId(vm.SystemAssignedManagedServiceIdentityPrincipalId)
                                    .AllowSecretPermissions(SecretPermissions.Get)
                                .Attach()
                                .ApplyAsync();
                            // Remove after 5 min 
                            log.LogInformation("[EnableVmMSIFunction] Add KeyVault Completed: " + DateTime.Now.ToString());
                            await RemoveAccess(vm, _keyVault, log);
                        }
                        catch (Exception e) {
                            log.LogInformation("[EnableVmMSIFunction][Error] " + e.Message);
                        }
                    }
                }
            }
        }

        // Determine the VM that the artifact is being applied to.
        private async Task<List<string>> GetArtifactInfoAsync(AzureResourceInformation resourceInfo)
        {
            List<string> computeId = new List<string>();

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
                    computeId.Add(vm.SelectToken("properties.computeId").Value<string>());
                }

            }

            log.LogInformation("[EnableVmMSIFunction] Getting compute id " + computeId);
            return computeId; 
        }

        // Remove the IMSI from the VM and the KeyVault Access policy
        private async Task RemoveAccess(Microsoft.Azure.Management.Compute.Fluent.IVirtualMachine vm, Microsoft.Azure.Management.KeyVault.Fluent.IVault vault, ILogger log)
        {
            try
            {
                TimeSpan timeSpan = new TimeSpan(0, 5, 0);
                await Task.Delay(timeSpan);
                log.LogInformation("[EnableVmMSIFunction] Cleanup starting: " + DateTime.Now.ToString());
                // Remove Access policy
                await vault.Update()
                    .WithoutAccessPolicy(vm.SystemAssignedManagedServiceIdentityPrincipalId).ApplyAsync();
                await vault.RefreshAsync();
                // Remove VM identity
                await vm.Update().WithoutSystemAssignedManagedServiceIdentity().ApplyAsync();
                log.LogInformation("[EnableVmMSIFunction] Cleanup finished: " + DateTime.Now.ToString());
            }
            catch (Exception e)
            {
                log.LogInformation("[EnableVmMSIFunction] Cleanup Error: " + e.Message);
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
