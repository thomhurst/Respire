using Respire.FastClient;
using StackExchange.Redis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestFixture>(Shared = SharedType.PerClass)]
public class GetSetOperationsTests(RedisTestFixture fixture)
{
    private readonly RedisTestFixture _fixture = fixture;
    private RespireClient? _respireClient;
    private ConnectionMultiplexer? _stackExchangeMultiplexer;
    private IDatabase? _stackExchangeDb;

    [Before(Test)]
    public async Task InitializeAsync()
    {
        // Parse connection string to get host and port
        var connectionString = _fixture.ConnectionString;
        var parts = connectionString.Split(',')[0].Split(':');
        var host = parts[0];
        var port = int.Parse(parts[1]);
        
        _respireClient = await RespireClient.CreateAsync(host, port);

        _stackExchangeMultiplexer = await ConnectionMultiplexer.ConnectAsync(_fixture.ConnectionString);
        _stackExchangeDb = _stackExchangeMultiplexer.GetDatabase();

        // Clean up before each test
        await _stackExchangeDb.ExecuteAsync("FLUSHDB");
    }

    [After(Test)]
    public async ValueTask DisposeAsync()
    {
        _respireClient?.Dispose();
        _stackExchangeMultiplexer?.Dispose();
    }

    [Test]
    public async Task Set_SimpleString_ShouldStoreCorrectValue()
    {
        // Arrange
        const string key = "test:simple:string";
        const string value = "Hello, World!";

        // Act
        await _respireClient!.SetAsync(key, value);

        // Assert - verify using StackExchange.Redis
        var retrievedValue = await _stackExchangeDb!.StringGetAsync(key);
        await Assert.That(retrievedValue.HasValue).IsTrue();
        await Assert.That(retrievedValue.ToString()).IsEqualTo(value);
    }

    [Test]
    public async Task Get_ExistingKey_ShouldReturnCorrectValue()
    {
        // Arrange
        const string key = "test:get:existing";
        const string expectedValue = "Test Value 123";
        
        // Set using StackExchange.Redis to ensure the value is there
        await _stackExchangeDb!.StringSetAsync(key, expectedValue);

        // Act
        var actualValue = await _respireClient!.GetAsync(key);

        // Assert
        await Assert.That(actualValue).IsNotDefault();
        await Assert.That(actualValue.AsString()).IsEqualTo(expectedValue);
    }

    [Test]
    public async Task Get_NonExistentKey_ShouldReturnNull()
    {
        // Arrange
        const string key = "test:get:nonexistent";

        // Act
        var value = await _respireClient!.GetAsync(key);

        // Assert
        await Assert.That(value.IsNull).IsTrue();
    }

    [Test]
    public async Task Set_EmptyString_ShouldStoreEmptyValue()
    {
        // Arrange
        const string key = "test:empty:string";
        const string value = "";

        // Act
        await _respireClient!.SetAsync(key, value);

        // Assert
        var retrievedValue = await _stackExchangeDb!.StringGetAsync(key);
        // StackExchange.Redis returns HasValue=false for empty strings, but the value is still there
        await Assert.That(retrievedValue.IsNull).IsFalse();
        await Assert.That(retrievedValue.ToString()).IsEqualTo(value);
    }

    [Test]
    public async Task Set_LongString_ShouldStoreCompleteValue()
    {
        // Arrange
        const string key = "test:long:string";
        var value = new string('A', 10000); // 10,000 character string

        // Act
        await _respireClient!.SetAsync(key, value);

        // Assert
        var retrievedValue = await _stackExchangeDb!.StringGetAsync(key);
        await Assert.That(retrievedValue.HasValue).IsTrue();
        await Assert.That(retrievedValue.ToString()).IsEqualTo(value);
        await Assert.That(retrievedValue.ToString().Length).IsEqualTo(10000);
    }

    [Test]
    public async Task Set_SpecialCharacters_ShouldHandleCorrectly()
    {
        // Arrange
        const string key = "test:special:chars";
        const string value = "Hello\r\nWorld\t!@#$%^&*()_+-=[]{}|;':\"<>?,./`~😀🎉";

        // Act
        await _respireClient!.SetAsync(key, value);

        // Assert
        var retrievedValue = await _stackExchangeDb!.StringGetAsync(key);
        await Assert.That(retrievedValue.HasValue).IsTrue();
        await Assert.That(retrievedValue.ToString()).IsEqualTo(value);
    }

    [Test]
    public async Task Set_NumericString_ShouldStoreAsString()
    {
        // Arrange
        const string key = "test:numeric:string";
        const string value = "12345";

        // Act
        await _respireClient!.SetAsync(key, value);

        // Assert
        var retrievedValue = await _stackExchangeDb!.StringGetAsync(key);
        await Assert.That(retrievedValue.HasValue).IsTrue();
        await Assert.That(retrievedValue.ToString()).IsEqualTo(value);
        
        // Verify it's stored as string, not converted to number
        await Assert.That(retrievedValue.ToString()).IsTypeOf<string>();
    }

    [Test]
    public async Task SetAndGet_Roundtrip_ShouldMaintainValue()
    {
        // Arrange
        const string key = "test:roundtrip";
        const string originalValue = "This is a test value for roundtrip";

        // Act
        await _respireClient!.SetAsync(key, originalValue);
        var retrievedValue = await _respireClient.GetAsync(key);

        // Assert
        await Assert.That(retrievedValue.IsNull).IsFalse();
        await Assert.That(retrievedValue.ToString()).IsEqualTo(originalValue);
        
        // Also verify with StackExchange
        var stackExchangeValue = await _stackExchangeDb!.StringGetAsync(key);
        await Assert.That(stackExchangeValue.ToString()).IsEqualTo(originalValue);
    }

