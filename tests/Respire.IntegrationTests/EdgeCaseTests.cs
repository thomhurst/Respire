using FluentAssertions;
using Respire.FastClient;
using StackExchange.Redis;
using TUnit.Core;
using TUnit.Assertions;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestFixture>(Shared = SharedType.Keyed)]
public class EdgeCaseTests
{
    private readonly RedisTestFixture _fixture;
    private RespireClient _respireClient = null!;
    private IConnectionMultiplexer _stackExchangeMultiplexer = null!;
    private IDatabase _stackExchangeDb = null!;
    
    public EdgeCaseTests(RedisTestFixture fixture)
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
    public async Task VeryLargeValues_HandledCorrectly()
    {
        const string largeKey = "test:large";
        
        // Create a large value (1MB)
        var largeValue = new string('x', 1024 * 1024);
        
        // Set with StackExchange
        await _stackExchangeDb.StringSetAsync(largeKey, largeValue);
        
        // Get with Respire
        var retrieved = await _respireClient.GetAsync(largeKey);
        retrieved.ToString().Length.Should().Be(1024 * 1024);
        retrieved.ToString().Should().StartWith("xxxx");
        retrieved.ToString().Should().EndWith("xxxx");
    }
    
    [Test]
    public async Task EmptyStringValues_HandledCorrectly()
    {
        const string emptyKey1 = "test:empty1";
        const string emptyKey2 = "test:empty2";
        
        // Set empty string with StackExchange
        await _stackExchangeDb.StringSetAsync(emptyKey1, "");
        
        // Get with Respire
        var empty1 = await _respireClient.GetAsync(emptyKey1);
        empty1.IsNull.Should().BeFalse();
        empty1.ToString().Should().Be("");
        
        // Set empty string with Respire
        await _respireClient.SetAsync(emptyKey2, "");
        
        // Get with StackExchange
        var empty2 = await _stackExchangeDb.StringGetAsync(emptyKey2);
        empty2.HasValue.Should().BeTrue();
        empty2.ToString().Should().Be("");
    }
    
    [Test]
    public async Task NonExistentKeys_ReturnNull()
    {
        const string nonExistentKey = "test:nonexistent";
        
        // Get non-existent with both clients
        var seNull = await _stackExchangeDb.StringGetAsync(nonExistentKey);
        var rNull = await _respireClient.GetAsync(nonExistentKey);
        
        seNull.IsNull.Should().BeTrue();
        rNull.IsNull.Should().BeTrue();
        
        // EXISTS on non-existent
        var seExists = await _stackExchangeDb.KeyExistsAsync(nonExistentKey);
        var rExists = await _respireClient.ExistsAsync(nonExistentKey);
        
        seExists.Should().BeFalse();
        rExists.AsInteger().Should().Be(0);
        
        // DELETE non-existent
        var seDeleted = await _stackExchangeDb.KeyDeleteAsync(nonExistentKey);
        await _respireClient.DelAsync(nonExistentKey); // Should not throw
        
        seDeleted.Should().BeFalse();
    }
    
    [Test]
    public async Task SpecialCharacters_InKeysAndValues()
    {
        // Test various special characters
        var testCases = new[]
        {
            ("key with spaces", "value with spaces"),
            ("key:with:colons", "value:with:colons"),
            ("key-with-dashes", "value-with-dashes"),
            ("key_with_underscores", "value_with_underscores"),
            ("key.with.dots", "value.with.dots"),
            ("key/with/slashes", "value/with/slashes"),
            ("key\\with\\backslashes", "value\\with\\backslashes"),
            ("key'with'quotes", "value'with'quotes"),
            ("key\"with\"doublequotes", "value\"with\"doublequotes"),
            ("key[with]brackets", "value[with]brackets"),
            ("key{with}braces", "value{with}braces"),
            ("key|with|pipes", "value|with|pipes"),
            ("key@with@at", "value@with@at"),
            ("key#with#hash", "value#with#hash"),
            ("key$with$dollar", "value$with$dollar"),
            ("key%with%percent", "value%with%percent"),
            ("key^with^caret", "value^with^caret"),
            ("key&with&ampersand", "value&with&ampersand"),
            ("key*with*asterisk", "value*with*asterisk"),
            ("key(with)parens", "value(with)parens"),
            ("key+with+plus", "value+with+plus"),
            ("key=with=equals", "value=with=equals"),
            ("key<with>angles", "value<with>angles"),
            ("key?with?question", "value?with?question"),
            ("key!with!exclamation", "value!with!exclamation"),
            ("key~with~tilde", "value~with~tilde"),
            ("key`with`backtick", "value`with`backtick"),
        };
        
        foreach (var (key, value) in testCases)
        {
            // Set with Respire
            await _respireClient.SetAsync(key, value);
            
            // Get with StackExchange
            var retrieved = await _stackExchangeDb.StringGetAsync(key);
            retrieved.ToString().Should().Be(value, $"Failed for key: {key}");
        }
    }
    
