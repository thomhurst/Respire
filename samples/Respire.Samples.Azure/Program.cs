using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Respire;
using Respire.Extensions.Azure;
using Respire.Extensions.DependencyInjection;

var credential = new DefaultAzureCredential();
var endpoint = Environment.GetEnvironmentVariable("AZURE_MANAGED_REDIS_HOST") ?? "my-cache.redis.azure.net";
var redisUserObjectId = Environment.GetEnvironmentVariable("AZURE_MANAGED_REDIS_USER_OBJECT_ID")
    ?? "<managed-identity-or-service-principal-object-id>";

// Direct client configuration. The credential remains owned by this application.
var options = new RespireOptions
{
    Endpoints = { new RespireEndpoint(endpoint, 10000) },
    UseTls = true,
    Protocol = RespProtocol.Resp3,
    CredentialProvider = new AzureManagedRedisCredentialProvider(credential, redisUserObjectId),
};

// The same options and provider can be registered through dependency injection.
var services = new ServiceCollection();
services.AddRespire(_ => options);

// Resolve and use IRespireClient from the application's service provider when connected to Azure.
Console.WriteLine($"Configured Azure Managed Redis at {endpoint} using {AzureManagedRedisCredentialProvider.Scope}.");