    [Test]
    public async Task Set_OverwriteExistingKey_ShouldUpdateValue()
    {
        // Arrange
        const string key = "test:overwrite";
        const string originalValue = "Original Value";
        const string newValue = "Updated Value";

        // Act
        await _respireClient!.SetAsync(key, originalValue);
        await _respireClient.SetAsync(key, newValue);

        // Assert
        var retrievedValue = await _respireClient.GetAsync(key);
        await Assert.That(retrievedValue.ToString()).IsEqualTo(newValue);
        await Assert.That(retrievedValue.ToString()).IsNotEqualTo(originalValue);
        
        // Verify with StackExchange
        var stackExchangeValue = await _stackExchangeDb!.StringGetAsync(key);
        await Assert.That(stackExchangeValue.ToString()).IsEqualTo(newValue);
    }

    [Test]
    public async Task Set_MultipleKeys_ShouldStoreIndependently()
    {
        // Arrange
        var keyValuePairs = new Dictionary<string, string>
        {
            { "test:multi:key1", "Value 1" },
            { "test:multi:key2", "Value 2" },
            { "test:multi:key3", "Value 3" },
            { "test:multi:key4", "Value 4" },
            { "test:multi:key5", "Value 5" }
        };

        // Act
        foreach (var kvp in keyValuePairs)
        {
            await _respireClient!.SetAsync(kvp.Key, kvp.Value);
        }

        // Assert - verify each key has correct value
        foreach (var kvp in keyValuePairs)
        {
            var retrievedValue = await _respireClient!.GetAsync(kvp.Key);
            await Assert.That(retrievedValue.ToString()).IsEqualTo(kvp.Value);
            
            // Also verify with StackExchange
            var stackExchangeValue = await _stackExchangeDb!.StringGetAsync(kvp.Key);
            await Assert.That(stackExchangeValue.ToString()).IsEqualTo(kvp.Value);
        }
    }

    [Test]
    public async Task Get_AfterKeyDeleted_ShouldReturnNull()
    {
        // Arrange
        const string key = "test:delete:key";
        const string value = "To be deleted";
        
        await _respireClient!.SetAsync(key, value);
        await _stackExchangeDb!.KeyDeleteAsync(key); // Delete using StackExchange

        // Act
        var retrievedValue = await _respireClient.GetAsync(key);

        // Assert
        await Assert.That(retrievedValue.IsNull).IsTrue();
    }

    [Test]
    public async Task Set_KeyWithColon_ShouldWork()
    {
        // Arrange
        const string key = "user:profile:12345:settings:notifications";
        const string value = "enabled";

        // Act
        await _respireClient!.SetAsync(key, value);

        // Assert
        var retrievedValue = await _respireClient.GetAsync(key);
        await Assert.That(retrievedValue.ToString()).IsEqualTo(value);
    }

    [Test]
    public async Task Set_KeyWithSpaces_ShouldWork()
    {
        // Arrange
        const string key = "key with spaces";
        const string value = "value with spaces";

        // Act
        await _respireClient!.SetAsync(key, value);

        // Assert
        var retrievedValue = await _respireClient.GetAsync(key);
        await Assert.That(retrievedValue.ToString()).IsEqualTo(value);
    }

    [Test]
    public async Task Get_CaseSensitiveKeys_ShouldBeDifferent()
    {
        // Arrange
        const string key1 = "TestKey";
        const string key2 = "testkey";
        const string key3 = "TESTKEY";
        const string value1 = "Value1";
        const string value2 = "Value2";
        const string value3 = "Value3";

        // Act
        await _respireClient!.SetAsync(key1, value1);
        await _respireClient.SetAsync(key2, value2);
        await _respireClient.SetAsync(key3, value3);

        // Assert
        var retrieved1 = await _respireClient.GetAsync(key1);
        var retrieved2 = await _respireClient.GetAsync(key2);
        var retrieved3 = await _respireClient.GetAsync(key3);

        await Assert.That(retrieved1.ToString()).IsEqualTo(value1);
        await Assert.That(retrieved2.ToString()).IsEqualTo(value2);
        await Assert.That(retrieved3.ToString()).IsEqualTo(value3);
    }

    [Test]
    public async Task SetAndGet_BinaryData_ShouldPreserveContent()
    {
        // Arrange
        const string key = "test:binary:data";
        // Create a string with various byte values
        var binaryString = string.Concat(
            Enumerable.Range(0, 256)
                .Where(i => i != 0) // Exclude null terminator
                .Select(i => (char)i)
        );

        // Act
        await _respireClient!.SetAsync(key, binaryString);
        var retrievedValue = await _respireClient.GetAsync(key);

        // Assert
        await Assert.That(retrievedValue.IsNull).IsFalse();
        await Assert.That(retrievedValue.ToString()).IsEqualTo(binaryString);
        await Assert.That(retrievedValue.ToString().Length).IsEqualTo(binaryString.Length);
    }

    [Test]
    public async Task Set_RapidSuccession_ShouldHandleAllOperations()
    {
        // Arrange
        const int operationCount = 100;
        var tasks = new List<Task>();

        // Act - Set multiple keys in rapid succession
        for (var i = 0; i < operationCount; i++)
        {
            var key = $"test:rapid:key{i}";
            var value = $"value{i}";
            tasks.Add(_respireClient!.SetAsync(key, value).AsTask());
        }
        
        await Task.WhenAll(tasks);

        // Assert - Verify all values were set correctly
        for (var i = 0; i < operationCount; i++)
        {
            var key = $"test:rapid:key{i}";
            var expectedValue = $"value{i}";
            
            var actualValue = await _respireClient!.GetAsync(key);
            await Assert.That(actualValue.ToString()).IsEqualTo(expectedValue);
        }
    }
}