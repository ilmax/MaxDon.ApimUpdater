using CommandLine;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ApiManagement;
using Azure.ResourceManager.Resources;
using Azure;
using Azure.ResourceManager.ApiManagement.Models;
using Azure.Core.Diagnostics;
using Azure.Core.Pipeline;
using Azure.Core;

namespace MaxDon.ApimUpdater;

[Verb("update", HelpText = "Updates the api of an API Management service")]
internal class UpdateApiCommand : ICommand
{
    [Option("api-name", Required = true, HelpText = "The name of the api in the API Management service.")]
    public string ApiName { get; init; } = null!;

    [Option("svc", Required = true, HelpText = "The url of the downstream service that will be exposed via the API.")]
    public string ServiceUrl { get; init; } = null!;

    [Option("spec-url", Required = true, HelpText = "The url of the downstream service specification e.g. the swagger file.")]
    public string SpecificationUrl { get; init; } = null!;

    [Option("spec-format", Required = true, HelpText = "The type of the specification, possible values are (openapi, openapi+json, openapi+json-link, openapi-link, swagger-json, swagger-link-json, wadl-link-json, wadl-xml, wsdl and wsdl-link).")]
    public string Format { get; init; } = "openapi-link";

    [Option("apim-name", Required = false, HelpText = "The name of the API Management service, if not provided it will pick the only one present in the selected subscription, or throw if more than one are found.")]
    public string? ApiManagementService { get; init; }

    [Option("sub-id", Required = false, HelpText = "The id of the subscription, if not provided it will use the default subscription of the logged in session.")]
    public string? SubscriptionId { get; init; }

    [Option("sub-name", Required = false, HelpText = "The Name of the subscription, used to find the most suitable subscription if more than one are found. This has no effect is sub-id is specified.")]
    public string? SubscriptionName { get; init; }

    [Option("retry", Required = false, HelpText = "The number of times to retry, max allowed valus is 10.")]
    public int Retry { get; init; } = 3;

    [Option("debug", Required = false, HelpText = "Enables detailed logging.")]
    public bool Verbose { get; init; }

    public async Task<int> ExecuteAsync()
    {
        var sanitizer = new SanitazeHttpResponse(ApiName);
        WriteMessage("Finding subscription...");
        ArmClientOptions options = new();
        options.Diagnostics.IsLoggingEnabled = options.Diagnostics.IsLoggingContentEnabled = Verbose;
        options.AddPolicy(sanitizer, HttpPipelinePosition.PerCall);
        ArmClient client = new(GetDefaultCredentials(), default, options);
        if (Verbose)
        {
            using AzureEventSourceListener listener = AzureEventSourceListener.CreateConsoleLogger(System.Diagnostics.Tracing.EventLevel.LogAlways);
            return await RunAsync(client);
        }

        return await RunAsync(client);

        static DefaultAzureCredential GetDefaultCredentials() => new(new DefaultAzureCredentialOptions
        {
            ExcludeVisualStudioCodeCredential = true,
            ExcludeVisualStudioCredential = true
        });
    }

    private async Task<int> RunAsync(ArmClient client)
    {
        var subscription = await GetSubscriptionAsync(client);

        return subscription switch
        {
            null => PrintError(),
            _ => await UpdateApiOnSubscriptionAsync(subscription)
        };

        static int PrintError()
        {
            WriteError("Unable to find a subscription");
            return 1;
        }
    }

    private async Task<SubscriptionResource?> GetSubscriptionAsync(ArmClient client)
    {
        if (!string.IsNullOrEmpty(SubscriptionId))
        {
            var subResponse = await client.GetSubscriptions().GetAsync(SubscriptionId);
            if (subResponse.Value is not null)
            {
                return subResponse.Value;
            }
        }

        var subscription = client.GetDefaultSubscription();
        if (subscription is not null)
        {
            return subscription;
        }

        var subscriptions = client.GetSubscriptions().GetAll();
        return subscriptions.Count() switch
        {
            0 => null,
            1 => subscriptions.Single(),
            _ => PickSubscription(subscriptions)
        };
    }

    private SubscriptionResource? PickSubscription(Pageable<SubscriptionResource> subscriptions)
    {
        if (!string.IsNullOrEmpty(SubscriptionName))
        {
            foreach (var sub in subscriptions)
            {
                if (sub.HasData && sub.Data.DisplayName is string name && name.Contains(SubscriptionName, StringComparison.OrdinalIgnoreCase))
                {
                    WriteMessage($"Found many subscriptions, using: {sub.Data.DisplayName}");
                    return sub;
                }
            }
        }

        return null;
    }

