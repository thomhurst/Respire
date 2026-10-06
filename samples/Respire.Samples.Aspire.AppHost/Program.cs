var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("cache").WithImageTag("8.10-alpine");
var valkey = builder.AddValkey("valkey").WithImageTag("9-alpine");

builder.AddProject<Projects.Respire_Samples_Aspire_Api>("api")
    .WithReference(redis).WaitFor(redis)
    .WithReference(valkey).WaitFor(valkey)
    .WithHttpHealthCheck("/health", endpointName: "http");

builder.Build().Run();
