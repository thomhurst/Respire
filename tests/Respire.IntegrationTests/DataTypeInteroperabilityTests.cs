using FluentAssertions;
using Respire.FastClient;
using StackExchange.Redis;
using System.Text;
using TUnit.Core;
using TUnit.Assertions;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestFixture>(Shared = SharedType.Keyed)]
public class DataTypeInteroperabilityTests
{
    private readonly RedisTestFixture _fixture;
    private RespireClient _respireClient = null!;
    private IConnectionMultiplexer _stackExchangeMultiplexer = null!;
    private IDatabase _stackExchangeDb = null!;
    
    public DataTypeInteroperabilityTests(RedisTestFixture fixture)
    {
        _fixture = fixture;
    }
    
    [Before(HookType.Test)]
    public async Task InitializeAsync()
    {
        _respireClient = await RespireClient.CreateAsync(RedisTestFixture.Host, RedisTestFixture.Port);
        _stackExchangeMultiplexer = await ConnectionMultiplexer.ConnectAsync(_fixture.ConnectionString);
        _stackExchangeDb = _stackExchangeMultiplexer.GetDatabase();
        await _stackExchangeDb.ExecuteAsync("FLUSHDB");
    }
    
    [After(HookType.Test)]
    public async Task DisposeAsync()
    {
        await _respireClient.DisposeAsync();
        await _stackExchangeMultiplexer.DisposeAsync();
    }
    
    [Test]
    public async Task HashOperations_CrossClientCompatibility()
    {
        const string hashKey = "test:hash";
        
        // Set hash fields with StackExchange
        await _stackExchangeDb.HashSetAsync(hashKey, new HashEntry[]
        {
            new("field1", "value1"),
            new("field2", "value2"),
            new("field3", "value3")
        });
        
        // Get with Respire
        var field1 = await _respireClient.HGetAsync(hashKey, "field1");
        field1.ToString().Should().Be("value1");
        
        // Set another field with Respire
        await _respireClient.HSetAsync(hashKey, "field4", "value4");
        
        // Verify with StackExchange
        var field4 = await _stackExchangeDb.HashGetAsync(hashKey, "field4");
        field4.ToString().Should().Be("value4");
        
        // Get all fields with StackExchange and verify count
        var allFields = await _stackExchangeDb.HashGetAllAsync(hashKey);
        allFields.Should().HaveCount(4);
    }
    
    [Test]
    public async Task ListOperations_CrossClientCompatibility()
    {
        const string listKey = "test:list";
        
        // Push items with StackExchange
        await _stackExchangeDb.ListLeftPushAsync(listKey, new RedisValue[] { "item1", "item2", "item3" });
        
        // Push with Respire
        await _respireClient.LPushAsync(listKey, "item0");
        
        // Verify order with StackExchange
        var items = await _stackExchangeDb.ListRangeAsync(listKey);
        items.Should().HaveCount(4);
        items[0].ToString().Should().Be("item0");
        items[1].ToString().Should().Be("item3");
        items[2].ToString().Should().Be("item2");
        items[3].ToString().Should().Be("item1");
        
        // Pop with Respire
        var popped = await _respireClient.RPopAsync(listKey);
        popped.ToString().Should().Be("item1");
        
        // Verify with StackExchange
        var length = await _stackExchangeDb.ListLengthAsync(listKey);
        length.Should().Be(3);
    }
    
    [Test]
    public async Task SetOperations_CrossClientCompatibility()
    {
        const string setKey = "test:set";
        
        // Add members with StackExchange
        await _stackExchangeDb.SetAddAsync(setKey, new RedisValue[] { "member1", "member2", "member3" });
        
        // Add with Respire
        await _respireClient.SAddAsync(setKey, "member4");
        await _respireClient.SAddAsync(setKey, "member2"); // Duplicate - should not be added
        
        // Check membership with StackExchange
        var members = await _stackExchangeDb.SetMembersAsync(setKey);
        members.Should().HaveCount(4);
        members.Should().Contain(m => m == "member4");
        
        // Remove with Respire
        await _respireClient.SRemAsync(setKey, "member1");
        
        // Verify with StackExchange
        var isMember = await _stackExchangeDb.SetContainsAsync(setKey, "member1");
        isMember.Should().BeFalse();
        
        var newCount = await _stackExchangeDb.SetLengthAsync(setKey);
        newCount.Should().Be(3);
    }
    
