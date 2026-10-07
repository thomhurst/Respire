using System.Runtime.InteropServices;
using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Protocol;

public class AggregatePayloadTests
{
    [Test]
    [Arguments(4095, 6, false, true)]
    [Arguments(4095, 6, true, true)]
    [Arguments(4096, 511, false, false)]
    [Arguments(4096, 511, true, false)]
    [Arguments(4096, 512, false, true)]
    [Arguments(4096, 512, true, true)]
    [Arguments(4096, 513, false, true)]
    [Arguments(4096, 513, true, true)]
    [Arguments(4097, 512, false, false)]
    [Arguments(4097, 512, true, false)]
    [Arguments(4103, 512, false, false)]
    [Arguments(4103, 512, true, false)]
    [Arguments(4103, 513, false, true)]
    [Arguments(4103, 513, true, true)]
    [Arguments(8192, 1023, false, false)]
    [Arguments(8192, 1023, true, false)]
    [Arguments(8192, 1024, false, true)]
    [Arguments(8192, 1024, true, true)]
    public async Task SparseFramesCopyOnlyRetainedPayloads(int frameLength, int payloadBytes, bool nested, bool sharesFrame)
    {
        var firstLength = payloadBytes - 3;
        var suffix = "$" + firstLength + "\r\n" + new string('x', firstLength)
            + "\r\n" + (nested ? "*1\r\n" : "") + "$3\r\nend\r\n";
        var prefix = "*2\r\n|1\r\n+k\r\n$";
        var paddingLength = frameLength - prefix.Length - suffix.Length - 4;
        while (prefix.Length + paddingLength.ToString().Length + 4 + paddingLength + suffix.Length != frameLength)
            paddingLength = frameLength - prefix.Length - paddingLength.ToString().Length - 4 - suffix.Length;
        var input = Encoding.ASCII.GetBytes(prefix + paddingLength + "\r\n"
            + new string('a', paddingLength) + "\r\n" + suffix);
        var position = 0;
        await Assert.That(input.Length).IsEqualTo(frameLength);
        await Assert.That(RespParser.TryParseValue(input, ref position, out var value)).IsEqualTo(RespParseStatus.Done);
        using (value)
        using (var owned = value.ToOwned())
        {
            var first = value.AsArray()[0];
            var second = nested ? value.AsArray()[1].AsArray()[0] : value.AsArray()[1];
            MemoryMarshal.TryGetArray(first.AsMemory(), out var one);
            MemoryMarshal.TryGetArray(second.AsMemory(), out var two);
            await Assert.That(ReferenceEquals(one.Array, two.Array)).IsEqualTo(sharesFrame);
            if (!sharesFrame) await Assert.That(one.Array!.Length).IsLessThan(frameLength);
            input.AsSpan().Fill(0);
            await Assert.That(position).IsEqualTo(frameLength);
            await Assert.That(first.AsString()).IsEqualTo(new string('x', firstLength));
            await Assert.That(second.AsString()).IsEqualTo("end");
            await Assert.That(value.Equals(owned)).IsTrue();
            await Assert.That(value.GetHashCode()).IsEqualTo(owned.GetHashCode());
            await Assert.That(value.GetOwnedSize()).IsEqualTo(owned.GetOwnedSize());
        }
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FramesBeyondThePoolLimitCopyOnlyRetainedChildPayloads(bool oversized)
    {
        // Keep enough retained payload for shared storage at the exact pool limit;
        // one extra wire byte must still select the unpooled-frame fallback.
        var frameLength = 64 * 1024 * 1024 + (oversized ? 1 : 0);
        var oneText = new string('o', 8 * 1024 * 1024);
        var suffix = Encoding.ASCII.GetBytes("\r\n$" + oneText.Length + "\r\n" + oneText + "\r\n*1\r\n$3\r\ntwo\r\n");
        var prefix = Encoding.ASCII.GetBytes($"*2\r\n|1\r\n+k\r\n${frameLength}\r\n");
        var paddingLength = frameLength - prefix.Length - suffix.Length;
        prefix = Encoding.ASCII.GetBytes($"*2\r\n|1\r\n+k\r\n${paddingLength}\r\n");
        var input = new byte[3 + frameLength + 4];
        prefix.CopyTo(input, 3);
        input.AsSpan(3 + prefix.Length, paddingLength).Fill((byte)'x');
        suffix.CopyTo(input, 3 + prefix.Length + paddingLength);
        ":9\r\n"u8.CopyTo(input.AsSpan(3 + frameLength));
        var position = 3;
        await Assert.That(RespParser.TryParseValue(input, ref position, out var value)).IsEqualTo(RespParseStatus.Done);
        using (value)
        using (var owned = value.ToOwned())
        {
            MemoryMarshal.TryGetArray(value.AsArray()[0].AsMemory(), out var one);
            MemoryMarshal.TryGetArray(value.AsArray()[1].AsArray()[0].AsMemory(), out var two);
            if (oversized)
            {
                await Assert.That(ReferenceEquals(one.Array, two.Array)).IsFalse();
                await Assert.That(one.Array!.Length).IsLessThan(frameLength);
                await Assert.That(two.Array!.Length).IsLessThan(1024);
            }
            else await Assert.That(one.Array).IsSameReferenceAs(two.Array);
            await Assert.That(position).IsEqualTo(3 + frameLength);
            input.AsSpan().Fill(0);
            await Assert.That(value.AsArray()[0].AsString()).IsEqualTo(oneText);
            await Assert.That(value.AsArray()[1].AsArray()[0].AsString()).IsEqualTo("two");
            await Assert.That(value.Equals(owned)).IsTrue();
            await Assert.That(value.GetHashCode()).IsEqualTo(owned.GetHashCode());
            await Assert.That(value.GetOwnedSize()).IsEqualTo(owned.GetOwnedSize());
        }
    }

#if DEBUG
    [Test]
    [NotInParallel]
    public async Task RootDisposalPoisonsSharedPayloadForInvalidChildReads()
    {
        var pos = 0;
        await Assert.That(RespParser.TryParseValue("*1\r\n$3\r\none\r\n"u8, ref pos, out var value)).IsEqualTo(RespParseStatus.Done);
        var child = value.AsArray()[0];
        value.Dispose();
        // This is an intentionally invalid internal read, checked only in Debug builds.
        await Assert.That(child.AsSpan().SequenceEqual(new byte[] { 0xDD, 0xDD, 0xDD })).IsTrue();
    }
#endif

    [Test]
    [Arguments('*')]
    [Arguments('~')]
    [Arguments('>')]
    [Arguments('%')]
    public async Task OwnedSizeMatchesCompleteFragmentedAndOwnedReplies(char marker)
    {
        var frame = Encoding.ASCII.GetBytes($"{marker}{(marker == '%' ? 1 : 2)}\r\n$3\r\none\r\n*2\r\n:7\r\n$3\r\ntwo\r\n");
        var pos = 0;
        await Assert.That(RespParser.TryParseValue(frame, ref pos, out var complete)).IsEqualTo(RespParseStatus.Done);
        using (complete)
        using (var owned = complete.ToOwned())
        using (var parser = new RespParseState(int.MaxValue))
        {
            pos = 0;
            await Assert.That(parser.TryParse(frame.AsSpan(0, 4), ref pos, out _, out _)).IsEqualTo(RespParseStatus.NeedMoreData);
            await Assert.That(parser.TryParse(frame, ref pos, out var fragmented, out _)).IsEqualTo(RespParseStatus.Done);
            using (fragmented)
            {
                await Assert.That(complete.GetOwnedSize()).IsEqualTo(owned.GetOwnedSize());
                await Assert.That(fragmented.GetOwnedSize()).IsEqualTo(owned.GetOwnedSize());
                await Assert.That(owned.GetOwnedSize()).IsEqualTo(5 * 32L + 6);
            }
        }
    }

    [Test]
    public async Task LeadingAttributesAreExcludedFromSharedFrame()
    {
        var attributes = "|1\r\n+meta\r\n+ignored\r\n|1\r\n+more\r\n+ignored\r\n"u8.ToArray();
        var root = "*1\r\n$3\r\none\r\n"u8.ToArray();
        var input = new byte[3 + attributes.Length + root.Length];
        attributes.CopyTo(input, 3);
        root.CopyTo(input, 3 + attributes.Length);
        var pos = 3;
        await Assert.That(RespParser.TryParseValue(input, ref pos, out var value)).IsEqualTo(RespParseStatus.Done);
        using (value)
        {
            MemoryMarshal.TryGetArray(value.AsArray()[0].AsMemory(), out var payload);
            await Assert.That(payload.Offset).IsEqualTo(8);
            await Assert.That(payload.Array!.AsSpan(0, root.Length).SequenceEqual(root)).IsTrue();
            await Assert.That(pos).IsEqualTo(input.Length);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ChildPublicViewRejectsAccessAfterRootDisposal(bool fragmented)
    {
        var value = await ParseAggregateAsync("*1\r\n$3\r\none\r\n"u8.ToArray(), fragmented);
        using var result = new RespireResult(in value);
        var child = result[0];
        var bytes = child.AsBytes();
        result.Dispose();
        await Assert.That(child.IsDisposed).IsTrue();
        await Assert.That(() => child.AsString()).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(() => child.AsBytes()).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(bytes).IsEquivalentTo("one"u8.ToArray());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BorrowedDeserializationMaterializesChildren(bool fragmented)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)],
        });
        var value = await ParseAggregateAsync("*3\r\n$3\r\none\r\n$2\r\n42\r\n$7\r\n[1,2,3]\r\n"u8.ToArray(), fragmented);
        string? text;
        byte[]? bytes;
        int number;
        int[]? numbers;
        try
        {
            text = client.DeserializeBorrowed<string>(in value.AsArray()[0]);
            bytes = client.DeserializeBorrowed<byte[]>(in value.AsArray()[0]);
            number = client.DeserializeBorrowed<int>(in value.AsArray()[1]);
            numbers = client.DeserializeBorrowed<int[]>(in value.AsArray()[2]);
            MemoryMarshal.TryGetArray(value.AsArray()[0].AsMemory(), out var payload);
            await Assert.That(bytes).IsNotSameReferenceAs(payload.Array);
            // Deterministically model receive-pool reuse after materialization.
            foreach (ref readonly var child in value.AsArray())
            {
                MemoryMarshal.TryGetArray(child.AsMemory(), out var storage);
                storage.Array!.AsSpan().Fill(0);
            }
        }
        finally { value.Dispose(); }
        await Assert.That(text).IsEqualTo("one");
        await Assert.That(bytes).IsEquivalentTo("one"u8.ToArray());
        await Assert.That(number).IsEqualTo(42);
        await Assert.That(numbers).IsEquivalentTo(new[] { 1, 2, 3 });
    }

    private static async Task<RespValue> ParseAggregateAsync(byte[] frame, bool fragmented)
    {
        var pos = 0;
        if (!fragmented)
        {
            await Assert.That(RespParser.TryParseValue(frame, ref pos, out var complete)).IsEqualTo(RespParseStatus.Done);
            return complete;
        }
        using var parser = new RespParseState(int.MaxValue);
        await Assert.That(parser.TryParse(frame.AsSpan(0, 4), ref pos, out _, out _)).IsEqualTo(RespParseStatus.NeedMoreData);
        await Assert.That(parser.TryParse(frame, ref pos, out var value, out _)).IsEqualTo(RespParseStatus.Done);
        return value;
    }

    [Test]
    public async Task SeededAggregateCorpusMatchesFragmentedParser()
    {
        var random = new Random(962);
        for (var iteration = 0; iteration < 128; iteration++)
        {
            using var wire = new MemoryStream();
            WriteAggregate(wire, random, depth: 0);
            var frame = wire.ToArray();
            var input = new byte[frame.Length + 8];
            frame.CopyTo(input, 3);
            "+OK\r\n"u8.CopyTo(input.AsSpan(3 + frame.Length));
            var pos = 3;
            await Assert.That(RespParser.TryParseValue(input, ref pos, out var complete)).IsEqualTo(RespParseStatus.Done);
            using (complete)
            using (var parser = new RespParseState(int.MaxValue))
            {
                await Assert.That(pos).IsEqualTo(3 + frame.Length);
                pos = 3;
                var end = 3;
                RespValue fragmented = default;
                while (end < 3 + frame.Length)
                {
                    end = Math.Min(3 + frame.Length, end + random.Next(1, 8));
                    var status = parser.TryParse(input.AsSpan(0, end), ref pos, out fragmented, out _);
                    await Assert.That(status).IsEqualTo(end == 3 + frame.Length
                        ? RespParseStatus.Done : RespParseStatus.NeedMoreData);
                }
                using (fragmented)
                {
                    await Assert.That(fragmented.Equals(complete)).IsTrue();
                    using var owned = complete.ToOwned();
                    await Assert.That(owned.Equals(fragmented)).IsTrue();
                    await Assert.That(owned.GetHashCode()).IsEqualTo(complete.GetHashCode());
                }
            }
        }
    }

    private static void WriteAggregate(Stream wire, Random random, int depth)
    {
        var marker = "*%~>"[random.Next(4)];
        var count = random.Next(1, 5);
        wire.Write(Encoding.ASCII.GetBytes($"{marker}{count}\r\n"));
        for (var index = 0; index < (marker == '%' ? count * 2 : count); index++)
        {
            if (random.Next(4) == 0) wire.Write("|1\r\n+meta\r\n+ignored\r\n"u8);
            if (depth < 4 && random.Next(4) == 0)
            {
                WriteAggregate(wire, random, depth + 1);
                continue;
            }
            switch (random.Next(6))
            {
                case 0: wire.Write("+OK\r\n"u8); break;
                case 1: wire.Write("+value\r\n"u8); break;
                case 2: wire.Write("$-1\r\n"u8); break;
                case 3: wire.Write(Encoding.ASCII.GetBytes($":{random.NextInt64()}\r\n")); break;
                default:
                    var payload = new byte[random.Next(0, 80)];
                    random.NextBytes(payload);
                    wire.Write(Encoding.ASCII.GetBytes($"${payload.Length}\r\n"));
                    wire.Write(payload);
                    wire.Write("\r\n"u8);
                    break;
            }
        }
    }

    [Test]
    [Arguments('*')]
    [Arguments('~')]
    [Arguments('>')]
    [Arguments('%')]
    public async Task CompleteAggregateChildrenShareOneOwnedFrame(char marker)
    {
        var count = marker == '%' ? 2 : 4;
        var frame = Encoding.ASCII.GetBytes($"{marker}{count}\r\n$3\r\none\r\n+two\r\n-ERR three\r\n(444444444444444444444444\r\n");
        var input = new byte[frame.Length + 9];
        frame.CopyTo(input, 4);
        ":9\r\n"u8.CopyTo(input.AsSpan(4 + frame.Length));
        var pos = 4;
        await Assert.That(RespParser.TryParseValue(input, ref pos, out var value)).IsEqualTo(RespParseStatus.Done);
        using (value)
        {
            await Assert.That(pos).IsEqualTo(4 + frame.Length);
            var payloads = Enumerable.Range(0, 4).Select(index => value.AsArray()[index].AsMemory()).ToArray();
            foreach (var payload in payloads)
                await Assert.That(MemoryMarshal.TryGetArray(payload, out _)).IsTrue();
            MemoryMarshal.TryGetArray(payloads[0], out var first);
            foreach (var payload in payloads.Skip(1))
            {
                MemoryMarshal.TryGetArray(payload, out var other);
                await Assert.That(other.Array).IsSameReferenceAs(first.Array);
            }
            await Assert.That(first.Array).IsNotSameReferenceAs(input);
            using var owned = value.ToOwned();
            input.AsSpan().Fill(0);
            await Assert.That(value.AsArray()[0].AsString()).IsEqualTo("one");
            await Assert.That(value.AsArray()[1].AsString()).IsEqualTo("two");
            await Assert.That(value.AsArray()[2].GetErrorMessage()).IsEqualTo("ERR three");
            await Assert.That(value.AsArray()[3].AsString()).IsEqualTo("444444444444444444444444");
            await Assert.That(owned.Equals(value)).IsTrue();
            await Assert.That(owned.GetHashCode()).IsEqualTo(value.GetHashCode());
        }
    }

    [Test]
    public async Task NestedAttributesAndStringFamiliesKeepSharedPayloadAndOwnedCopies()
    {
        var frame = "|1\r\n+meta\r\n$4\r\nskip\r\n*3\r\n*2\r\n|1\r\n+k\r\n+ignore\r\n$3\r\none\r\n!9\r\nERR three\r\n%1\r\n+two\r\n=9\r\ntxt:hello\r\n$0\r\n\r\n"u8.ToArray();
        var pos = 0;
        await Assert.That(RespParser.TryParseValue(frame, ref pos, out var value)).IsEqualTo(RespParseStatus.Done);
        using var owned = value.ToOwned();
        try
        {
            MemoryMarshal.TryGetArray(value.AsArray()[0].AsArray()[0].AsMemory(), out var one);
            MemoryMarshal.TryGetArray(value.AsArray()[0].AsArray()[1].AsMemory(), out var error);
            MemoryMarshal.TryGetArray(value.AsArray()[1].AsArray()[0].AsMemory(), out var two);
            await Assert.That(error.Array).IsSameReferenceAs(one.Array);
            await Assert.That(two.Array).IsSameReferenceAs(one.Array);
            await Assert.That(value.AsArray()[1].AsArray()[1].AsString()).IsEqualTo("hello");
            await Assert.That(value.AsArray()[2].AsString()).IsEqualTo("");
            await Assert.That(pos).IsEqualTo(frame.Length);
        }
        finally { value.Dispose(); }
        frame.AsSpan().Fill(0);
        await Assert.That(owned.AsArray()[0].AsArray()[0].AsString()).IsEqualTo("one");
        await Assert.That(owned.AsArray()[0].AsArray()[1].GetErrorMessage()).IsEqualTo("ERR three");
        await Assert.That(owned.AsArray()[1].AsArray()[1].AsString()).IsEqualTo("hello");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EveryFragmentBoundaryMatchesCompleteAggregate(bool resumable)
    {
        var frame = "*4\r\n$3\r\none\r\n%1\r\n$3\r\ntwo\r\n$5\r\nthree\r\n$-1\r\n!8\r\nERR four\r\n"u8.ToArray();
        for (var split = 0; split < frame.Length; split++)
        {
            using var parser = new RespParseState(int.MaxValue);
            var pos = 0;
            var status = resumable
                ? parser.TryParse(frame.AsSpan(0, split), ref pos, out var incomplete, out _)
                : RespParser.TryParseValue(frame.AsSpan(0, split), ref pos, out incomplete);
            incomplete.Dispose();
            await Assert.That(status).IsEqualTo(RespParseStatus.NeedMoreData);
            if (!resumable) await Assert.That(pos).IsEqualTo(0);
            status = resumable
                ? parser.TryParse(frame, ref pos, out var complete, out _)
                : RespParser.TryParseValue(frame, ref pos, out complete);
            using (complete)
            {
                await Assert.That(status).IsEqualTo(RespParseStatus.Done);
                await Assert.That(pos).IsEqualTo(frame.Length);
                await Assert.That(complete.AsArray()[0].AsString()).IsEqualTo("one");
                await Assert.That(complete.AsArray()[1].AsArray()[1].AsString()).IsEqualTo("three");
                await Assert.That(complete.AsArray()[2].IsNull).IsTrue();
                await Assert.That(complete.AsArray()[3].GetErrorMessage()).IsEqualTo("ERR four");
            }
        }
    }
}
