var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache").WithImageTag("8.10-alpine");
builder.AddProject<Projects.Api>("api")
    .WithReference(cache)
    .WaitFor(cache)
    .WithHttpHealthCheck("/health/ready", endpointName: "http");

builder.Build().Run();
