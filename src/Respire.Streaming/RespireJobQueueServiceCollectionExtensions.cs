using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Respire.Streaming;

/// <summary>Registers typed job queues using the existing scoped stream worker host.</summary>
public static class RespireJobQueueServiceCollectionExtensions
{
    /// <summary>Registers a named job producer and a hosted worker that explicitly acknowledges, retries, or rejects jobs.</summary>
    /// <remarks>Register IRespireClient separately. Resolve the producer using
    /// GetRequiredKeyedService&lt;RespireJobQueue&lt;T&gt;&gt;(queueName). Each registration needs a distinct queue name
    /// and stream-worker TelemetryName. Serializers can run concurrently. Concurrency is per registration, not global.
    /// New consumer groups must start at the beginning so jobs enqueued before startup are not skipped.</remarks>
    public static IServiceCollection AddRespireJobQueue<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler, T>(
        this IServiceCollection services, string queueName, Func<T, byte[]> serialize, Func<byte[], T> deserialize,
        RespireStreamWorkerOptions? options = null)
        where THandler : class, IRespireStreamHandler<RespireJob<T>>
        => services.AddRespireJobQueue<THandler, T>(queueName, serialize, deserialize, options, null);

    /// <summary>Registers a typed queue with explicit scheduling and retry policy.</summary>
    public static IServiceCollection AddRespireJobQueue<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler, T>(
        this IServiceCollection services, string queueName, Func<T, byte[]> serialize, Func<byte[], T> deserialize,
        RespireStreamWorkerOptions? options, RespireJobQueueOptions? queueOptions)
        where THandler : class, IRespireStreamHandler<RespireJob<T>>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serialize);
        ArgumentNullException.ThrowIfNull(deserialize);
        var stream = RespireJobQueue<T>.GetStreamKey(queueName);
        if (services.Any(service => service.ServiceType == typeof(QueueRegistration)
            && service.ImplementationInstance is QueueRegistration registration && registration.Name == queueName))
            throw new ArgumentException("Each job queue must have a unique name within the service collection.", nameof(queueName));
        options ??= new RespireStreamWorkerOptions { DeliveryLimit = 5 };
        if (options.GroupStart != RespireStreamId.Beginning && options.GroupStart != (RespireStreamId)"0-0")
            throw new ArgumentException("Job queue groups must start at the beginning.", nameof(options));
        queueOptions ??= new();
        queueOptions.Validate();
        options = options with
        {
            DeadLetterStream = options.DeadLetterStream ?? RespireJobQueue<T>.GetDeadLetterStreamKey(queueName),
            JobEventsStream = stream + ":events",
            TraceParentField = "traceparent",
            TraceStateField = "tracestate",
            JobQueue = queueOptions,
        };
        services.AddRespireStreamWorker<THandler, RespireJob<T>>(stream, RespireJobQueue<T>.ConsumerGroup,
            entry => RespireJobQueue<T>.Deserialize(entry, deserialize), options);
        services.AddSingleton(new QueueRegistration(queueName));
        services.AddKeyedSingleton<RespireJobQueue<T>>(queueName, (provider, _) =>
            new(provider.GetRequiredService<IRespireClient>(), queueName, serialize));
        return services;
    }

    private sealed record QueueRegistration(string Name);
}
