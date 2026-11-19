var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache").WithImageTag("8.10-alpine");
builder.AddDotnetProject("api", "../Respire.Aspire-Starter.Api/Respire.Aspire-Starter.Api.csproj")
    .WithReference(cache)
    .WaitFor(cache)
    .WithHttpHealthCheck("/health/ready", endpointName: "http");

builder.Build().Run();
