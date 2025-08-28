using FluentAssertions;
using Respire.FastClient;
using StackExchange.Redis;
using TUnit.Core;
using TUnit.Assertions;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestFixture>(Shared = SharedType.Keyed)]
public class ClientComparisonTests
{
    private readonly RedisTestFixture _fixture;
    private RespireClient _respireClient = null!;
    private IConnectionMultiplexer _stackExchangeMultiplexer = null!;
    private IDatabase _stackExchangeDb = null!;
    
    public ClientComparisonTests(RedisTestFixture fixture)
    {
        _fixture = fixture;
    }
    
    [Before(HookType.Test)]
    public async Task InitializeAsync()
    {
        // Initialize Respire client
        _respireClient = await RespireClient.CreateAsync(RedisTestFixture.Host, RedisTestFixture.Port);
        
        // Initialize StackExchange.Redis client
        _stackExchangeMultiplexer = await ConnectionMultiplexer.ConnectAsync(_fixture.ConnectionString);
        _stackExchangeDb = _stackExchangeMultiplexer.GetDatabase();
        
        // Clear the database before each test
        await _stackExchangeDb.ExecuteAsync("FLUSHDB");
    }
    
    [After(HookType.Test)]
    public async Task DisposeAsync()
    {
        await _respireClient.DisposeAsync();
        await _stackExchangeMultiplexer.DisposeAsync();
    }
    
    [Test]
    public async Task StringOperations_ShouldProduceSameResults()
    {
        // Arrange
        const string key = "test:string";
        const string value = "Hello, Redis!";
        
        // Act - Set with StackExchange, Get with Respire
        await _stackExchangeDb.StringSetAsync(key, value);
        var respireGet = await _respireClient.GetAsync(key);
        
        // Assert
        respireGet.ToString().Should().Be(value);
        
        // Act - Set with Respire, Get with StackExchange  
        const string key2 = "test:string2";
        const string value2 = "Hello from Respire!";
        await _respireClient.SetAsync(key2, value2);
        var stackExchangeGet = await _stackExchangeDb.StringGetAsync(key2);
        
        // Assert
        stackExchangeGet.ToString().Should().Be(value2);
    }
    
    [Test]
    public async Task IncrementOperations_ShouldProduceSameResults()
    {
        // Arrange
        const string key = "test:counter";
        
        // Act - Increment with both clients alternately
        var se1 = await _stackExchangeDb.StringIncrementAsync(key); // 1
        var r1 = await _respireClient.IncrWithResponseAsync(key);   // 2
        var se2 = await _stackExchangeDb.StringIncrementAsync(key);  // 3
        var r2 = await _respireClient.IncrWithResponseAsync(key);    // 4
        
        // Assert
        se1.Should().Be(1);
        r1.AsInteger().Should().Be(2);
        se2.Should().Be(3);
        r2.AsInteger().Should().Be(4);
        
        // Verify final value with both clients
        var finalValueSE = await _stackExchangeDb.StringGetAsync(key);
        var finalValueR = await _respireClient.GetAsync(key);
        
        finalValueSE.ToString().Should().Be("4");
        finalValueR.ToString().Should().Be("4");
    }
    
    [Test]
    public async Task ExistsOperation_ShouldProduceSameResults()
    {
        // Arrange
        const string key1 = "test:exists1";
        const string key2 = "test:exists2";
        const string value = "exists";
        
        // Act - Set with Respire
        await _respireClient.SetAsync(key1, value);
        
        // Check exists with both clients
        var existsSE = await _stackExchangeDb.KeyExistsAsync(key1);
        var existsR = await _respireClient.ExistsAsync(key1);
        var notExistsSE = await _stackExchangeDb.KeyExistsAsync(key2);
        var notExistsR = await _respireClient.ExistsAsync(key2);
        
        // Assert
        existsSE.Should().BeTrue();
        existsR.AsInteger().Should().Be(1);
        notExistsSE.Should().BeFalse();
        notExistsR.AsInteger().Should().Be(0);
    }
    
    [Test]
    public async Task DeleteOperation_ShouldProduceSameResults()
    {
        // Arrange
        const string key = "test:delete";
        const string value = "to_be_deleted";
        
        // Act - Set with StackExchange
        await _stackExchangeDb.StringSetAsync(key, value);
        
        // Verify exists
        var existsBefore = await _respireClient.ExistsAsync(key);
        existsBefore.AsInteger().Should().Be(1);
        
        // Delete with Respire
        await _respireClient.DelAsync(key);
        
        // Verify deleted with both clients
        var existsAfterSE = await _stackExchangeDb.KeyExistsAsync(key);
        var existsAfterR = await _respireClient.ExistsAsync(key);
        
        // Assert
        existsAfterSE.Should().BeFalse();
        existsAfterR.AsInteger().Should().Be(0);
    }
    
    [Test]
    public async Task PingOperation_ShouldWork()
    {
        // Act
        var respireResult = await _respireClient.PingWithResponseAsync();
        var stackExchangeResult = await _stackExchangeDb.PingAsync();
        
        // Assert - Just verify they both succeed
        respireResult.Should().NotBeNull();
        respireResult.ToString().Should().Be("PONG");
        stackExchangeResult.TotalMilliseconds.Should().BeGreaterThan(0);
    }
    
