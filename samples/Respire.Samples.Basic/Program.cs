using Respire;

// A tour of the Respire API. Needs a Redis on localhost:6379 — e.g.:
//   docker run --rm -p 6379:6379 redis:7

await using var redis = await RespireClient.ConnectAsync("redis://localhost");

// ── Strings ──────────────────────────────────────────────────────────────────
await redis.SetAsync("greeting", "hello world", expiry: TimeSpan.FromMinutes(5));
Console.WriteLine($"greeting = {await redis.GetStringAsync("greeting")}");

var hits = await redis.IncrementAsync("stats:hits");
Console.WriteLine($"hits = {hits}");

// Typed values — serialized with System.Text.Json by default.
await redis.SetAsync("user:1", new User("Ada", 36));
var user = await redis.GetAsync<User>("user:1");
Console.WriteLine($"user:1 = {user}");

// Conditional set: only when the key does not exist.
var claimed = await redis.SetAsync("lock:job42", "worker-a", expiry: TimeSpan.FromSeconds(30), when: SetWhen.NotExists);
Console.WriteLine($"lock claimed: {claimed}");

// Expiry is one argument: a TimeSpan (PX), an instant (PXAT), or "keep what's there" (KEEPTTL).
await redis.SetAsync("greeting", "hello again", expiry: RespireTtl.Keep);
await redis.SetAsync("daily:report", "pending", expiry: RespireTtl.At(DateTimeOffset.UtcNow.AddHours(1)));
Console.WriteLine($"greeting ttl = {await redis.Keys.ExpiryAsync("greeting")}");

// ── Facets: one group per data type ──────────────────────────────────────────
await redis.Hashes.SetAsync("user:1:profile", ("name", "Ada"), ("lang", "en"));
Console.WriteLine($"profile.name = {await redis.Hashes.GetStringAsync("user:1:profile", "name")}");

await redis.Lists.RightPushAsync("queue", "job-1", "job-2");
Console.WriteLine($"popped = {await redis.Lists.LeftPopAsync("queue")}");

await redis.SortedSets.AddAsync("leaderboard", "ada", 42);
await redis.SortedSets.AddAsync("leaderboard", "grace", 58);
foreach (var entry in await redis.SortedSets.RangeWithScoresAsync("leaderboard", descending: true))
{
    Console.WriteLine($"  {entry.Member}: {entry.Score}");
}

// ── Batch: explicit pipeline, one flush ──────────────────────────────────────
var batch = redis.CreateBatch();
var a = batch.GetStringAsync("greeting");
var b = batch.IncrementAsync("stats:hits");
await batch.SendAsync();
Console.WriteLine($"batched: {a.Result} / hits={b.Result}");

// ── Transaction: MULTI/EXEC with typed results ───────────────────────────────
var tx = redis.CreateTransaction();
var newBalance = tx.IncrementAsync("balance", 100);
tx.ListRightPushAsync("audit", "deposit:100");
var committed = await tx.CommitAsync();
Console.WriteLine($"committed={committed}, balance={newBalance.Result}");

// ── Raw escape hatch — any command, interpolated safely ──────────────────────
using (var encoding = await redis.ExecuteAsync($"OBJECT ENCODING {"greeting"}"))
{
    Console.WriteLine($"encoding = {encoding.AsString()}");
}

// ── Pub/sub as an async stream ───────────────────────────────────────────────
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
var listener = Task.Run(async () =>
{
    await using var subscription = redis.Subscribe("news");
    try
    {
        await foreach (var message in subscription.WithCancellation(cts.Token))
        {
            Console.WriteLine($"received on {message.Channel}: {message.Text}");
            break;
        }
    }
    catch (OperationCanceledException)
    {
    }
});

// Retry until the subscriber is registered (PublishAsync returns the receiver count).
while (await redis.PublishAsync("news", "respire 1.0 shipped") == 0 && !cts.IsCancellationRequested)
{
    await Task.Delay(50);
}

await listener;

Console.WriteLine("done.");

public sealed record User(string Name, int Age);
