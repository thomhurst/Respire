namespace Respire.Protocol;

/// <summary>
/// Pre-encoded RESP frames for commands that take no arguments.
/// </summary>
internal static class RespCommands
{
    public static readonly byte[] Ping = "*1\r\n$4\r\nPING\r\n"u8.ToArray();
    public static readonly byte[] Quit = "*1\r\n$4\r\nQUIT\r\n"u8.ToArray();
    public static readonly byte[] RandomKey = "*1\r\n$9\r\nRANDOMKEY\r\n"u8.ToArray();
    public static readonly byte[] DbSize = "*1\r\n$6\r\nDBSIZE\r\n"u8.ToArray();
    public static readonly byte[] Info = "*1\r\n$4\r\nINFO\r\n"u8.ToArray();
    public static readonly byte[] Time = "*1\r\n$4\r\nTIME\r\n"u8.ToArray();
    public static readonly byte[] FlushDb = "*1\r\n$7\r\nFLUSHDB\r\n"u8.ToArray();
    public static readonly byte[] FlushAll = "*1\r\n$8\r\nFLUSHALL\r\n"u8.ToArray();
    public static readonly byte[] Save = "*1\r\n$4\r\nSAVE\r\n"u8.ToArray();
    public static readonly byte[] BgSave = "*1\r\n$6\r\nBGSAVE\r\n"u8.ToArray();
    public static readonly byte[] LastSave = "*1\r\n$8\r\nLASTSAVE\r\n"u8.ToArray();
    public static readonly byte[] Multi = "*1\r\n$5\r\nMULTI\r\n"u8.ToArray();
    public static readonly byte[] Exec = "*1\r\n$4\r\nEXEC\r\n"u8.ToArray();
    public static readonly byte[] Discard = "*1\r\n$7\r\nDISCARD\r\n"u8.ToArray();
    public static readonly byte[] Role = "*1\r\n$4\r\nROLE\r\n"u8.ToArray();
}
