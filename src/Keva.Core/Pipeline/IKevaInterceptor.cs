using Keva.Core.Protocol;

namespace Keva.Core.Pipeline;

public readonly struct CommandInfo
{
    public string Command { get; }
    public string[] Arguments { get; }
    
    public CommandInfo(string command, params string[] arguments)
    {
        Command = command;
        Arguments = arguments ?? Array.Empty<string>();
    }
}

public delegate ValueTask<RespValue> InterceptorDelegate(CommandInfo commandInfo, CancellationToken cancellationToken = default);

public interface IKevaInterceptor
{
    ValueTask<RespValue> InterceptAsync(
        CommandInfo commandInfo,
        InterceptorDelegate next,
        CancellationToken cancellationToken = default);
}