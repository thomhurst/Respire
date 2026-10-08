using TUnit.Assertions;

namespace Respire;

[RespireHash("model:{{{Id}}}")]
public partial record StoredHashModel(string Id, string Name, int Count, string? Note, int? Age);

[RespireHash("nullable-model")]
public partial record NullableHashModel(string? Text, int? Number);

internal static class GeneratedHashModelScenarios
{
    public static async Task RoundTripAsync(IRespireClient root)
    {
        var client = root.WithKeyPrefix("generated:" + Guid.NewGuid().ToString("N") + ":");
        var model = new StoredHashModel("é🙂", "", 0, "old", 42);
        var templateKey = StoredHashModelHashMapper.GetKey(model);
        await StoredHashModelHashMapper.SetAsync(client, model);
        await Assert.That(await StoredHashModelHashMapper.GetAsync(client, templateKey)).IsEqualTo(model);
        await client.Hashes.SetAsync(templateKey, "Unknown", (RespireValue)"preserve");
        var updated = model with { Note = null, Age = null, Count = 7 };
        await StoredHashModelHashMapper.SetAsync(client, updated);
        await Assert.That(await StoredHashModelHashMapper.GetAsync(client, templateKey)).IsEqualTo(updated);
        await Assert.That(await client.Hashes.GetStringAsync(templateKey, "Unknown")).IsEqualTo("preserve");
        await Assert.That(await client.Hashes.ExistsAsync(templateKey, "Note")).IsFalse();
        await Assert.That(await client.Hashes.ExistsAsync(templateKey, "Age")).IsFalse();

        var partial = await StoredHashModelHashMapper.GetPartialAsync(client, templateKey, ["Age", "Name", "Count"]);
        await Assert.That(partial.Age.Selected && !partial.Age.Found).IsTrue();
        await Assert.That(partial.Name.Selected && partial.Name.Found).IsTrue();
        await Assert.That(partial.Name.Value).IsEqualTo("");
        await Assert.That(partial.Count.Value).IsEqualTo(7);
        await Assert.That(partial.Id.Selected).IsFalse();

        RespireKey binaryKey = new byte[] { 0xff, 0, 0x80, 0xc3, 0xa9 };
        var binaryClient = client.WithKeyPrefix(new byte[] { 0xfe, 0 });
        await StoredHashModelHashMapper.SetAsync(binaryClient, binaryKey, model);
        await Assert.That(await StoredHashModelHashMapper.GetAsync(binaryClient, binaryKey)).IsEqualTo(model);
        var zero = await StoredHashModelHashMapper.GetPartialAsync(binaryClient, binaryKey, ["Count"]);
        await Assert.That(zero.Count.Selected && zero.Count.Found).IsTrue();
        await Assert.That(zero.Count.Value).IsEqualTo(0);
        await Assert.That(await StoredHashModelHashMapper.GetAsync(client, "missing")).IsNull();
        var absent = await StoredHashModelHashMapper.GetPartialAsync(client, "missing", ["Id", "Count"]);
        await Assert.That(absent.Id.Selected && !absent.Id.Found).IsTrue();
        await Assert.That(absent.Count.Selected && !absent.Count.Found).IsTrue();

        await client.Hashes.RemoveAsync(templateKey, "Id");
        await Assert.That(async () => await StoredHashModelHashMapper.GetAsync(client, templateKey)).Throws<FormatException>();
        // Partial reads do not require, decode, or construct the remaining model properties.
        await Assert.That((await StoredHashModelHashMapper.GetPartialAsync(client, templateKey, ["Count"])).Count.Value).IsEqualTo(7);
        await client.Hashes.SetAsync(templateKey, "Count", (RespireValue)"invalid");
        await Assert.That(async () => await StoredHashModelHashMapper.GetPartialAsync(client, templateKey, ["Count"])).Throws<FormatException>();

        await NullableHashModelHashMapper.SetAsync(client, new NullableHashModel("", 0));
        await Assert.That(await NullableHashModelHashMapper.GetAsync(client, "nullable-model")).IsEqualTo(new NullableHashModel("", 0));
        await client.Hashes.SetAsync("nullable-model", "Unknown", (RespireValue)"keep");
        await NullableHashModelHashMapper.SetAsync(client, new NullableHashModel(null, null));
        await Assert.That(await NullableHashModelHashMapper.GetAsync(client, "nullable-model")).IsEqualTo(new NullableHashModel(null, null));
        await Assert.That(await client.Hashes.CountAsync("nullable-model")).IsEqualTo(1);
        await client.Hashes.RemoveAsync("nullable-model", "Unknown");
        await NullableHashModelHashMapper.SetAsync(client, new NullableHashModel(null, null));
        await Assert.That(await NullableHashModelHashMapper.GetAsync(client, "nullable-model")).IsNull();
        await client.SetAsync("wrong-type", (RespireValue)"string");
        await Assert.That(async () => await StoredHashModelHashMapper.GetAsync(client, "wrong-type")).Throws<RespireServerException>();
        await Assert.That(async () => await StoredHashModelHashMapper.SetAsync(client, "wrong-type", model)).Throws<RespireServerException>();
    }
}
