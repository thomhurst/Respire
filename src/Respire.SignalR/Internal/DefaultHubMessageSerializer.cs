// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace Respire.SignalR.Internal;

internal sealed class DefaultHubMessageSerializer
{
    private readonly List<IHubProtocol> _hubProtocols;

    public DefaultHubMessageSerializer(IHubProtocolResolver hubProtocolResolver, IList<string>? globalSupportedProtocols, IList<string>? hubSupportedProtocols)
    {
        var supportedProtocols = hubSupportedProtocols ?? globalSupportedProtocols
            ?? hubProtocolResolver.AllProtocols.Select(static protocol => protocol.Name).ToArray();
        _hubProtocols = new List<IHubProtocol>(supportedProtocols.Count);
        var protocolNames = supportedProtocols as IReadOnlyList<string> ?? supportedProtocols.ToArray();
        foreach (var protocolName in supportedProtocols)
        {
            var protocol = hubProtocolResolver.GetProtocol(protocolName, protocolNames);
            if (protocol != null)
            {
                _hubProtocols.Add(protocol);
            }
        }
    }

    public IReadOnlyList<SerializedMessage> SerializeMessage(HubMessage message)
    {
        var list = new List<SerializedMessage>(_hubProtocols.Count);
        foreach (var protocol in _hubProtocols)
        {
            list.Add(new SerializedMessage(protocol.Name, protocol.GetMessageBytes(message)));
        }

        return list;
    }
}
