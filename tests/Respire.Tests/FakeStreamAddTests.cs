using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeStreamAddTests
{
    [Test]
    public async Task AutomaticIdCarriesSaturatedSequenceAsync()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using RespireClient client = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010

        await client.Streams.AddAsync(
            "events",
            new RespireStreamAddOptions { Id = "18446744073709551614-18446744073709551615" },
            ("field", "initial"));
        RespireStreamId carried = await client.Streams.AddAsync("events", ("field", "auto"));
        RespireStreamId next = await client.Streams.AddAsync("events", ("field", "next"));

        await Assert.That(carried).IsEqualTo((RespireStreamId)"18446744073709551615-0");
        await Assert.That(next).IsEqualTo((RespireStreamId)"18446744073709551615-1");
        await Assert.That(await client.Streams.CountAsync("events")).IsEqualTo(3);
    }

    [Test]
    public async Task AutomaticIdRejectsExhaustedStreamAsync()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using RespireClient client = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010

        await client.Streams.AddAsync(
            "events",
            new RespireStreamAddOptions { Id = "18446744073709551615-18446744073709551615" },
            ("field", "initial"));

        RespireServerException? error = await Assert.That(async () => await client.Streams.AddAsync("events", ("field", "auto")))
            .Throws<RespireServerException>();

        await Assert.That(error!.Message).IsEqualTo("ERR The stream has exhausted the last possible ID, unable to add more items");
        await Assert.That(await client.Streams.CountAsync("events")).IsEqualTo(1);
    }
}
