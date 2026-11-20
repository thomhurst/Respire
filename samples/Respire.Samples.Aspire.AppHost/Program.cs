var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("cache").WithImageTag("8.10-alpine");
var valkey = builder.AddValkey("valkey").WithImageTag("9-alpine");

builder.AddDotnetProject("api", "../Respire.Samples.Aspire.Api/Respire.Samples.Aspire.Api.csproj")
    .WithReference(redis).WaitFor(redis)
    .WithReference(valkey).WaitFor(valkey)
    .WithHttpHealthCheck("/health", endpointName: "http");

builder.Build().Run();