    [Test]
    public async Task MixedOperations_ShouldMaintainConsistency()
    {
        // This test mixes operations between both clients to ensure they can work together
        const string listKey = "test:list";
        const string hashKey = "test:hash";
        const string setKey = "test:set";
        
        // List operations
        await _stackExchangeDb.ListLeftPushAsync(listKey, "item1");
        await _stackExchangeDb.ListLeftPushAsync(listKey, "item2");
        await _stackExchangeDb.ListLeftPushAsync(listKey, "item3");
        
        // Hash operations  
        await _stackExchangeDb.HashSetAsync(hashKey, "field1", "value1");
        await _stackExchangeDb.HashSetAsync(hashKey, "field2", "value2");
        
        // Set operations
        await _stackExchangeDb.SetAddAsync(setKey, "member1");
        await _stackExchangeDb.SetAddAsync(setKey, "member2");
        
        // Verify with Respire EXISTS
        var listExists = await _respireClient.ExistsAsync(listKey);
        var hashExists = await _respireClient.ExistsAsync(hashKey);
        var setExists = await _respireClient.ExistsAsync(setKey);
        
        listExists.AsInteger().Should().Be(1);
        hashExists.AsInteger().Should().Be(1);
        setExists.AsInteger().Should().Be(1);
        
        // Clean up with mixed clients
        await _respireClient.DelAsync(listKey);
        var deleted = await _stackExchangeDb.KeyDeleteAsync(new RedisKey[] { hashKey, setKey });
        
        deleted.Should().Be(2);
        
        // Verify all deleted
        var allDeleted = await _stackExchangeDb.KeyExistsAsync(listKey) ||
                        await _stackExchangeDb.KeyExistsAsync(hashKey) ||
                        await _stackExchangeDb.KeyExistsAsync(setKey);
        allDeleted.Should().BeFalse();
    }
    
    [Test]
    public async Task ExpireAndTTL_Operations_ShouldWork()
    {
        // Arrange
        const string key = "test:expire";
        const string value = "expiring_value";
        const int expireSeconds = 10;
        
        // Act - Set with StackExchange
        await _stackExchangeDb.StringSetAsync(key, value);
        
        // Set expiration with Respire
        await _respireClient.ExpireAsync(key, expireSeconds);
        
        // Check TTL with Respire
        var ttl = await _respireClient.TtlAsync(key);
        
        // Assert
        ttl.AsInteger().Should().BeInRange(5, expireSeconds); // Allow some time variance
        
        // Verify with StackExchange
        var ttlSE = await _stackExchangeDb.KeyTimeToLiveAsync(key);
        ttlSE.Should().NotBeNull();
        ttlSE!.Value.TotalSeconds.Should().BeInRange(5, expireSeconds);
    }
    
    [Test] 
    public async Task BulkOperations_ShouldBeConsistent()
    {
        // Test that both clients can handle bulk operations and remain consistent
        const int itemCount = 100;
        var tasks = new List<Task>();
        
        // Alternate between clients for setting values
        for (int i = 0; i < itemCount; i++)
        {
            var key = $"bulk:item:{i}";
            var value = $"value_{i}";
            
            if (i % 2 == 0)
            {
                tasks.Add(_stackExchangeDb.StringSetAsync(key, value));
            }
            else
            {
                tasks.Add(_respireClient.SetAsync(key, value).AsTask());
            }
        }
        
        await Task.WhenAll(tasks);
        
        // Verify all values with alternating clients
        for (int i = 0; i < itemCount; i++)
        {
            var key = $"bulk:item:{i}";
            var expectedValue = $"value_{i}";
            
            if (i % 2 == 0)
            {
                var value = await _respireClient.GetAsync(key);
                value.ToString().Should().Be(expectedValue);
            }
            else
            {
                var value = await _stackExchangeDb.StringGetAsync(key);
                value.ToString().Should().Be(expectedValue);
            }
        }
    }
    
    [Test]
    [Arguments("simple")]
    [Arguments("with spaces")]
    [Arguments("with-dashes-and_underscores")]
    [Arguments("with:colons:and|pipes")]
    [Arguments("üñíçödé")]
    [Arguments("😀🎉🚀")]
    [Arguments("very_long_key_name_that_exceeds_typical_lengths_to_test_buffer_handling_in_both_clients_1234567890")]
    public async Task VariousKeyFormats_ShouldWork(string keySuffix)
    {
        // Test various key formats to ensure both clients handle them the same way
        var key = $"test:format:{keySuffix}";
        var value = $"value_for_{keySuffix}";
        
        // Set with Respire
        await _respireClient.SetAsync(key, value);
        
        // Get with StackExchange
        var retrievedSE = await _stackExchangeDb.StringGetAsync(key);
        retrievedSE.ToString().Should().Be(value);
        
        // Overwrite with StackExchange
        var newValue = $"new_{value}";
        await _stackExchangeDb.StringSetAsync(key, newValue);
        
        // Get with Respire
        var retrievedR = await _respireClient.GetAsync(key);
        retrievedR.ToString().Should().Be(newValue);
    }
    
    [Test]
    public async Task NullAndEmptyValues_ShouldBeHandledConsistently()
    {
        // Test null/empty value handling
        const string emptyKey = "test:empty";
        const string nullKey = "test:null";
        
        // Set empty string with StackExchange
        await _stackExchangeDb.StringSetAsync(emptyKey, "");
        var emptyFromRespire = await _respireClient.GetAsync(emptyKey);
        emptyFromRespire.ToString().Should().Be("");
        
        // Try to get non-existent key
        var nullFromSE = await _stackExchangeDb.StringGetAsync(nullKey);
        var nullFromRespire = await _respireClient.GetAsync(nullKey);
        
        nullFromSE.IsNull.Should().BeTrue();
        nullFromRespire.IsNull.Should().BeTrue();
    }
}