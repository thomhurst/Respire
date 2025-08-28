using System.Buffers;
using System.Text;
using Respire.Protocol;

Console.WriteLine("===== Respire RESP Protocol Demo =====\n");

// Create pools for zero-allocation operations
var bytePool = ArrayPool<byte>.Shared;

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
    Console.WriteLine("Writing RESP commands using pre-compiled commands:");
    
    // Use pre-compiled commands for maximum performance
    var pingCommand = RespCommands.Ping;
    Console.WriteLine($"PING command: {EscapeString(Encoding.UTF8.GetString(pingCommand))}");
    
    var infoCommand = RespCommands.Info;
    Console.WriteLine($"INFO command: {EscapeString(Encoding.UTF8.GetString(infoCommand))}");
    
    var dbSizeCommand = RespCommands.DbSize;
    Console.WriteLine($"DBSIZE command: {EscapeString(Encoding.UTF8.GetString(dbSizeCommand))}");
}

void DemoParsing()
{
    Console.WriteLine("Parsing RESP responses using RespireReader:");
    
    // Parse simple string
    var simpleStringData = "+OK\r\n"u8.ToArray();
    var sequence = new ReadOnlySequence<byte>(simpleStringData);
    var reader = new RespPipelineReader(sequence);
    if (reader.TryReadValue(out var value))
    {
        Console.WriteLine($"Parsed Simple String: Type={value.Type}");
    }
    
    // Parse integer
    var integerData = ":1000\r\n"u8.ToArray();
    sequence = new ReadOnlySequence<byte>(integerData);
    reader = new RespPipelineReader(sequence);
    if (reader.TryReadValue(out value))
    {
        Console.WriteLine($"Parsed Integer: Type={value.Type}, Value={value.AsInteger()}");
    }
    
    // Parse bulk string
    var bulkStringData = "$11\r\nHello World\r\n"u8.ToArray();
    sequence = new ReadOnlySequence<byte>(bulkStringData);
    reader = new RespPipelineReader(sequence);
    if (reader.TryReadValue(out value))
    {
        Console.WriteLine($"Parsed Bulk String: Type={value.Type}");
    }
    
    // Parse null bulk string
    var nullData = "$-1\r\n"u8.ToArray();
    sequence = new ReadOnlySequence<byte>(nullData);
    reader = new RespPipelineReader(sequence);
    if (reader.TryReadValue(out value))
    {
        Console.WriteLine($"Parsed Null: Type={value.Type}, IsNull={value.IsNull}");
    }
    
    // Parse boolean
    var boolData = "#t\r\n"u8.ToArray();
    sequence = new ReadOnlySequence<byte>(boolData);
    reader = new RespPipelineReader(sequence);
    if (reader.TryReadValue(out value))
    {
        Console.WriteLine($"Parsed Boolean: Type={value.Type}, Value={value.AsBoolean()}");
    }
}

void DemoRoundTrip()
{
    Console.WriteLine("Demonstrating high-performance Redis client usage:");
    
    // Demonstrate parsing a response
    var responseData = ":1\r\n"u8.ToArray(); // Integer response: 1
    var sequence = new ReadOnlySequence<byte>(responseData);
    var reader = new RespPipelineReader(sequence);
    if (reader.TryReadValue(out var response))
    {
        Console.WriteLine($"Response: {response.AsInteger()} (fields added)");
    }
    
    Console.WriteLine("Pre-compiled commands provide maximum performance for Redis operations.");
}

string EscapeString(string input)
{
    return input.Replace("\r", "\\r").Replace("\n", "\\n");
}
