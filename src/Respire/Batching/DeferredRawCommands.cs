using Respire.Commands;

namespace Respire;

/// <summary>Conservative, explicit key layouts for the public deferred escape hatch.</summary>
internal static class DeferredRawCommands
{
    internal static RespirePending<RespireResult> Enqueue(
        IPendingSink sink, RespireCommand descriptor, RespireValue[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var operation = Normalize(descriptor.Name);
        var words = operation.Split(' ');
        var root = words[0];
        var behavior = RespireCommand.Classify(root);
        if (behavior is RespireCommandBehavior.ConnectionScoped or RespireCommandBehavior.Blocking
            || RespireCommand.IsBlocking(root, behavior, args))
            throw new NotSupportedException($"{operation} cannot run in a deferred command queue.");

        // Validate nulls while snapshotting, before counted layouts read their arguments.
        var tokens = new RespireValue[checked(words.Length + args.Length)];
        for (var index = 0; index < words.Length; index++) tokens[index] = words[index];
        for (var index = 0; index < args.Length; index++)
        {
            RespireValue.ThrowIfNull(args[index], nameof(args));
            tokens[words.Length + index] = args[index].Snapshot();
        }

        // Determine every key, never just guess that the first argument is the routing key.
        var layout = RawCommandKeyLayouts.GetDeferredLayout(operation, tokens.AsSpan(words.Length));

        // Prefixing only mutates this private snapshot. A failed slot check leaves the sink untouched.
        int? slot = null;
        for (var index = 0; index < layout.Count; index++)
            PrefixKey(layout.Start + index * layout.Stride);
        if (layout.Extra >= 0) PrefixKey(layout.Extra);

        var firstKey = layout.Extra >= 0 ? layout.Extra : layout.Count > 0 ? layout.Start : -1;
        var command = CreateCommand(descriptor, tokens,
            firstKey < 0 ? -1 : words.Length + firstKey, words.Length);
        return sink.Add<DynamicCommand, RespireResult>(operation, command, static (client, value) =>
        {
            // Unread pendings must not retain pooled response storage.
            var owned = value.ToOwned();
            return client.CreateResult(in owned);
        });

        void PrefixKey(int index)
        {
            var key = tokens[words.Length + index].AsKey();
            var resolved = sink.Client.Key(in key);
            tokens[words.Length + index] = resolved;
            if (sink.Client.Core.Cluster is null) return;
            // AsKey produces text/bytes, and prefixing preserves that representation. Every key hashes.
            if (!resolved.TryGetClusterSlot(out var current))
                throw new InvalidOperationException("The deferred command key has no Cluster slot.");
            if (slot.HasValue && slot.Value != current)
                // Match typed multi-key commands, including their locally detected CROSSSLOT contract.
                throw new RespireServerException("CROSSSLOT Keys in request don't hash to the same slot", operation);
            slot = current;
        }
    }

    internal static DynamicCommand CreateCommand(
        RespireCommand descriptor, RespireValue[] tokens, int routingKeyIndex, int argumentOffset)
    {
        var cursorArgumentIndex = descriptor.CursorArgumentIndex < 0
            ? -1
            : argumentOffset + descriptor.CursorArgumentIndex;
        return new DynamicCommand(tokens, routingKeyIndex, argumentOffset,
            cacheMutation: descriptor.CacheMutation,
            readKind: descriptor.ReadKind, cursorArgumentIndex: cursorArgumentIndex,
            hasExplicitCacheMutation: descriptor.HasExplicitCacheMutation);
    }

    private static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = name.ToUpperInvariant();
        var previousSpace = true;
        foreach (var character in name)
        {
            if (character == ' ')
            {
                if (previousSpace) throw new ArgumentException("Use a verb or known subcommand without inline arguments.", nameof(name));
                previousSpace = true;
                continue;
            }
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-'))
                throw new ArgumentException("Command names must contain ASCII command tokens separated by single spaces.", nameof(name));
            previousSpace = false;
        }
        if (previousSpace) throw new ArgumentException("Command names must not end with a space.", nameof(name));
        return operation;
    }
}
