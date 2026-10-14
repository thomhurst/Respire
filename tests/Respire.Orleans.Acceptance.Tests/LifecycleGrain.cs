using System.Threading.Channels;
using Orleans;
using Orleans.Runtime;

namespace Respire.Orleans.Acceptance.Tests;

public interface ILifecycleGrain : IGrainWithStringKey
{
    Task SetValue(int value);
    Task<int> GetValue();
    Task<Guid> GetActivation();
    Task RegisterReminder();
    Task RemoveReminder();
}

public sealed class LifecycleGrain(
    [PersistentState("value")] IPersistentState<int> state,
    ReminderDeliveries deliveries) : Grain, ILifecycleGrain, IRemindable
{
    private readonly Guid _activation = Guid.NewGuid();

    public async Task SetValue(int value)
    {
        state.State = value;
        await state.WriteStateAsync();
    }

    public Task<int> GetValue() => Task.FromResult(state.State);
    public Task<Guid> GetActivation() => Task.FromResult(_activation);

    public async Task RegisterReminder() =>
        _ = await this.RegisterOrUpdateReminder("delivery", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

    public async Task RemoveReminder()
    {
        var reminder = await this.GetReminder("delivery");
        if (reminder is not null) await this.UnregisterReminder(reminder);
    }

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        deliveries.Received.Writer.TryWrite((_activation, state.State));
        return Task.CompletedTask;
    }
}

// One observer per silo: a delivery after restart cannot be satisfied by the old activation.
public sealed class ReminderDeliveries
{
    public Channel<(Guid Activation, int Value)> Received { get; } =
        Channel.CreateUnbounded<(Guid, int)>();
}
