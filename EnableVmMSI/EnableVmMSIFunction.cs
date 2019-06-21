// Default URL for triggering event grid function in the local environment.
// http://localhost:7071/runtime/webhooks/EventGrid?functionName=EnableVmMSIFunction
/*

The MIT License (MIT)
Copyright (c) Microsoft Corporation  
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.  

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE. 
*/

using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.EventGrid.Models;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Extensions.Logging;
using System;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;



namespace EnableVmMSI
{
    public static class EnableVmMSIFunction
    {

        [FunctionName("EnableVmMSIFunction")]
        public static void Run([EventGridTrigger]EventGridEvent eventGridEvent, ILogger log)
        {
            log.LogInformation("Starting EnableVmMSIFunction");

            log.LogInformation("Getting keyvault information");
            // Get Environment variables.
            KeyVaultInformation djSecrets = new KeyVaultInformation();
            djSecrets.KeyVaultName = GetEnvironmentVariable("AzureKeyVaultName"); //azureKeyVaultName;
            djSecrets.KeyVaultUri = "https://" + djSecrets.KeyVaultName + ".vault.azure.net";
            djSecrets.KeyVaultResourceGroup = GetEnvironmentVariable("AzureKeyVaultResourceGroup");
            //djSecrets.KV_SecretName_ServicePrinciple = GetEnvironmentVariable("AzureServicePrincipalIdSecretName");
           // djSecrets.KV_SecretName_ServicePrinciplePwd = GetEnvironmentVariable("AzureServicePrincipalCredSecretName");

            // Handle Azure Events
            log.LogInformation("Check event to see if it is the one we expect");
            AzureResourceInformation resourceId = GetVmResourceId(eventGridEvent);
            
            if (!string.IsNullOrWhiteSpace(resourceId.ResourceUri))
            {
                log.LogInformation($"Found event for {resourceId.ResourceUri}");
                AzureResourceManager arm = new AzureResourceManager(resourceId, djSecrets, log);
            }
            log.LogInformation("Ending VMSMSIFunction:" + eventGridEvent.Data.ToString());
        }

        /*
         * Input: EventGrid Event
         * Determine the necessary event - artifacts
         * Parse the information and if the correct artifact folder and lab populate the AzureResourceInformation
         */
        private static AzureResourceInformation GetVmResourceId(EventGridEvent evnt)
        {
            AzureResourceInformation returnData = new AzureResourceInformation();

            returnData.ArtifactTitle = GetEnvironmentVariable("DevTestLabArtifact");
            returnData.ArtifactFolder = GetEnvironmentVariable("DevTestLabArtifactFolder");
            //returnData.LabName = GetEnvironmentVariable("DevTestLabName");

            if (StringComparer.OrdinalIgnoreCase.Equals(evnt.EventType, "Microsoft.Resources.ResourceActionSuccess") &&
                evnt.Data is JObject data &&
                data.TryGetValue("operationName", out var operation) &&
                StringComparer.OrdinalIgnoreCase.Equals(operation.ToString(), "Microsoft.DevTestLab/labs/artifactsources/artifacts/generateArmTemplate/action"))
            {
                returnData.ResourceUri = data.SelectToken("resourceUri")?.ToString();
                returnData.TenantId = data.SelectToken("tenantId")?.ToString();
                returnData.SubscriptionId = data.SelectToken("subscriptionId")?.ToString();

                if (!returnData.ResourceUri.Contains(returnData.ArtifactFolder))
                {
                    returnData.ResourceUri = null;
                }
            }

            return returnData;

        }

        // Get the environment variables.
        public static string GetEnvironmentVariable(string name)
        {
            return System.Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        }
    }
}
