// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR.Internal;
using Microsoft.AspNetCore.SignalR.Protocol;
using Respire.SignalR.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Respire.SignalR;

/// <summary>
/// The Redis scaleout provider for multi-server support.
/// </summary>
/// <typeparam name="THub">The type of <see cref="Hub"/> to manage connections for.</typeparam>
public sealed class RespireHubLifetimeManager<THub> : HubLifetimeManager<THub>, IDisposable, IAsyncDisposable where THub : Hub
{
    private readonly HubConnectionStore _connections = new HubConnectionStore();
    private readonly RedisSubscriptionManager _groups = new RedisSubscriptionManager();
    private readonly RedisSubscriptionManager _users = new RedisSubscriptionManager();
    private readonly RespirePubSub _bus;
    private readonly ILogger _logger;
    private readonly RespireSignalROptions _options;
    private readonly RedisChannels _channels;
    private readonly string _serverName = GenerateServerName();
    private readonly RedisProtocol _protocol;
    private readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1);
    private readonly IHubProtocolResolver _hubProtocolResolver;
    private readonly ClientResultsManager _clientResultsManager = new();
    private bool _initialized;
    private int _disposed;

    private readonly AckHandler _ackHandler;
    private int _internalAckId;

    /// <summary>
    /// Constructs the <see cref="RespireHubLifetimeManager{THub}"/> with types from Dependency Injection.
    /// </summary>
    /// <param name="logger">The logger to write information about what the class is doing.</param>
    /// <param name="options">Backplane options.</param>
    /// <param name="hubProtocolResolver">The <see cref="IHubProtocolResolver"/> to get an <see cref="IHubProtocol"/> instance when writing to connections.</param>
    /// <param name="globalHubOptions">The global <see cref="HubOptions"/>.</param>
    /// <param name="hubOptions">The <typeparamref name="THub"/> specific options.</param>
    /// <param name="client">The shared client. This manager disposes its subscriptions, never the client.</param>
    public RespireHubLifetimeManager(RespireClient client, ILogger<RespireHubLifetimeManager<THub>> logger,
                                   IOptions<RespireSignalROptions> options,
                                   IHubProtocolResolver hubProtocolResolver,
                                   IOptions<HubOptions>? globalHubOptions,
                                   IOptions<HubOptions<THub>>? hubOptions)
    {
        _hubProtocolResolver = hubProtocolResolver;
        _logger = logger;
        var configured = options.Value;
        if (configured.RemoteClientResultTimeout <= TimeSpan.Zero
            || configured.RemoteClientResultTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Remote client result timeout must be positive and within the timer limit.");
        _options = new RespireSignalROptions
        {
            ChannelPrefix = configured.ChannelPrefix,
            SubscriptionBufferSize = configured.SubscriptionBufferSize,
            UseShardedPubSub = configured.UseShardedPubSub,
            GroupAckTimeout = configured.GroupAckTimeout,
            RemoteClientResultTimeout = configured.RemoteClientResultTimeout,
        };
        _bus = new RespirePubSub(client, _options, logger, typeof(THub).FullName!);
        _ackHandler = new AckHandler();
        _channels = new RedisChannels(typeof(THub).FullName!, _serverName);
        if (globalHubOptions != null && hubOptions != null)
        {
            _protocol = new RedisProtocol(new DefaultHubMessageSerializer(hubProtocolResolver, globalHubOptions.Value.SupportedProtocols, hubOptions.Value.SupportedProtocols));
        }
        else
        {
            var supportedProtocols = hubProtocolResolver.AllProtocols.Select(p => p.Name).ToList();
            _protocol = new RedisProtocol(new DefaultHubMessageSerializer(hubProtocolResolver, supportedProtocols, null));
        }

    }

    /// <inheritdoc />
    public override async Task OnConnectedAsync(HubConnectionContext connection)
    {
        await EnsureRedisServerConnection().ConfigureAwait(false);

        var feature = new RedisFeature();
        connection.Features.Set<IRedisFeature>(feature);

        var userTask = Task.CompletedTask;

        _connections.Add(connection);

        var connectionTask = SubscribeToConnection(connection);

        if (!string.IsNullOrEmpty(connection.UserIdentifier))
        {
            userTask = SubscribeToUser(connection);
        }

        try { await Task.WhenAll(connectionTask, userTask).ConfigureAwait(false); }
        catch { await OnDisconnectedAsync(connection).ConfigureAwait(false); throw; }
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(HubConnectionContext connection)
    {
        _connections.Remove(connection);

        // Failed initialization may not have attached this manager's connection state.
        if (connection.Features.Get<IRedisFeature>() is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var connectionChannel = _channels.Connection(connection.ConnectionId);
        var tasks = new List<Task>();

        RedisLog.Unsubscribe(_logger, connectionChannel);
        tasks.Add(_bus.UnsubscribeAsync(connectionChannel));

        var feature = connection.Features.GetRequiredFeature<IRedisFeature>();
        var groupNames = feature.Groups;
        string[] groups;
        await feature.GroupLock.WaitAsync().ConfigureAwait(false);
        try { groups = groupNames.ToArray(); }
        finally { feature.GroupLock.Release(); }
        foreach (var group in groups)
        {
            tasks.Add(RemoveGroupAsyncCore(connection, group));
        }

        if (!string.IsNullOrEmpty(connection.UserIdentifier))
        {
            tasks.Add(RemoveUserAsync(connection));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        var message = _protocol.WriteInvocation(methodName, args);
        return PublishAsync(_channels.All, message, cancellationToken);
    }

    /// <inheritdoc />
    public override Task SendAllExceptAsync(string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default)
    {
        var message = _protocol.WriteInvocation(methodName, args, excludedConnectionIds: excludedConnectionIds);
        return PublishAsync(_channels.All, message, cancellationToken);
    }

    /// <inheritdoc />
    public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionId);

        // If the connection is local we can skip sending the message through the bus since we require sticky connections.
        // This also saves serializing and deserializing the message!
        var connection = _connections[connectionId];
        if (connection != null)
        {
            return connection.WriteAsync(new InvocationMessage(methodName, args), cancellationToken).AsTask();
        }

        var message = _protocol.WriteInvocation(methodName, args);
        return PublishAsync(_channels.Connection(connectionId), message, cancellationToken);
    }

    /// <inheritdoc />
    public override Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupName);

        var message = _protocol.WriteInvocation(methodName, args);
        return PublishAsync(_channels.Group(groupName), message, cancellationToken);
    }

    /// <inheritdoc />
    public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupName);

        var message = _protocol.WriteInvocation(methodName, args, excludedConnectionIds: excludedConnectionIds);
        return PublishAsync(_channels.Group(groupName), message, cancellationToken);
    }

    /// <inheritdoc />
    public override Task SendUserAsync(string userId, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        var message = _protocol.WriteInvocation(methodName, args);
        return PublishAsync(_channels.User(userId), message, cancellationToken);
    }

    /// <inheritdoc />
    public override Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        ArgumentNullException.ThrowIfNull(groupName);

        cancellationToken.ThrowIfCancellationRequested();
        var connection = _connections[connectionId];
        if (connection != null)
        {
            // short circuit if connection is on this server
            return AddGroupAsyncCore(connection, groupName);
        }

        return SendGroupActionAndWaitForAck(connectionId, groupName, GroupAction.Add, cancellationToken);
    }

    /// <inheritdoc />
    public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        ArgumentNullException.ThrowIfNull(groupName);

        cancellationToken.ThrowIfCancellationRequested();
        var connection = _connections[connectionId];
        if (connection != null)
        {
            // short circuit if connection is on this server
            return RemoveGroupAsyncCore(connection, groupName);
        }

        return SendGroupActionAndWaitForAck(connectionId, groupName, GroupAction.Remove, cancellationToken);
    }

    /// <inheritdoc />
    public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionIds);

        var publishTasks = new List<Task>(connectionIds.Count);
        var payload = _protocol.WriteInvocation(methodName, args);

        foreach (var connectionId in connectionIds)
        {
            publishTasks.Add(PublishAsync(_channels.Connection(connectionId), payload, cancellationToken));
        }

        return Task.WhenAll(publishTasks);
    }

    /// <inheritdoc />
    public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupNames);
        var publishTasks = new List<Task>(groupNames.Count);
        var payload = _protocol.WriteInvocation(methodName, args);

        foreach (var groupName in groupNames)
        {
            if (!string.IsNullOrEmpty(groupName))
            {
                publishTasks.Add(PublishAsync(_channels.Group(groupName), payload, cancellationToken));
            }
        }

        return Task.WhenAll(publishTasks);
    }

    /// <inheritdoc />
    public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        if (userIds.Count > 0)
        {
            var payload = _protocol.WriteInvocation(methodName, args);
            var publishTasks = new List<Task>(userIds.Count);
            foreach (var userId in userIds)
            {
                if (!string.IsNullOrEmpty(userId))
                {
                    publishTasks.Add(PublishAsync(_channels.User(userId), payload, cancellationToken));
                }
            }

            return Task.WhenAll(publishTasks);
        }

        return Task.CompletedTask;
    }

    private async Task<long> PublishAsync(string channel, byte[] payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureRedisServerConnection().WaitAsync(cancellationToken).ConfigureAwait(false);
        RedisLog.PublishToChannel(_logger, channel);
        return await _bus.PublishAsync(channel, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddGroupAsyncCore(HubConnectionContext connection, string groupName)
    {
        var feature = connection.Features.GetRequiredFeature<IRedisFeature>();
        var groupNames = feature.Groups;

        await feature.GroupLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (groupNames.Contains(groupName) || connection.ConnectionAborted.IsCancellationRequested) return;
            var groupChannel = _channels.Group(groupName);
            await _groups.AddSubscriptionAsync(groupChannel, connection, SubscribeToGroupAsync).ConfigureAwait(false);
            if (connection.ConnectionAborted.IsCancellationRequested)
                await _groups.RemoveSubscriptionAsync(groupChannel, connection, _bus,
                    static (state, name) => ((RespirePubSub)state).UnsubscribeAsync(name)).ConfigureAwait(false);
            else groupNames.Add(groupName);
        }
        finally { feature.GroupLock.Release(); }
    }

    /// <summary>
    /// This takes <see cref="HubConnectionContext"/> because we want to remove the connection from the
    /// _connections list in OnDisconnectedAsync and still be able to remove groups with this method.
    /// </summary>
    private async Task RemoveGroupAsyncCore(HubConnectionContext connection, string groupName)
    {
        var groupChannel = _channels.Group(groupName);
        var feature = connection.Features.GetRequiredFeature<IRedisFeature>();
        await feature.GroupLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _groups.RemoveSubscriptionAsync(groupChannel, connection, this, static (state, channelName) =>
            {
                var lifetimeManager = (RespireHubLifetimeManager<THub>)state;
                RedisLog.Unsubscribe(lifetimeManager._logger, channelName);
                return lifetimeManager._bus.UnsubscribeAsync(channelName);
            }).ConfigureAwait(false);
            feature.Groups.Remove(groupName);
        }
        finally { feature.GroupLock.Release(); }
    }

    private async Task SendGroupActionAndWaitForAck(string connectionId, string groupName, GroupAction action,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _internalAckId);
        var ack = _ackHandler.CreateAck(id);
        // Send Add/Remove Group to other servers and wait for an ack or timeout
        var message = RedisProtocol.WriteGroupCommand(new RedisGroupCommand(id, _serverName, action, groupName, connectionId));
        try
        {
            await PublishAsync(_channels.GroupManagement, message, cancellationToken).ConfigureAwait(false);
            await ack.WaitAsync(_options.GroupAckTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally { _ackHandler.RemoveAck(id); }
    }

    private Task RemoveUserAsync(HubConnectionContext connection)
    {
        var userChannel = _channels.User(connection.UserIdentifier!);

        return _users.RemoveSubscriptionAsync(userChannel, connection, this, static (state, channelName) =>
        {
            var lifetimeManager = (RespireHubLifetimeManager<THub>)state;
            RedisLog.Unsubscribe(lifetimeManager._logger, channelName);
            return lifetimeManager._bus.UnsubscribeAsync(channelName);
        });
    }

    /// <summary>
    /// Cleans up the Redis connection.
    /// </summary>
    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Stops and joins this manager's subscription consumers without disposing the shared client.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var connection in _connections) _connections.Remove(connection);
        _ackHandler.Dispose();
        await _bus.DisposeAsync().ConfigureAwait(false);
        await _clientResultsManager.CompleteAllAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task<T> InvokeConnectionAsync<T>(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(connectionId);

        var connection = _connections[connectionId];

        // ID needs to be unique for each invocation and across servers, we generate a GUID every time, that should provide enough uniqueness guarantees.
        var invocationId = GenerateInvocationId();

        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _bus.Stopping);
        using var _ = CancellationTokenUtils.CreateLinkedToken(shutdown.Token,
            connection?.ConnectionAborted ?? default, out var linkedToken);
        var task = _clientResultsManager.AddInvocation<T>(connectionId, invocationId, linkedToken);

        try
        {
            if (connection == null)
            {
                // TODO: Need to handle other server going away while waiting for connection result
                var messageBytes = _protocol.WriteInvocation(methodName, args, invocationId, returnChannel: _channels.ReturnResults);
                var received = await PublishAsync(_channels.Connection(connectionId), messageBytes, linkedToken).ConfigureAwait(false);
                if (received < 1 && _bus.ReceiverCountIsGlobal)
                {
                    throw new IOException($"Connection '{connectionId}' does not exist.");
                }
            }
            else
            {
                // We're sending to a single connection
                // Write message directly to connection without caching it in memory
                var message = new InvocationMessage(invocationId, methodName, args);

                await connection.WriteAsync(message, linkedToken).ConfigureAwait(false);
            }
        }
        catch
        {
            _clientResultsManager.RemoveInvocation(invocationId);
            throw;
        }

        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            // ConnectionAborted will trigger a generic "Canceled" exception from the task, let's convert it into a more specific message.
            if (connection?.ConnectionAborted.IsCancellationRequested == true)
            {
                throw new IOException($"Connection '{connectionId}' disconnected.");
            }
            throw;
        }
    }

    /// <inheritdoc/>
    public override Task SetConnectionResultAsync(string connectionId, CompletionMessage result)
    {
        return _clientResultsManager.TryCompleteResult(connectionId, result);
    }

    /// <inheritdoc/>
    public override bool TryGetReturnType(string invocationId, [NotNullWhen(true)] out Type? type)
    {
        return _clientResultsManager.TryGetType(invocationId, out type);
    }

    private async Task SubscribeToAll()
    {
        RedisLog.Subscribing(_logger, _channels.All);
        var channel = await _bus.SubscribeAsync(_channels.All).ConfigureAwait(false);
        channel.OnMessage(async channelMessage =>
        {
            try
            {
                RedisLog.ReceivedFromChannel(_logger, _channels.All);

                var invocation = RedisProtocol.ReadInvocation(channelMessage.Message);

                var tasks = new List<Task>(_connections.Count);

                foreach (var connection in _connections)
                {
                    if (invocation.ExcludedConnectionIds == null || !invocation.ExcludedConnectionIds.Contains(connection.ConnectionId))
                    {
                        tasks.Add(connection.WriteAsync(invocation.Message, channelMessage.Stopping).AsTask());
                    }
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RedisLog.FailedWritingMessage(_logger, ex);
            }
        });
    }

    private async Task SubscribeToGroupManagementChannel()
    {
        var channel = await _bus.SubscribeAsync(_channels.GroupManagement).ConfigureAwait(false);
        channel.OnMessage(async channelMessage =>
        {
            try
            {
                var groupMessage = RedisProtocol.ReadGroupCommand(channelMessage.Message);

                var connection = _connections[groupMessage.ConnectionId];
                if (connection == null)
                {
                    // user not on this server
                    return;
                }

                if (groupMessage.Action == GroupAction.Remove)
                {
                    await RemoveGroupAsyncCore(connection, groupMessage.GroupName).ConfigureAwait(false);
                }

                if (groupMessage.Action == GroupAction.Add)
                {
                    await AddGroupAsyncCore(connection, groupMessage.GroupName).ConfigureAwait(false);
                }

                // Send an ack to the server that sent the original command.
                await PublishAsync(_channels.Ack(groupMessage.ServerName), RedisProtocol.WriteAck(groupMessage.Id), channelMessage.Stopping).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RedisLog.InternalMessageFailed(_logger, ex);
            }
        });
    }

    private async Task SubscribeToAckChannel()
    {
        // Create server specific channel in order to send an ack to a single server
        var channel = await _bus.SubscribeAsync(_channels.Ack(_serverName)).ConfigureAwait(false);
        channel.OnMessage(channelMessage =>
        {
            var ackId = RedisProtocol.ReadAck(channelMessage.Message);

            _ackHandler.TriggerAck(ackId);
        });
    }

    private async Task SubscribeToConnection(HubConnectionContext connection)
    {
        var connectionChannel = _channels.Connection(connection.ConnectionId);

        RedisLog.Subscribing(_logger, connectionChannel);
        var channel = await _bus.SubscribeAsync(connectionChannel).ConfigureAwait(false);
        channel.OnMessage(channelMessage =>
        {
            var invocation = RedisProtocol.ReadInvocation(channelMessage.Message);

            // This is a Client result we need to setup state for the completion and forward the message to the client
            if (!string.IsNullOrEmpty(invocation.InvocationId))
            {
                var lifetime = new RemoteInvocationLifetime(_clientResultsManager, invocation.InvocationId,
                    connection.ConnectionAborted, _options.RemoteClientResultTimeout, async completionMessage =>
                {
                    try
                    {
                        var memoryBufferWriter = new ArrayBufferWriter<byte>();
                        connection.Protocol.WriteMessage(completionMessage, memoryBufferWriter);
                        var message = RedisProtocol.WriteCompletionMessage(memoryBufferWriter.WrittenMemory, connection.Protocol.Name);
                        await PublishAsync(invocation.ReturnChannel!, message, _bus.Stopping).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        RedisLog.ErrorForwardingResult(_logger, completionMessage.InvocationId!, ex);
                    }
                });
                try
                {
                    _clientResultsManager.AddInvocation(invocation.InvocationId,
                        (typeof(RawResult), connection.ConnectionId, lifetime,
                            static (state, completion) => ((RemoteInvocationLifetime)state).CompleteAsync(completion)));
                }
                catch { lifetime.DisposeBeforeStart(); throw; }
                lifetime.Start();
            }

            // Forward message from other server to client
            // Normal client method invokes and client result invokes use the same message
            return connection.WriteAsync(invocation.Message, channelMessage.Stopping).AsTask();
        });
    }

    private Task SubscribeToUser(HubConnectionContext connection)
    {
        var userChannel = _channels.User(connection.UserIdentifier!);

        return _users.AddSubscriptionAsync(userChannel, connection, async (channelName, subscriptions) =>
        {
            RedisLog.Subscribing(_logger, channelName);
            var channel = await _bus.SubscribeAsync(channelName).ConfigureAwait(false);
            channel.OnMessage(async channelMessage =>
            {
                try
                {
                    var invocation = RedisProtocol.ReadInvocation(channelMessage.Message);

                    var tasks = new List<Task>(subscriptions.Count);
                    foreach (var userConnection in subscriptions)
                    {
                        tasks.Add(userConnection.WriteAsync(invocation.Message, channelMessage.Stopping).AsTask());
                    }

                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RedisLog.FailedWritingMessage(_logger, ex);
                }
            });
        });
    }

    private async Task SubscribeToGroupAsync(string groupChannel, HubConnectionStore groupConnections)
    {
        RedisLog.Subscribing(_logger, groupChannel);
        var channel = await _bus.SubscribeAsync(groupChannel).ConfigureAwait(false);
        channel.OnMessage(async (channelMessage) =>
        {
            try
            {
                var invocation = RedisProtocol.ReadInvocation(channelMessage.Message);

                var tasks = new List<Task>(groupConnections.Count);
                foreach (var groupConnection in groupConnections)
                {
                    if (invocation.ExcludedConnectionIds?.Contains(groupConnection.ConnectionId) == true)
                    {
                        continue;
                    }

                    tasks.Add(groupConnection.WriteAsync(invocation.Message, channelMessage.Stopping).AsTask());
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RedisLog.FailedWritingMessage(_logger, ex);
            }
        });
    }

    private async Task SubscribeToReturnResultsAsync()
    {
        var channel = await _bus.SubscribeAsync(_channels.ReturnResults).ConfigureAwait(false);
        channel.OnMessage(async (channelMessage) =>
        {
            var completion = RedisProtocol.ReadCompletion(channelMessage.Message);
            IHubProtocol? protocol = null;
            foreach (var hubProtocol in _hubProtocolResolver.AllProtocols)
            {
                if (hubProtocol.Name.Equals(completion.ProtocolName))
                {
                    protocol = hubProtocol;
                    break;
                }
            }

            // Should only happen if you have different versions of servers and don't have the same protocols registered on both
            if (protocol is null)
            {
                RedisLog.MismatchedServers(_logger, completion.ProtocolName);
                return;
            }

            var ros = completion.CompletionMessage;
            HubMessage? hubMessage = null;
            bool retryForError = false;
            try
            {
                var parseSuccess = protocol.TryParseMessage(ref ros, _clientResultsManager, out hubMessage);
                retryForError = !parseSuccess;
            }
            catch
            {
                // Client returned wrong type? Or just an error from the HubProtocol, let's try with RawResult as the type and see if that works
                retryForError = true;
            }

            if (retryForError)
            {
                try
                {
                    ros = completion.CompletionMessage;
                    // if this works then we know there was an error with the type the client returned, we'll replace the CompletionMessage below and provide an error to the application code
                    if (!protocol.TryParseMessage(ref ros, FakeInvocationBinder.Instance, out hubMessage))
                    {
                        RedisLog.ErrorParsingResult(_logger, completion.ProtocolName, null);
                        return;
                    }
                }
                // Exceptions here would mean the HubProtocol implementation very likely has a bug, the other server has already deserialized the message (with RawResult) so it should be deserializable
                // We don't know the InvocationId, we should let the application developer know and potentially surface the issue to the HubProtocol implementor
                catch (Exception ex)
                {
                    RedisLog.ErrorParsingResult(_logger, completion.ProtocolName, ex);
                    return;
                }
            }

            var invocationInfo = _clientResultsManager.RemoveInvocation(((CompletionMessage)hubMessage!).InvocationId!);

            if (retryForError && invocationInfo is not null)
            {
                hubMessage = CompletionMessage.WithError(((CompletionMessage)hubMessage!).InvocationId!, $"Client result wasn't deserializable to {invocationInfo?.Type.Name}.");
            }

            if (invocationInfo is { } pending)
                await pending.Completion(pending.Tcs, (CompletionMessage)hubMessage!).ConfigureAwait(false);
        });
    }

    private class FakeInvocationBinder : IInvocationBinder
    {
        public static readonly FakeInvocationBinder Instance = new FakeInvocationBinder();

        private FakeInvocationBinder() { }

        public IReadOnlyList<Type> GetParameterTypes(string methodName)
        {
            throw new NotImplementedException();
        }

        public Type GetReturnType(string invocationId)
        {
            return typeof(RawResult);
        }

        public Type GetStreamItemType(string streamId)
        {
            throw new NotImplementedException();
        }
    }

    private async Task EnsureRedisServerConnection()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!Volatile.Read(ref _initialized))
        {
            await _connectionLock.WaitAsync(_bus.Stopping).ConfigureAwait(false);
            try
            {
                if (!_initialized)
                {
                    try
                    {
                        await SubscribeToAll().ConfigureAwait(false);
                        await SubscribeToGroupManagementChannel().ConfigureAwait(false);
                        await SubscribeToAckChannel().ConfigureAwait(false);
                        await SubscribeToReturnResultsAsync().ConfigureAwait(false);
                        Volatile.Write(ref _initialized, true);
                    }
                    catch
                    {
                        // A partial initialization must not leave duplicate consumers on retry.
                        if (!_bus.Stopping.IsCancellationRequested) await _bus.ResetAsync().ConfigureAwait(false);
                        throw;
                    }
                }
            }
            finally
            {
                _connectionLock.Release();
            }
        }
    }

    private static string GenerateServerName()
    {
        // Use the machine name for convenient diagnostics, but add a guid to make it unique.
        // Example: MyServerName_02db60e5fab243b890a847fa5c4dcb29
        return $"{Environment.MachineName}_{Guid.NewGuid():N}";
    }

    private static string GenerateInvocationId()
    {
        Span<byte> buffer = stackalloc byte[16];
        var success = Guid.NewGuid().TryWriteBytes(buffer);
        Debug.Assert(success);
        // 16 * 4/3 = 21.333 which means base64 encoding will use 22 characters of actual data and 2 characters of padding ('=')
        Span<char> base64 = stackalloc char[24];
        success = Convert.TryToBase64Chars(buffer, base64, out var written);
        Debug.Assert(success);
        Debug.Assert(written == 24);
        // Trim the two '=='
        Debug.Assert(base64.EndsWith("=="));
        return new string(base64[..^2]);
    }

    private interface IRedisFeature
    {
        HashSet<string> Groups { get; }
        SemaphoreSlim GroupLock { get; }
    }

    private sealed class RedisFeature : IRedisFeature
    {
        public HashSet<string> Groups { get; } = new HashSet<string>(StringComparer.Ordinal);
        public SemaphoreSlim GroupLock { get; } = new(1, 1);
    }
}
