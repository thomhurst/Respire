using System.Net;

namespace Keva.Core.Connection;

public class ConnectionOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 6379;
    public EndPoint? EndPoint { get; set; }
    
    // Connection settings
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int SendBufferSize { get; set; } = 4096;
    public int ReceiveBufferSize { get; set; } = 4096;
    
    // Reconnection settings
    public bool EnableAutoReconnect { get; set; } = true;
    public int MaxReconnectAttempts { get; set; } = 10;
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromMinutes(1);
    public BackoffStrategy ReconnectBackoff { get; set; } = BackoffStrategy.ExponentialWithJitter;
    
    // Health check settings
    public bool EnableHealthChecks { get; set; } = true;
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxFailuresBeforeReconnect { get; set; } = 3;
    
    // Command queueing
    public bool EnableCommandQueueing { get; set; } = true;
    public int MaxQueuedCommands { get; set; } = 1000;
    public TimeSpan QueueTimeout { get; set; } = TimeSpan.FromSeconds(30);
    
    // Authentication
    public string? Password { get; set; }
    public string? Username { get; set; }
    public int Database { get; set; } = 0;
    
    // Protocol
    public int ProtocolVersion { get; set; } = 3; // RESP3 by default
    
    public EndPoint GetEndPoint()
    {
        return EndPoint ?? new DnsEndPoint(Host, Port);
    }
}

public enum BackoffStrategy
{
    Fixed,
    Linear,
    Exponential,
    ExponentialWithJitter
}