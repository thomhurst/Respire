using System.Buffers;
using System.Text;
using Keva.Core.Protocol;

Console.WriteLine("===== Keva RESP Protocol Demo =====\n");

// Create pools for zero-allocation operations
var bytePool = ArrayPool<byte>.Shared;
var valuePool = ArrayPool<RespValue>.Shared;

// Demo: Writing RESP Commands
Console.WriteLine("1. Writing RESP Commands:");
Console.WriteLine("--------------------------");
DemoWriting();

// Demo: Parsing RESP Responses
Console.WriteLine("\n2. Parsing RESP Responses:");
Console.WriteLine("---------------------------");
DemoParsing();

// Demo: Round-trip operations
Console.WriteLine("\n3. Round-trip Operations:");
Console.WriteLine("-------------------------");
DemoRoundTrip();

Console.WriteLine("\nPress any key to exit...");
Console.ReadKey();

void DemoWriting()
{
    var buffer = new ArrayBufferWriter<byte>(1024);
    var writer = new RespWriter(buffer, bytePool);
    
    // Write a SET command
    writer.WriteCommand("SET", "mykey", "Hello, Keva!");
    var setCommand = Encoding.UTF8.GetString(buffer.WrittenSpan);
    Console.WriteLine($"SET command: {EscapeString(setCommand)}");
    
    // Write a GET command
    buffer.Clear();
    writer = new RespWriter(buffer, bytePool);
    writer.WriteCommand("GET", "mykey");
    var getCommand = Encoding.UTF8.GetString(buffer.WrittenSpan);
    Console.WriteLine($"GET command: {EscapeString(getCommand)}");
    
    // Write different RESP types
    buffer.Clear();
    writer = new RespWriter(buffer, bytePool);
    
    // Simple string
    writer.Write(RespValue.SimpleString("OK"));
    Console.WriteLine($"Simple String: {EscapeString(Encoding.UTF8.GetString(buffer.WrittenSpan))}");
    
    buffer.Clear();
    writer = new RespWriter(buffer, bytePool);
    
    // Integer
    writer.Write(RespValue.Integer(42));
    Console.WriteLine($"Integer: {EscapeString(Encoding.UTF8.GetString(buffer.WrittenSpan))}");
    
    buffer.Clear();
    writer = new RespWriter(buffer, bytePool);
    
    // Array
    var array = RespValue.Array(
        RespValue.BulkString("first"),
        RespValue.Integer(2),
        RespValue.SimpleString("third")
    );
    writer.Write(array);
    Console.WriteLine($"Array: {EscapeString(Encoding.UTF8.GetString(buffer.WrittenSpan))}");
}

void DemoParsing()
{
    // Parse simple string
    var simpleStringData = "+OK\r\n"u8.ToArray();
    var reader = new RespReader(simpleStringData, bytePool, valuePool);
    if (reader.TryRead(out var value))
    {
        Console.WriteLine($"Parsed Simple String: Type={value.Type}, Value={value.AsString()}");
    }
    
    // Parse bulk string
    var bulkStringData = "$11\r\nHello World\r\n"u8.ToArray();
    reader = new RespReader(bulkStringData, bytePool, valuePool);
    if (reader.TryRead(out value))
    {
        Console.WriteLine($"Parsed Bulk String: Type={value.Type}, Value={value.AsString()}");
    }
    
    // Parse integer
    var integerData = ":1000\r\n"u8.ToArray();
    reader = new RespReader(integerData, bytePool, valuePool);
    if (reader.TryRead(out value))
    {
        Console.WriteLine($"Parsed Integer: Type={value.Type}, Value={value.AsInteger()}");
    }
    
    // Parse null
    var nullData = "_\r\n"u8.ToArray();
    reader = new RespReader(nullData, bytePool, valuePool);
    if (reader.TryRead(out value))
    {
        Console.WriteLine($"Parsed Null: Type={value.Type}, IsNull={value.IsNull}");
    }
    
    // Parse array
    var arrayData = "*3\r\n$3\r\nSET\r\n$3\r\nkey\r\n$5\r\nvalue\r\n"u8.ToArray();
    reader = new RespReader(arrayData, bytePool, valuePool);
    if (reader.TryRead(out value))
    {
        Console.WriteLine($"Parsed Array: Type={value.Type}, Length={value.AsArray().Length}");
        var items = value.AsArray().Span;
        for (int i = 0; i < items.Length; i++)
        {
            Console.WriteLine($"  Item[{i}]: {items[i].AsString()}");
        }
    }
    
    // Parse error
    var errorData = "-ERR unknown command\r\n"u8.ToArray();
    reader = new RespReader(errorData, bytePool, valuePool);
    if (reader.TryRead(out value))
    {
        Console.WriteLine($"Parsed Error: Type={value.Type}, IsError={value.IsError}, Message={value.GetErrorMessage()}");
    }
    
    // Parse RESP3 types
    var boolData = "#t\r\n"u8.ToArray();
    reader = new RespReader(boolData, bytePool, valuePool);
    if (reader.TryRead(out value))
    {
        Console.WriteLine($"Parsed Boolean: Type={value.Type}, Value={value.AsBoolean()}");
    }
    
    var doubleData = ",3.14159\r\n"u8.ToArray();
    reader = new RespReader(doubleData, bytePool, valuePool);
    if (reader.TryRead(out value))
    {
        Console.WriteLine($"Parsed Double: Type={value.Type}, Value={value.AsDouble()}");
    }
}

void DemoRoundTrip()
{
    var originalArray = RespValue.Array(
        RespValue.BulkString("HSET"),
        RespValue.BulkString("user:1000"),
        RespValue.BulkString("name"),
        RespValue.BulkString("John Doe"),
        RespValue.BulkString("age"),
        RespValue.BulkString("30")
    );
    
    // Write to buffer
    var buffer = new ArrayBufferWriter<byte>(1024);
    var writer = new RespWriter(buffer, bytePool);
    writer.Write(originalArray);
    
    Console.WriteLine($"Original command: HSET user:1000 name \"John Doe\" age 30");
    Console.WriteLine($"Serialized: {EscapeString(Encoding.UTF8.GetString(buffer.WrittenSpan))}");
    
    // Parse back
    var reader = new RespReader(buffer.WrittenMemory, bytePool, valuePool);
    if (reader.TryRead(out var parsedValue))
    {
        var items = parsedValue.AsArray().Span;
        Console.WriteLine($"Parsed back: {items.Length} items");
        for (int i = 0; i < items.Length; i++)
        {
            Console.WriteLine($"  [{i}]: {items[i].AsString()}");
        }
    }
    
    // Verify equality
    Console.WriteLine($"Values are equal: {originalArray.Equals(parsedValue)}");
}

string EscapeString(string input)
{
    return input.Replace("\r", "\\r").Replace("\n", "\\n");
}
