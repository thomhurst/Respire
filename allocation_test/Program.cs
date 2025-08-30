using System;
using System.Threading.Tasks;
using Respire.FastClient;
using Testcontainers.Redis;

class Program 
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Starting Redis container...");
        var redisContainer = new RedisBuilder().Build();
        await redisContainer.StartAsync();
        
        try
        {
            var connectionString = redisContainer.GetConnectionString();
            var parts = connectionString.Split(',')[0].Split(':');
            var host = parts[0];
            var port = int.Parse(parts[1]);
            
            Console.WriteLine($"Redis container started at {host}:{port}");
            Console.WriteLine($"Process ID: {Environment.ProcessId}");
            Console.WriteLine("Waiting 10 seconds for trace attachment...");
            await Task.Delay(10000);
            
            Console.WriteLine("=== Running Allocation Test ===");
            await RunDetailedAllocationTest(host, port);
            
            Console.WriteLine("\nAllocation test completed.");
        }
        finally
        {
            Console.WriteLine("Stopping Redis container...");
            await redisContainer.DisposeAsync();
        }
    }
    
    static async Task RunDetailedAllocationTest(string host, int port)
    {
        Console.WriteLine("Warming up GC and clearing memory...");
        
        // Warm up and clear
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);
        
        var startTotal = GC.GetTotalAllocatedBytes(true);
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        
        Console.WriteLine("\n--- CLIENT CREATION ---");
        var beforeCreate = GC.GetTotalAllocatedBytes(false);
        var client = await RespireClient.CreateAsync(host, port);
        var afterCreate = GC.GetTotalAllocatedBytes(false);
        Console.WriteLine($"✓ Client created - Allocated: {afterCreate - beforeCreate:N0} bytes");
        
        // Warm up the client with a few operations
        Console.WriteLine("\n--- WARMUP PHASE ---");
        for (int i = 0; i < 10; i++)
        {
            await client.SetAsync($"warmup{i}", $"value{i}");
            await client.GetAsync($"warmup{i}");
        }
        Console.WriteLine("✓ Warmup completed");
        
        // Clear GC again before measuring operations
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);
        
        Console.WriteLine("\n--- OPERATION ALLOCATIONS ---");
        
        // Measure SET operations
        var beforeSets = GC.GetTotalAllocatedBytes(false);
        for (int i = 0; i < 100; i++)
        {
            await client.SetAsync($"key{i}", $"value{i}");
        }
        var afterSets = GC.GetTotalAllocatedBytes(false);
        var setAllocations = afterSets - beforeSets;
        Console.WriteLine($"✓ 100 SET operations - Total: {setAllocations:N0} bytes");
        Console.WriteLine($"  Average per SET: {setAllocations / 100.0:F1} bytes");
        
        // Measure GET operations
        var beforeGets = GC.GetTotalAllocatedBytes(false);
        for (int i = 0; i < 100; i++)
        {
            var value = await client.GetAsync($"key{i}");
        }
        var afterGets = GC.GetTotalAllocatedBytes(false);
        var getAllocations = afterGets - beforeGets;
        Console.WriteLine($"✓ 100 GET operations - Total: {getAllocations:N0} bytes");
        Console.WriteLine($"  Average per GET: {getAllocations / 100.0:F1} bytes");
        
        // Measure mixed operations
        var beforeMixed = GC.GetTotalAllocatedBytes(false);
        for (int i = 0; i < 50; i++)
        {
            await client.SetAsync($"mixed{i}", $"mixedvalue{i}");
            var value = await client.GetAsync($"mixed{i}");
        }
        var afterMixed = GC.GetTotalAllocatedBytes(false);
        var mixedAllocations = afterMixed - beforeMixed;
        Console.WriteLine($"✓ 50 SET+GET pairs - Total: {mixedAllocations:N0} bytes");
        Console.WriteLine($"  Average per pair: {mixedAllocations / 50.0:F1} bytes");
        
        // Measure DEL operations
        var beforeDels = GC.GetTotalAllocatedBytes(false);
        for (int i = 0; i < 100; i++)
        {
            await client.DelAsync($"key{i}");
        }
        var afterDels = GC.GetTotalAllocatedBytes(false);
        var delAllocations = afterDels - beforeDels;
        Console.WriteLine($"✓ 100 DEL operations - Total: {delAllocations:N0} bytes");
        Console.WriteLine($"  Average per DEL: {delAllocations / 100.0:F1} bytes");
        
        // Cleanup
        var beforeDispose = GC.GetTotalAllocatedBytes(false);
        await client.DisposeAsync();
        var afterDispose = GC.GetTotalAllocatedBytes(false);
        Console.WriteLine($"\n✓ Client disposed - Allocated during dispose: {afterDispose - beforeDispose:N0} bytes");
        
        var endTotal = GC.GetTotalAllocatedBytes(true);
        var gen0After = GC.CollectionCount(0);
        var gen1After = GC.CollectionCount(1);
        var gen2After = GC.CollectionCount(2);
        
        Console.WriteLine($"\n--- SUMMARY ---");
        Console.WriteLine($"Total allocated: {endTotal - startTotal:N0} bytes");
        Console.WriteLine($"GC Collections - Gen0: {gen0After - gen0Before}, Gen1: {gen1After - gen1Before}, Gen2: {gen2After - gen2Before}");
        
        // Calculate overall average
        var totalOps = 100 + 100 + 100 + 100; // SET + GET + SET/GET pairs + DEL
        var totalOpAllocations = setAllocations + getAllocations + mixedAllocations + delAllocations;
        Console.WriteLine($"\nAverage allocation per operation: {totalOpAllocations / (double)totalOps:F1} bytes");
    }
}