using System.Buffers;
using Keva.Core.Connection;
using Keva.Core.Pipeline;
using Keva.Core.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Keva.Client;

public class KevaClientBuilder
{
    private readonly List<IKevaInterceptor> _interceptors = new();
    private readonly KevaClientOptions _options = new();
    private readonly ConnectionOptions _connectionOptions = new();
    private ConnectionPoolOptions? _poolOptions;
    private IServiceProvider? _serviceProvider;
    
    public KevaClientBuilder ConfigureConnection(Action<ConnectionOptions> configure)
    {
        configure(_connectionOptions);
        return this;
    }
    
    public KevaClientBuilder ConfigurePool(Action<ConnectionPoolOptions> configure)
    {
        _poolOptions ??= new ConnectionPoolOptions();
        configure(_poolOptions);
        return this;
    }
    
    public KevaClientBuilder ConfigureClient(Action<KevaClientOptions> configure)
    {
        configure(_options);
        return this;
    }
    
    public KevaClientBuilder AddInterceptor(IKevaInterceptor interceptor)
    {
        if (interceptor == null)
        {
            throw new ArgumentNullException(nameof(interceptor));
        }

        _interceptors.Add(interceptor);
        return this;
    }
    
    public KevaClientBuilder AddInterceptor<T>() where T : IKevaInterceptor, new()
    {
        _interceptors.Add(new T());
        return this;
    }
    
    public KevaClientBuilder AddInterceptor<T>(Func<IServiceProvider, T> factory) where T : IKevaInterceptor
    {
        if (_serviceProvider == null)
        {
            throw new InvalidOperationException("Service provider is not configured. Use WithServiceProvider first.");
        }

        var interceptor = factory(_serviceProvider);
        _interceptors.Add(interceptor);
        return this;
    }
    
    public KevaClientBuilder WithServiceProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        return this;
    }
    
    public KevaClientBuilder UseEndpoint(string host, int port = 6379)
    {
        _connectionOptions.Host = host;
        _connectionOptions.Port = port;
        return this;
    }
    
    public KevaClientBuilder UsePassword(string password)
    {
        _connectionOptions.Password = password;
        return this;
    }
    
    public KevaClientBuilder UseDatabase(int database)
    {
        _connectionOptions.Database = database;
        return this;
    }
    
    public KevaClientBuilder EnableAutoReconnect(bool enable = true)
    {
        _connectionOptions.EnableAutoReconnect = enable;
        return this;
    }
    
    public KevaClientBuilder EnableHealthChecks(bool enable = true)
    {
        _connectionOptions.EnableHealthChecks = enable;
        return this;
    }
    
    public KevaClientBuilder SetPoolSize(int min, int max)
    {
        _poolOptions ??= new ConnectionPoolOptions();
        _poolOptions.MinConnections = min;
        _poolOptions.MaxConnections = max;
        return this;
    }
    
    public IKevaClient Build()
    {
        // Setup pool options
        _poolOptions ??= new ConnectionPoolOptions();
        _poolOptions.ConnectionOptions = _connectionOptions;
        
        // Update client options
        _options.ConnectionPool = _poolOptions;
        
        // Create connection pool
        var connectionPool = new ConnectionPool(_poolOptions);
        
        // Create terminal handler that executes commands
        InterceptorDelegate terminalHandler = async (CommandInfo commandInfo, CancellationToken ct) =>
        {
            // Build RESP command from CommandInfo (extracted to avoid ref struct in async)
            var commandBytes = BuildRespCommand(commandInfo);
            
            var connection = await connectionPool.AcquireAsync(ct);
            try
            {
                return await connection.ExecuteAsync(commandBytes, ct);
            }
            finally
            {
                await connectionPool.ReleaseAsync(connection);
            }
        };
        
        // Build interceptor chain
        var chainBuilder = InterceptorChain.CreateBuilder(terminalHandler);
        foreach (var interceptor in _interceptors)
        {
            chainBuilder.Add(interceptor);
        }
        var chain = chainBuilder.Build();
        
        return new KevaClient(connectionPool, chain, _options);
    }
    
    public static KevaClientBuilder Create()
    {
        return new KevaClientBuilder();
    }
    
    // Convenience factory method
    public static IKevaClient CreateClient(string host, int port = 6379)
    {
        return Create()
            .UseEndpoint(host, port)
            .Build();
    }
    
    private static ReadOnlyMemory<byte> BuildRespCommand(CommandInfo commandInfo)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        var writer = new RespWriter(buffer);
        writer.WriteCommand(commandInfo.Command, commandInfo.Arguments);
        return buffer.WrittenMemory;
    }
}