    [Test]
    public async Task UnicodeAndEmoji_HandledCorrectly()
    {
        var unicodeCases = new[]
        {
            ("unicode:chinese", "你好世界"),
            ("unicode:japanese", "こんにちは世界"),
            ("unicode:arabic", "مرحبا بالعالم"),
            ("unicode:hebrew", "שלום עולם"),
            ("unicode:russian", "Привет мир"),
            ("unicode:emoji", "😀🎉🚀💻🔥"),
            ("unicode:mixed", "Hello世界🌍")
        };
        
        foreach (var (key, value) in unicodeCases)
        {
            // Set with StackExchange
            await _stackExchangeDb.StringSetAsync(key, value);
            
            // Get with Respire
            var retrieved = await _respireClient.GetAsync(key);
            retrieved.ToString().Should().Be(value);
            
            // Overwrite with Respire
            var newValue = value + "✨";
            await _respireClient.SetAsync(key, newValue);
            
            // Verify with StackExchange
            var updated = await _stackExchangeDb.StringGetAsync(key);
            updated.ToString().Should().Be(newValue);
        }
    }
    
    [Test]
    public async Task ConcurrentModifications_MaintainConsistency()
    {
        const string sharedKey = "test:concurrent";
        const int iterations = 100;
        
        // Initialize counter
        await _stackExchangeDb.StringSetAsync(sharedKey, 0);
        
        // Concurrent increments from both clients
        var tasks = new List<Task>();
        
        for (var i = 0; i < iterations; i++)
        {
            if (i % 2 == 0)
            {
                tasks.Add(_stackExchangeDb.StringIncrementAsync(sharedKey));
            }
            else
            {
                tasks.Add(_respireClient.IncrWithResponseAsync(sharedKey).AsTask());
            }
        }
        
        await Task.WhenAll(tasks);
        
        // Both clients should see the same final value
        var finalSE = await _stackExchangeDb.StringGetAsync(sharedKey);
        var finalR = await _respireClient.GetAsync(sharedKey);
        
        finalSE.ToString().Should().Be(iterations.ToString());
        finalR.ToString().Should().Be(iterations.ToString());
    }
    
    [Test]
    public async Task TypeMismatch_HandledGracefully()
    {
        const string key = "test:typemismatch";
        
        // Set as string
        await _stackExchangeDb.StringSetAsync(key, "not_a_number");
        
        // Try to increment (should fail gracefully)
        try
        {
            await _stackExchangeDb.StringIncrementAsync(key);
            Assert.Fail("Expected an error for incrementing non-numeric value");
        }
        catch (RedisServerException ex)
        {
            ex.Message.Should().Contain("not an integer");
        }
        
        // Try with Respire (should also handle the error)
        var incrResult = await _respireClient.IncrWithResponseAsync(key);
        incrResult.IsError.Should().BeTrue();
    }
    
    [Test]
    public async Task RapidKeyCreationAndDeletion()
    {
        const int cycles = 50;
        
        for (var i = 0; i < cycles; i++)
        {
            var key = $"rapid:{i}";
            
            // Create with alternating clients
            if (i % 2 == 0)
            {
                await _stackExchangeDb.StringSetAsync(key, $"value_{i}");
            }
            else
            {
                await _respireClient.SetAsync(key, $"value_{i}");
            }
            
            // Verify exists
            var exists = i % 2 == 0
                ? (await _respireClient.ExistsAsync(key)).AsBoolean()
                : await _stackExchangeDb.KeyExistsAsync(key);
            
            if (i % 2 == 0)
            {
                exists.Should().BeTrue();
            }
            else
            {
                exists.Should().BeTrue();
            }
            
            // Delete with opposite client
            if (i % 2 == 0)
            {
                await _respireClient.DelAsync(key);
            }
            else
            {
                await _stackExchangeDb.KeyDeleteAsync(key);
            }
            
            // Verify deleted
            var stillExists = await _stackExchangeDb.KeyExistsAsync(key);
            stillExists.Should().BeFalse();
        }
    }
}