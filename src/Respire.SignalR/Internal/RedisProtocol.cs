// Adapted from dotnet/aspnetcore v8.0.31 under the MIT license; see LICENSE-Microsoft.txt.
using System.Buffers;
using MessagePack;

namespace Respire.SignalR.Internal;

// Microsoft's channel-specific backplane envelopes; hub payloads retain their own protocols.
internal sealed class RedisProtocol(DefaultHubMessageSerializer serializer)
{
    internal byte[] WriteInvocation(string methodName, object?[] args, string? invocationId = null,
        IReadOnlyList<string>? excludedConnectionIds = null, string? returnChannel = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(returnChannel is null ? 2 : 4);
        writer.WriteArrayHeader(excludedConnectionIds?.Count ?? 0);
        if (excludedConnectionIds is not null)
            foreach (var id in excludedConnectionIds) writer.Write(id);
        var messages = serializer.SerializeMessage(new InvocationMessage(invocationId, methodName, args));
        writer.WriteMapHeader(messages.Count);
        foreach (var message in messages)
        {
            writer.Write(message.ProtocolName);
            writer.Write(message.Serialized.Span);
        }
        if (returnChannel is not null) { writer.Write(invocationId); writer.Write(returnChannel); }
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    internal static byte[] WriteGroupCommand(RedisGroupCommand command)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(5);
        writer.Write(command.Id); writer.Write(command.ServerName); writer.Write((byte)command.Action);
        writer.Write(command.GroupName); writer.Write(command.ConnectionId);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    internal static byte[] WriteAck(int id)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(1); writer.Write(id); writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    internal static byte[] WriteCompletionMessage(ReadOnlyMemory<byte> message, string protocol)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(2); writer.Write(protocol); writer.Write(message.Span); writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    internal static RedisInvocation ReadInvocation(ReadOnlyMemory<byte> data)
    {
        var reader = new MessagePackReader(data);
        var count = ReadEnvelope(ref reader, 2);
        var idsCount = reader.ReadArrayHeader();
        if (idsCount > reader.Sequence.Length - reader.Consumed) throw new InvalidDataException("Invalid excluded connection count.");
        string[]? ids = idsCount == 0 ? null : new string[idsCount];
        for (var i = 0; i < idsCount; i++) ids![i] = ReadString(ref reader);
        var protocolsCount = reader.ReadMapHeader();
        if (protocolsCount > (reader.Sequence.Length - reader.Consumed) / 2) throw new InvalidDataException("Invalid hub protocol count.");
        var messages = new SerializedMessage[protocolsCount];
        for (var i = 0; i < messages.Length; i++)
        {
            var protocol = ReadString(ref reader);
            var bytes = reader.ReadBytes() ?? throw new InvalidDataException("Missing hub message.");
            messages[i] = new(protocol, bytes.ToArray());
        }
        var invocationId = count > 3 ? ReadString(ref reader) : null;
        var returnChannel = count > 3 ? ReadString(ref reader) : null;
        return new(new SerializedHubMessage(messages), ids, invocationId, returnChannel);
    }

    internal static RedisGroupCommand ReadGroupCommand(ReadOnlyMemory<byte> data)
    {
        var reader = new MessagePackReader(data);
        ReadEnvelope(ref reader, 5);
        var id = reader.ReadInt32();
        var server = ReadString(ref reader);
        var action = (GroupAction)reader.ReadByte();
        if (action is not (GroupAction.Add or GroupAction.Remove)) throw new InvalidDataException("Invalid group action.");
        return new(id, server, action, ReadString(ref reader), ReadString(ref reader));
    }

    internal static int ReadAck(ReadOnlyMemory<byte> data)
    {
        var reader = new MessagePackReader(data);
        ReadEnvelope(ref reader, 1);
        return reader.ReadInt32();
    }

    internal static RedisCompletion ReadCompletion(ReadOnlyMemory<byte> data)
    {
        var reader = new MessagePackReader(data);
        ReadEnvelope(ref reader, 2);
        var protocol = ReadString(ref reader);
        return new(protocol, reader.ReadBytes() ?? throw new InvalidDataException("Missing client result."));
    }

    private static int ReadEnvelope(ref MessagePackReader reader, int minimum)
    {
        var count = reader.ReadArrayHeader();
        if (count < minimum || count > reader.Sequence.Length - reader.Consumed) throw new InvalidDataException("Invalid backplane envelope.");
        return count;
    }

    private static string ReadString(ref MessagePackReader reader)
        => reader.ReadString() ?? throw new InvalidDataException("Missing backplane string.");
}
