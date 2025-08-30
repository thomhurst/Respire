using System;
using System.Threading.Tasks;
using Respire.FastClient;

class Program 
{
    static async Task Main(string[] args)
    {
        // Create connection to local Redis (if available)
        Console.WriteLine("Testing Respire allocation patterns...");
        
        Console.WriteLine($"Process ID: {Environment.ProcessId}");
        Console.WriteLine("Monitor with:");
        Console.WriteLine($"dotnet-counters monitor --process-id {Environment.ProcessId} --counters System.Runtime");
        Console.WriteLine();
        
        // Test with connection failure (no Redis server)
        await TestAllocationsWithMockConnections();
        
        static async Task TestAllocationsWithMockConnections()
        {
            Console.WriteLine("Testing allocation patterns with mock connections (no Redis required)...");
            
            // Test client creation allocations
            Console.WriteLine("\n=== Client Creation Test ===");
            var start = GC.GetTotalAllocatedBytes(true);
            
            RespireClient? client = null;
            try 
            {
                client = await RespireClient.CreateAsync("127.0.0.1", 6379);
                Console.WriteLine("✓ Client created successfully");
            }
            catch
            {
                Console.WriteLine("✗ Client creation failed (expected - no Redis)");
            }
            
            var end = GC.GetTotalAllocatedBytes(true);
            Console.WriteLine($"Client creation allocated: {end - start:N0} bytes");
            
            if (client != null)
            {
                // Test SET operations allocations
                Console.WriteLine("\n=== SET Operations Test ===");
                start = GC.GetTotalAllocatedBytes(true);
                
                for (int i = 0; i < 100; i++)
                {
                    try 
                    {
                        await client.SetAsync($"test:key:{i}", $"value{i}");
                    }
                    catch 
                    {
                        // Expected to fail, but we're measuring allocations
                    }
                }
                
                end = GC.GetTotalAllocatedBytes(true);
                var setAllocated = end - start;
                Console.WriteLine($"100 SET operations allocated: {setAllocated:N0} bytes");
                Console.WriteLine($"Per SET operation: {setAllocated / 100.0:F1} bytes");
                
                // Test GET operations allocations  
                Console.WriteLine("\n=== GET Operations Test ===");
                start = GC.GetTotalAllocatedBytes(true);
                
                for (int i = 0; i < 100; i++)
                {
                    try 
                    {
                        var result = await client.GetAsync($"test:key:{i}");
                    }
                    catch 
                    {
                        // Expected to fail, but we're measuring allocations
                    }
                }
                
                end = GC.GetTotalAllocatedBytes(true);
                var getAllocated = end - start;
                Console.WriteLine($"100 GET operations allocated: {getAllocated:N0} bytes");
                Console.WriteLine($"Per GET operation: {getAllocated / 100.0:F1} bytes");
                
                // Test batch operations
                Console.WriteLine("\n=== Batch Operations Test ===");
                start = GC.GetTotalAllocatedBytes(true);
                
                var keyValues = new List<(string Key, string Value)>();
                for (int i = 0; i < 50; i++)
                {
                    keyValues.Add(($"batch:key:{i}", $"batch_value_{i}"));
                }
                
                try 
                {
                    await client.SetBatchAsync(keyValues);
                }
                catch 
                {
                    // Expected to fail, but we're measuring allocations
                }
                
                end = GC.GetTotalAllocatedBytes(true);
                var batchAllocated = end - start;
                Console.WriteLine($"50-item batch SET allocated: {batchAllocated:N0} bytes");
                Console.WriteLine($"Per batch item: {batchAllocated / 50.0:F1} bytes");
                
                await client.DisposeAsync();
            }
            
            // Force garbage collection and show final memory state
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            
            var finalMemory = GC.GetTotalAllocatedBytes(false);
            Console.WriteLine($"\nFinal allocated memory: {finalMemory:N0} bytes");
        }
        
        Console.WriteLine("\nAllocation test completed.");
    }
}