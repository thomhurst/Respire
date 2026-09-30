using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class DeferredRawKeyLayoutTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SupportedLayoutsMatchRedisKeyDiscovery(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}");
        using var catalog = await client.ExecuteAsync("COMMAND");
        var verified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metadata in Commands(catalog))
        {
            var operation = metadata[0].AsString()!.Replace('|', ' ').ToUpperInvariant();
            var words = operation.Split(' ');
            var args = BuildArguments(operation, checked((int)Math.Abs(metadata[1].AsInteger())) - words.Length);
            DeferredRawCommands.KeyLayout layout;
            try
            {
                layout = DeferredRawCommands.GetLayout(operation, args.Select(x => (RespireValue)x).ToArray());
            }
            catch (NotSupportedException)
            {
                continue;
            }

            verified.Add(operation).Should().BeTrue();
            if (layout.Count == 0 && layout.Extra < 0)
            {
                // COMMAND GETKEYS reports an error for commands with no key arguments.
                metadata[8].Count.Should().Be(0, $"{operation} must have no server key specifications");
                continue;
            }

            var selected = Enumerable.Range(0, layout.Count)
                .Select(index => args[layout.Start + index * layout.Stride]).ToList();
            if (layout.Extra >= 0) selected.Add(args[layout.Extra]);
            RespireValue[] query = ["GETKEYS", .. words.Select(x => (RespireValue)x), .. args.Select(x => (RespireValue)x)];
            using var keys = await client.ExecuteAsync("COMMAND", query);
            var serverKeys = Enumerable.Range(0, keys.Count).Select(index => keys[index].AsString()).ToArray();
            selected.Should().BeEquivalentTo(serverKeys, $"{operation} must select exactly the keys Redis discovers");
        }

        // All current allowlisted operations exist in Redis 7. Changes to the allowlist must
        // update this count and supply a key-discovery fixture for any new argument grammar.
        verified.Should().HaveCount(167);
    }

    private static IEnumerable<RespireResult> Commands(RespireResult commands)
    {
        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index];
            yield return command;
            foreach (var subcommand in Commands(command[9])) yield return subcommand;
        }
    }

    private static string[] BuildArguments(string operation, int count) => operation switch
    {
        "EVAL" or "EVAL_RO" => ["return ARGV[1]", "2", "key-a", "key-b", "argument"],
        "EVALSHA" or "EVALSHA_RO" => [new string('0', 40), "2", "key-a", "key-b", "argument"],
        "FCALL" or "FCALL_RO" => ["function-name", "2", "key-a", "key-b", "argument"],
        "LMPOP" => ["2", "key-a", "key-b", "LEFT", "COUNT", "3"],
        "ZMPOP" => ["2", "key-a", "key-b", "MIN", "COUNT", "3"],
        "SINTERCARD" or "ZINTERCARD" => ["2", "key-a", "key-b", "LIMIT", "3"],
        "ZDIFF" or "ZINTER" or "ZUNION" => ["2", "key-a", "key-b", "WITHSCORES"],
        "ZDIFFSTORE" => ["destination", "2", "key-a", "key-b"],
        "ZINTERSTORE" or "ZUNIONSTORE" => ["destination", "2", "key-a", "key-b", "WEIGHTS", "2", "3"],
        "BITOP" => ["AND", "destination", "key-a", "key-b"],
        "MSET" or "MSETNX" => ["key-a", "value-a", "key-b", "value-b"],
        "COPY" => ["source", "destination", "DB", "1", "REPLACE"],
        // Redis's fixed-position key specifications need arity, not executable values.
        // COMMAND GETKEYS does not execute these commands or require their keys to exist.
        _ => Enumerable.Range(0, Math.Max(0, count)).Select(index => $"argument-{index}").ToArray(),
    };
}
