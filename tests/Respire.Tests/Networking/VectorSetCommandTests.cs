using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Respire.Commands;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class VectorSetCommandTests
{
    [Test]
    [Arguments(0, 0)]
    [Arguments(0, 1)]
    [Arguments(1, 0)]
    [Arguments(1, 1)]
    [Arguments(2, 0)]
    [Arguments(2, 1)]
    public async Task CommandsPreserveBinaryInputsOptionsAndDeferredSnapshots(int surface, int format)
    {
        byte[] member = [0, 255, 128];
        byte[] key = "{vectors}:index"u8.ToArray();
        byte[] json = "{\"Name\":\"first\"}"u8.ToArray();
        float[] vector = [1, -2.5f, 0];
        var expectedMember = member.ToArray();
        var expectedJson = json.ToArray();
        var info = "*12\r\n+quant-type\r\n+q8\r\n+vector-dim\r\n:3\r\n+size\r\n:2\r\n+hnsw-m\r\n:16\r\n+max-level\r\n:1\r\n+attributes-count\r\n:1\r\n"u8.ToArray();
        byte[][] replies = [":1\r\n"u8.ToArray(), Join("*3\r\n", Bulk(member), Bulk("0.75"u8.ToArray()), Bulk(json)),
            Join("*1\r\n", Bulk(member)), ":1\r\n"u8.ToArray(), ":2\r\n"u8.ToArray(), ":3\r\n"u8.ToArray(),
            "*3\r\n,1\r\n,-0.5\r\n,0\r\n"u8.ToArray(), "#t\r\n"u8.ToArray(), Bulk(json), ":1\r\n"u8.ToArray(),
            Bulk(json), ":1\r\n"u8.ToArray(), info, Join("*1\r\n%1\r\n", Bulk(member), ",0.5\r\n"u8.ToArray()),
            Bulk(member), Join("*1\r\n", Bulk(member)), Join("*1\r\n", Bulk(member))];
        if (surface == 2)
        {
            var executed = Join($"*{replies.Length}\r\n", replies);
            replies = [FakeRespServer.OkReply, .. Enumerable.Repeat("+QUEUED\r\n"u8.ToArray(), replies.Length), executed];
        }
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        using var batch = surface == 1 ? view.CreateBatch() : null;
        await using var transaction = surface == 2 ? view.CreateTransaction() : null;
        IRespireCommandQueue? queue = transaction ?? (IRespireCommandQueue?)batch;
        var pending = new List<Func<object?>>();
        var results = new List<object?>();
        async Task Capture<T>(Func<ValueTask<T>> immediate, Func<RespirePending<T>> deferred)
        {
            if (queue is null) results.Add(await immediate());
            else { var item = deferred(); pending.Add(() => item.Result); }
        }
        var add = new RespireVectorAddOptions { ReduceDimensions = 2, CheckAndSet = true,
            Quantization = RespireVectorQuantization.None, ExplorationFactor = 50, AttributesJson = json, Links = 4 };
        var search = new RespireVectorSearchOptions { IncludeScores = true, IncludeAttributes = true, Count = 5,
            Epsilon = 0.25, ExplorationFactor = 60, Filter = ".Name == \"first\"", FilterExplorationFactor = 200, Exact = true, NoThread = true };
        var encoding = (RespireVectorEncoding)format;
        var attributes = new Attributes { Name = "first" };
        await Capture(() => view.VectorSets.AddAsync(key, vector, member, add, encoding), () => queue!.VectorSets.Add(key, vector, member, add, encoding));
        await Capture(() => view.VectorSets.SearchAsync(key, vector, search, encoding), () => queue!.VectorSets.Search(key, vector, search, encoding));
        await Capture(() => view.VectorSets.SearchByMemberAsync(key, member), () => queue!.VectorSets.SearchByMember(key, member));
        await Capture(() => view.VectorSets.RemoveAsync(key, member), () => queue!.VectorSets.Remove(key, member));
        await Capture(() => view.VectorSets.CountAsync(key), () => queue!.VectorSets.Count(key));
        await Capture(() => view.VectorSets.DimensionsAsync(key), () => queue!.VectorSets.Dimensions(key));
        await Capture(() => view.VectorSets.EmbeddingAsync(key, member), () => queue!.VectorSets.Embedding(key, member));
        await Capture(() => view.VectorSets.ContainsAsync(key, member), () => queue!.VectorSets.Contains(key, member));
        await Capture(() => view.VectorSets.GetAttributesJsonAsync(key, member), () => queue!.VectorSets.GetAttributesJson(key, member));
        await Capture(() => view.VectorSets.SetAttributesJsonAsync(key, member, json), () => queue!.VectorSets.SetAttributesJson(key, member, json));
        await Capture(() => view.VectorSets.GetAttributesAsync<Attributes>(key, member), () => queue!.VectorSets.GetAttributes<Attributes>(key, member));
        await Capture(() => view.VectorSets.SetAttributesAsync(key, member, attributes), () => queue!.VectorSets.SetAttributes(key, member, attributes));
        await Capture(() => view.VectorSets.InfoAsync(key), () => queue!.VectorSets.Info(key));
        await Capture(() => view.VectorSets.LinksAsync(key, member, true), () => queue!.VectorSets.Links(key, member, true));
        await Capture(() => view.VectorSets.RandomMemberAsync(key), () => queue!.VectorSets.RandomMember(key));
        await Capture(() => view.VectorSets.RandomMembersAsync(key, -2), () => queue!.VectorSets.RandomMembers(key, -2));
        await Capture(() => view.VectorSets.RangeAsync(key, "-", "+", 10), () => queue!.VectorSets.Range(key, "-", "+", 10));
        Array.Fill(member, (byte)42); Array.Fill(key, (byte)'x'); Array.Fill(json, (byte)'x'); Array.Fill(vector, 99);
        attributes.Name = "changed";
        if (batch is not null) await batch.ExecuteAsync();
        if (transaction is not null) await transaction.CommitAsync();
        results.AddRange(pending.Select(read => read()));
        await client.DisposeAsync();
        await Assert.That((bool)results[0]!).IsTrue();
        var match = ((RespireVectorMatch[])results[1]!).Single();
        await Assert.That(match.Member).IsEquivalentTo(expectedMember);
        await Assert.That(match.Score).IsEqualTo((double?)0.75);
        await Assert.That(match.AttributesJson!).IsEquivalentTo(expectedJson);
        await Assert.That((float[])results[6]!).IsEquivalentTo((float[])[1, -0.5f, 0]);
        await Assert.That(((Attributes)results[10]!).Name).IsEqualTo("first");
        await Assert.That(((RespireVectorSetInfo)results[12]!).Dimensions).IsEqualTo(3);
        await Assert.That(((RespireVectorMatch[][])results[13]!)[0][0].Score).IsEqualTo((double?)0.5);
        await Assert.That((byte[])results[14]!).IsEquivalentTo(expectedMember);
        var commands = server.ReceivedArguments.Where(args => Encoding.ASCII.GetString(args[0]).StartsWith('V')).ToArray();
        await Assert.That(commands.Length).IsEqualTo(17);
        foreach (var command in commands) await Assert.That(Encoding.UTF8.GetString(command[1])).IsEqualTo("tenant:{vectors}:index");
        var written = commands[0];
        await Assert.That(Encoding.ASCII.GetString(written[2])).IsEqualTo("REDUCE");
        if (format == 0)
        {
            byte[] fp32 = new byte[12];
            BinaryPrimitives.WriteSingleLittleEndian(fp32, 1);
            BinaryPrimitives.WriteSingleLittleEndian(fp32.AsSpan(4), -2.5f);
            await Assert.That(Encoding.ASCII.GetString(written[4])).IsEqualTo("FP32");
            await Assert.That(written[5]).IsEquivalentTo(fp32);
            await Assert.That(written[6]).IsEquivalentTo(expectedMember);
        }
        else
        {
            await Assert.That(written.Skip(4).Take(5).Select(Encoding.ASCII.GetString).ToArray())
                .IsEquivalentTo(new[] { "VALUES", "3", "1", "-2.5", "0" });
            await Assert.That(written[9]).IsEquivalentTo(expectedMember);
        }
        await Assert.That(written[^3]).IsEquivalentTo(expectedJson);
        await Assert.That(commands[9][3]).IsEquivalentTo(expectedJson);
        await Assert.That(commands[11][3]).IsEquivalentTo(expectedJson);
        await Assert.That(Encoding.ASCII.GetString(commands[16][4])).IsEqualTo("10");
    }

    [Test]
    public async Task ValuesEncodingIsInvariantAndRoundTripsSinglePrecision()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            float[] vector = [1.25f, 1e-5f, 1e20f, float.Epsilon, float.MaxValue, -float.MaxValue, 1.2345678f];
            await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
            await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
            await client.VectorSets.AddAsync("k", vector, "m", encoding: RespireVectorEncoding.Values);
            var values = server.ReceivedArguments.Single().Skip(4).Take(vector.Length)
                .Select(Encoding.ASCII.GetString).ToArray();
            await Assert.That(values[0]).IsEqualTo("1.25");
            await Assert.That(values[1]).IsEqualTo("1E-05");
            await Assert.That(values[2]).IsEqualTo("1E+20");
            for (var index = 0; index < vector.Length; index++)
            {
                var parsed = float.Parse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture);
                await Assert.That(BitConverter.SingleToInt32Bits(parsed)).IsEqualTo(BitConverter.SingleToInt32Bits(vector[index]));
            }
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    [Test]
    public async Task InvalidExplorationFactorNamesOptionsAndExplainsTheMember()
    {
        await using var client = RespireClient.Create("redis://localhost:1");
        var addError = await Assert.That(async () => await client.VectorSets.AddAsync("k", new[] { 1f }, "m",
            new() { ExplorationFactor = 0 })).ThrowsExactly<ArgumentOutOfRangeException>();
        var searchError = await Assert.That(async () => await client.VectorSets.SearchAsync("k", new[] { 1f },
            new() { ExplorationFactor = 1_000_001 })).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(addError!.ParamName).IsEqualTo("options");
        await Assert.That(searchError!.ParamName).IsEqualTo("options");
        await Assert.That(addError.Message).Contains(nameof(RespireVectorAddOptions.ExplorationFactor));
        await Assert.That(searchError.Message).Contains(nameof(RespireVectorSearchOptions.ExplorationFactor));
    }

    [Test]
    public async Task InvalidAndCancelledCallsSendNothingAndDoNotPoisonQueues()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        await Assert.That(async () => await client.VectorSets.AddAsync("k", Array.Empty<float>(), "m")).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.VectorSets.AddAsync("k", new[] { float.NaN }, "m")).Throws<ArgumentException>();
        await Assert.That(async () => await client.VectorSets.SearchAsync("k", new[] { 1f }, new() { Epsilon = double.NaN })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.VectorSets.SearchAsync("k", new[] { 1f }, new() { Count = 0 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => batch.VectorSets.Add("k", new[] { 1f }, "m", new() { Links = 3 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => transaction.VectorSets.Add("k", new[] { 1f }, "m", encoding: (RespireVectorEncoding)99)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.VectorSets.RandomMembersAsync("k", long.MinValue)).Throws<ArgumentOutOfRangeException>();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.That(async () => await client.VectorSets.AddAsync("k", new[] { 1f }, "m", cancellationToken: cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await client.VectorSets.SearchAsync("k", new[] { 1f }, cancellationToken: cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await client.VectorSets.SearchByMemberAsync("k", "m", cancellationToken: cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await client.VectorSets.LinksAsync("k", "m", cancellationToken: cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
        var valid = batch.VectorSets.Count("k");
        await batch.ExecuteAsync();
        await Assert.That(valid.Result).IsEqualTo(1);
    }

    [Test]
    public async Task VectorCommandRoutesByPrefixedKeyAndSnapshotRetainsSlot()
    {
        await using var client = RespireClient.Create("redis://localhost:1");
        var view = (RespireClient)client.WithKeyPrefix("{tenant}:");
        var command = VectorSetCommands.BuildAdd(view, "key", new[] { 1f, 2f }, "{other}:member", default, default);
        await Assert.That(command.TryGetClusterSlot(out var slot)).IsTrue();
        await Assert.That(slot).IsEqualTo(((RespireKey)"{tenant}:key").ClusterSlot);
        var snapshot = SnapshotCommand.Create(in command);
        await Assert.That(snapshot.TryGetClusterSlot(out var copiedSlot)).IsTrue();
        await Assert.That(copiedSlot).IsEqualTo(slot);
    }

    public sealed class Attributes { public string Name { get; set; } = ""; }
    private static byte[] Bulk(byte[] value) => Join($"${value.Length}\r\n", value, "\r\n"u8.ToArray());
    private static byte[] Join(string prefix, params byte[][] parts) => Encoding.ASCII.GetBytes(prefix).Concat(parts.SelectMany(x => x)).ToArray();
}
