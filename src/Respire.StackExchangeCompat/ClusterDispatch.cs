using Respire.Commands;

namespace Respire.StackExchangeCompat;

internal sealed class ClusterDispatch
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Task> _slotTails = [];
    private Task _barrier = Task.CompletedTask;

    internal Reservation Enqueue(RespireClient client, RespireCommand command, RespireValue[] arguments)
    {
        int[]? slots = GetSlots(client, command, arguments);

        lock (_gate)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (slots is null)
            {
                // All slot tails already depend on the previous barrier. Future reservations depend on this one.
                Task predecessors = _slotTails.Count == 0 ? _barrier : Task.WhenAll(_slotTails.Values);
                _slotTails.Clear();
                _barrier = completion.Task;
                return new(slots, predecessors, completion);
            }

            Task slotPredecessors = slots.Length == 1
                ? _slotTails.GetValueOrDefault(slots[0], _barrier)
                : Task.WhenAll(slots.Select(slot => _slotTails.GetValueOrDefault(slot, _barrier)));

            foreach (int slot in slots)
            {
                _slotTails[slot] = completion.Task;
            }

            return new(slots, slotPredecessors, completion);
        }
    }

    private static int[]? GetSlots(RespireClient client, RespireCommand command, RespireValue[] arguments)
    {
        // A script can touch keys that are not in its argument list.
        if (command.Name is "EVAL" or "EVALSHA"
            || !RawCommandKeyLayouts.TryGetLayout(command.Name, arguments, out var layout)
            || (layout.Count == 0 && layout.Extra < 0))
        {
            return null;
        }

        if (layout.Count == 1 && layout.Extra < 0)
        {
            return [client.ResolveKey(arguments[layout.Start].AsKey()).ClusterSlot];
        }

        var slots = new HashSet<int>();

        for (int index = 0; index < layout.Count; index++)
        {
            int argumentIndex = layout.Start + index * layout.Stride;
            slots.Add(client.ResolveKey(arguments[argumentIndex].AsKey()).ClusterSlot);
        }

        if (layout.Extra >= 0)
        {
            slots.Add(client.ResolveKey(arguments[layout.Extra].AsKey()).ClusterSlot);
        }

        return [.. slots];
    }

    internal void Release(Reservation reservation)
    {
        if (reservation.Predecessors.IsCompleted)
        {
            Complete(reservation);
        }
        else
        {
            // Let a cancelled caller return without allowing its successors to overtake older commands.
            _ = CompleteAfterPredecessorsAsync(reservation);
        }
    }

    private async Task CompleteAfterPredecessorsAsync(Reservation reservation)
    {
        await reservation.Predecessors.ConfigureAwait(false);
        Complete(reservation);
    }

    private void Complete(Reservation reservation)
    {
        lock (_gate)
        {
            Task completed = reservation.Completion.Task;

            if (reservation.Slots is { } slots)
            {
                foreach (int slot in slots)
                {
                    if (_slotTails.GetValueOrDefault(slot) == completed)
                    {
                        _slotTails.Remove(slot);
                    }
                }
            }
            else if (_barrier == completed)
            {
                _barrier = Task.CompletedTask;
            }

            reservation.Completion.SetResult();
        }
    }

    internal readonly record struct Reservation(int[]? Slots, Task Predecessors, TaskCompletionSource Completion);
}