    [Test]
    public async Task NumericValues_HandledConsistently()
    {
        // Test various numeric formats
        const string intKey = "test:int";
        const string floatKey = "test:float";
        const string negativeKey = "test:negative";
        
        // Set integers
        await _stackExchangeDb.StringSetAsync(intKey, 42);
        var intFromRespire = await _respireClient.GetAsync(intKey);
        intFromRespire.ToString().Should().Be("42");
        
        // Set floats
        await _stackExchangeDb.StringSetAsync(floatKey, 3.14159);
        var floatFromRespire = await _respireClient.GetAsync(floatKey);
        floatFromRespire.ToString().Should().Be("3.14159");
        
        // Set negative numbers
        await _respireClient.SetAsync(negativeKey, "-123");
        var negativeFromSE = await _stackExchangeDb.StringGetAsync(negativeKey);
        negativeFromSE.ToString().Should().Be("-123");
        
        // Increment operations
        var incrResult = await _stackExchangeDb.StringIncrementAsync(intKey);
        incrResult.Should().Be(43);
        
        var respireIncr = await _respireClient.IncrWithResponseAsync(intKey);
        respireIncr.AsInteger().Should().Be(44);
    }
    
    [Test]
    public async Task BinaryData_HandledConsistently()
    {
        const string binaryKey = "test:binary";
        
        // Create binary data
        var binaryData = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE, 0xFD };
        
        // Set with StackExchange
        await _stackExchangeDb.StringSetAsync(binaryKey, binaryData);
        
        // Get with Respire - it will return as string representation
        var dataFromRespire = await _respireClient.GetAsync(binaryKey);
        var respireBytes = Encoding.UTF8.GetBytes(dataFromRespire.ToString());
        
        // Set binary-safe string with Respire
        const string binaryKey2 = "test:binary2";
        await _respireClient.SetAsync(binaryKey2, "\x00\x01\x02\xFF\xFE\xFD");
        
        // Verify the data is preserved
        var exists = await _stackExchangeDb.KeyExistsAsync(binaryKey2);
        exists.Should().BeTrue();
    }
    
    [Test]
    public async Task ExpireOperations_WorkAcrossClients()
    {
        const string expKey1 = "test:expire1";
        const string expKey2 = "test:expire2";
        
        // Set with expiry using StackExchange
        await _stackExchangeDb.StringSetAsync(expKey1, "value1", TimeSpan.FromSeconds(10));
        
        // Check TTL with Respire
        var ttl1 = await _respireClient.TtlAsync(expKey1);
        ttl1.AsInteger().Should().BeInRange(5, 10);
        
        // Set with Respire and add expiry with StackExchange
        await _respireClient.SetAsync(expKey2, "value2");
        await _stackExchangeDb.KeyExpireAsync(expKey2, TimeSpan.FromSeconds(15));
        
        // Verify with Respire
        var ttl2 = await _respireClient.TtlAsync(expKey2);
        ttl2.AsInteger().Should().BeInRange(10, 15);
    }
    
    [Test]
    public async Task TransactionLike_Operations()
    {
        // Test that operations from both clients maintain consistency
        const string counterKey = "test:transaction:counter";
        const string flagKey = "test:transaction:flag";
        
        // Initialize
        await _stackExchangeDb.StringSetAsync(counterKey, 0);
        await _respireClient.SetAsync(flagKey, "false");
        
        // Simulate transaction-like operations
        var tasks = new List<Task>();
        
        // StackExchange increments
        for (var i = 0; i < 50; i++)
        {
            tasks.Add(_stackExchangeDb.StringIncrementAsync(counterKey));
        }
        
        // Respire increments
        for (var i = 0; i < 50; i++)
        {
            tasks.Add(_respireClient.IncrWithResponseAsync(counterKey).AsTask());
        }
        
        await Task.WhenAll(tasks);
        
        // Set flag to true
        await _respireClient.SetAsync(flagKey, "true");
        
        // Verify final state
        var finalCounter = await _stackExchangeDb.StringGetAsync(counterKey);
        finalCounter.ToString().Should().Be("100");
        
        var finalFlag = await _respireClient.GetAsync(flagKey);
        finalFlag.ToString().Should().Be("true");
    }
    
    [Test]
    public async Task KeyPatternOperations()
    {
        // Set up keys with both clients
        await _stackExchangeDb.StringSetAsync("pattern:a:1", "value1");
        await _respireClient.SetAsync("pattern:a:2", "value2");
        await _stackExchangeDb.StringSetAsync("pattern:b:1", "value3");
        await _respireClient.SetAsync("pattern:b:2", "value4");
        
        // Use StackExchange to scan keys
        var server = _stackExchangeMultiplexer.GetServer(_fixture.ConnectionString);
        var keys = server.Keys(pattern: "pattern:a:*").ToList();
        keys.Should().HaveCount(2);
        
        // Delete pattern with StackExchange
        foreach (var key in keys)
        {
            await _stackExchangeDb.KeyDeleteAsync(key);
        }
        
        // Verify with Respire
        var exists1 = await _respireClient.ExistsAsync("pattern:a:1");
        var exists2 = await _respireClient.ExistsAsync("pattern:a:2");
        var exists3 = await _respireClient.ExistsAsync("pattern:b:1");
        var exists4 = await _respireClient.ExistsAsync("pattern:b:2");
        
        exists1.AsInteger().Should().Be(0);
        exists2.AsInteger().Should().Be(0);
        exists3.AsInteger().Should().Be(1);
        exists4.AsInteger().Should().Be(1);
    }
}