    private async Task<int> UpdateApiOnSubscriptionAsync(SubscriptionResource subscription)
    {
        WriteMessage("Finding API Management...");
        var apiManagementService = GetApiManagement(subscription);
        if (apiManagementService is null)
        {
            WriteMessage("Unable to find an API Management Service");
            return 2;
        }

        return await UpdateApiManagementApiAsync(apiManagementService);
    }

    private ApiManagementServiceResource? GetApiManagement(SubscriptionResource subscription)
    {
        var apiManagementServices = subscription.GetApiManagementServices();

        return apiManagementServices.Count() switch
        {
            0 => null,
            1 => apiManagementServices.Single(),
            _ => PickApiManagementService(apiManagementServices)
        };
    }

    private ApiManagementServiceResource? PickApiManagementService(Pageable<ApiManagementServiceResource> apiManagementServices)
    {
        if (!string.IsNullOrEmpty(ApiManagementService))
        {
            foreach (var service in apiManagementServices)
            {
                if (service.HasData && service.Data.Name is string name && name.Contains(ApiManagementService, StringComparison.OrdinalIgnoreCase))
                {
                    return service;
                }
            }
        }

        return null;
    }

    private async Task<int> UpdateApiManagementApiAsync(ApiManagementServiceResource apiManagementService)
    {
        var api = await apiManagementService.GetApiAsync(ApiName);
        if (api.Value is null)
        {
            WriteError($"Unable to find API with name: {ApiName}");
            return 3;
        }

        var collection = apiManagementService.GetApis();
        ApiCreateOrUpdateContent content = new()
        {
            DisplayName = api.Value.Data.DisplayName,
            Path = api.Value.Data.Path,
            Format = Format,
            Value = SpecificationUrl,
            ServiceUri = new Uri(ServiceUrl),
        };

        foreach (var protocol in api.Value.Data.Protocols)
        {
            content.Protocols.Add(protocol);
        }

        WriteMessage("Updating API...");
        int retry = Math.Min(Retry, 10);

        do
        {
            var result = await UpdateApi(collection, content);
            switch (result)
            {
                case Response r when !r.IsError:
                    WriteMessage("Update API completed successfully");
                    return 0;

                case Response r:
                    WriteError($"Update API failed: {r.Content}");
                    return 0;

                case Exception ex:
                    if (retry == 0)
                    {
                        WriteError(ex.Message);
                    }
                    break;
            }

        } while (retry-- > 0);

        WriteError("Unable to update API");
        return 4;
    }

    private static async Task<object> UpdateApi(ApiCollection collection, ApiCreateOrUpdateContent content)
    {
        try
        {
            var response = await collection.CreateOrUpdateAsync(WaitUntil.Completed, content.Path, content, ETag.All);
            return response.GetRawResponse();
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static void WriteMessage(string message) => Console.WriteLine($"[Informational] {message}");
    private static void WriteError(string message) => Console.Error.WriteLine($"[Error] {message}");
}

// Dirty hack to workaround https://github.com/Azure/azure-sdk-for-net/issues/34627
public class SanitazeHttpResponse : HttpPipelineSynchronousPolicy
{
    private readonly string _apiName;

    public SanitazeHttpResponse(string apiName)
    {
        if (string.IsNullOrEmpty(apiName))
        {
            throw new ArgumentException($"'{nameof(apiName)}' cannot be null or empty.", nameof(apiName));
        }

        _apiName = apiName;
    }

    public override void OnReceivedResponse(HttpMessage message)
    {
        if (message.Request.Method == RequestMethod.Get &&
            message.Request.Uri.Path.EndsWith($"/apis/{_apiName}") &&
            !message.Response.IsError &&
            message.Response?.ContentStream != null)
        {
            var bdoridg = BinaryData.FromStream(message.Response.ContentStream);
            var newContent = bdoridg.ToString().Replace("\"serviceUrl\": \"\"", "\"serviceUrl\": null");
            var bd = BinaryData.FromString(newContent);
            var ms = new MemoryStream(bd.ToArray());
            message.Response.ContentStream = ms;
        }

        base.OnReceivedResponse(message);
    }
}