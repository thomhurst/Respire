using System.Text;
using Respire.Protocol;

Console.WriteLine("===== Respire RESP Protocol Demo =====\n");

// Demo: Writing RESP Commands
Console.WriteLine("1. Writing RESP Commands:");
Console.WriteLine("--------------------------");
DemoWriting();

// Demo: Parsing RESP Responses
Console.WriteLine("\n2. Parsing RESP Responses:");
Console.WriteLine("---------------------------");
DemoParsing();

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
    Console.WriteLine("Parsing RESP responses using RespParser:");

    ParseAndPrint("+OK\r\n"u8, "Simple String");
    ParseAndPrint(":1000\r\n"u8, "Integer");
    ParseAndPrint("$11\r\nHello World\r\n"u8, "Bulk String");
    ParseAndPrint("$-1\r\n"u8, "Null Bulk String");
    ParseAndPrint("#t\r\n"u8, "Boolean");
    ParseAndPrint("*2\r\n$3\r\nfoo\r\n:42\r\n"u8, "Array");
}

void ParseAndPrint(ReadOnlySpan<byte> data, string label)
{
    var pos = 0;
    if (RespParser.TryParseValue(data, ref pos, out var value) == RespParseStatus.Done)
    {
        Console.WriteLine($"Parsed {label}: Type={value.Type}, Value={value}");
        value.Dispose();
    }
    else
    {
        Console.WriteLine($"Failed to parse {label}");
    }
}

string EscapeString(string input)
{
    return input.Replace("\r", "\\r").Replace("\n", "\\n");
}
