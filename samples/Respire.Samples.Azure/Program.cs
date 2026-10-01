using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Respire;
using Respire.Extensions.Azure;
using Respire.Extensions.DependencyInjection;

// Set RUN_AZURE_REDIS_SAMPLE=1 to issue a command against a real Azure Managed Redis endpoint.
// Connecting requires AZURE_MANAGED_REDIS_HOST and AZURE_MANAGED_REDIS_USER_OBJECT_ID; without
// RUN_AZURE_REDIS_SAMPLE the sample only demonstrates configuration with placeholder values.
var connect = Environment.GetEnvironmentVariable("RUN_AZURE_REDIS_SAMPLE") == "1";
var endpoint = GetSetting("AZURE_MANAGED_REDIS_HOST", "my-cache.redis.azure.net");
var redisUserObjectId = GetSetting(
    "AZURE_MANAGED_REDIS_USER_OBJECT_ID", "<managed-identity-or-service-principal-object-id>");

var credential = new DefaultAzureCredential();

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

await using var serviceProvider = services.BuildServiceProvider();
var client = serviceProvider.GetRequiredService<IRespireClient>();

if (connect)
{
    await client.PingAsync();
    Console.WriteLine($"Connected to Azure Managed Redis at {endpoint}.");
}
else
{
    Console.WriteLine($"Configured Azure Managed Redis at {endpoint}. Set RUN_AZURE_REDIS_SAMPLE=1 to connect.");
}

string GetSetting(string name, string placeholder)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (!string.IsNullOrWhiteSpace(value))
    {
        return value;
    }

    return connect
        ? throw new InvalidOperationException($"Set {name} when RUN_AZURE_REDIS_SAMPLE=1.")
        : placeholder;
}
