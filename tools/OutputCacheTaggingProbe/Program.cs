using System.Diagnostics;
using System.Globalization;
using Respire;
using Respire.OutputCaching;

if (args.Length != 1)
    throw new ArgumentException("Usage: OutputCacheTaggingProbe <connection-string>. Use a disposable Redis instance.");

const int count = 500;
await using var client = await RespireClient.ConnectAsync(args[0]);
Console.WriteLine("round,mode,tags,values,payload_bytes,set_us_per_value,get_us_per_value,evict_ms,cleanup_ms,value_bytes,tag_index_bytes");
for (var round = 0; round < 3; round++)
{
    foreach (var tagCount in new[] { 0, 1, 5, 25 })
    {
        // Alternate order to reduce systematic warmup/load bias.
        var modes = round % 2 == 0
            ? new[] { RespireOutputCacheTaggingMode.MicrosoftCompatible, RespireOutputCacheTaggingMode.GenerationAware }
            : new[] { RespireOutputCacheTaggingMode.GenerationAware, RespireOutputCacheTaggingMode.MicrosoftCompatible };
        foreach (var mode in modes)
        {
            var clock = new ProbeClock();
            var prefix = $"{{output-probe:{Guid.NewGuid():N}}}:";
            var store = new RespireOutputCacheStore(client, new()
            { InstanceName = prefix, TaggingMode = mode, TimeProvider = clock });
            var tags = Enumerable.Range(0, tagCount).Select(index => "tag-" + index).ToArray();
            var payload = new byte[1024];
            var lifetime = TimeSpan.FromMinutes(10);
            var valuePrefix = prefix + (mode == RespireOutputCacheTaggingMode.GenerationAware ? "__RPOCV2_" : "__MSOCV_");
            var tagPrefix = prefix + (mode == RespireOutputCacheTaggingMode.GenerationAware ? "__RPOCT2_" : "__MSOCT_");
            await store.SetAsync("warmup", payload, tags, lifetime);
            _ = await store.GetAsync("warmup");
            if (tags.Length != 0) await store.EvictByTagAsync(tags[0]);
            else await client.DeleteAsync([valuePrefix + "warmup"]);

            var timer = Stopwatch.StartNew();
            for (var index = 0; index < count; index++)
                await store.SetAsync(index.ToString(CultureInfo.InvariantCulture), payload, tags, lifetime);
            var setMicroseconds = timer.Elapsed.TotalMicroseconds / count;
            timer.Restart();
            for (var index = 0; index < count; index++)
            {
                var bytes = await store.GetAsync(index.ToString(CultureInfo.InvariantCulture));
                if (bytes is null || bytes.Length != payload.Length) throw new InvalidOperationException("Probe read failed.");
            }
            var getMicroseconds = timer.Elapsed.TotalMicroseconds / count;
            using var valueMemory = await client.ExecuteAsync("MEMORY", ["USAGE", valuePrefix + "0"]);
            var tagBytes = 0L;
            if (tags.Length != 0)
            {
                using var tagMemory = await client.ExecuteAsync("MEMORY", ["USAGE", tagPrefix + tags[0]]);
                tagBytes = tagMemory.AsInteger();
            }

            timer.Restart();
            if (tags.Length != 0) await store.EvictByTagAsync(tags[0]);
            var evictMilliseconds = tags.Length == 0 ? 0 : timer.Elapsed.TotalMilliseconds;
            if (tags.Length == 0)
            {
                // Delete only keys this probe created. Avoid a database-wide scan or flush.
                for (var index = 0; index < count; index++)
                    await client.DeleteAsync([valuePrefix + index.ToString(CultureInfo.InvariantCulture)]);
            }
            // Values are gone. Advance only the cleanup clock to measure pruning
            // the remaining references without waiting ten minutes per dataset.
            clock.Offset = TimeSpan.FromMinutes(11);
            timer.Restart();
            await store.CollectExpiredTagsAsync();
            var cleanupMilliseconds = timer.Elapsed.TotalMilliseconds;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{round},{mode},{tagCount},{count},{payload.Length},{setMicroseconds:F2},{getMicroseconds:F2},{evictMilliseconds:F2},{cleanupMilliseconds:F2},{valueMemory.AsInteger()},{tagBytes}"));
        }
    }
}

sealed class ProbeClock : TimeProvider
{
    public TimeSpan Offset { get; set; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
}
