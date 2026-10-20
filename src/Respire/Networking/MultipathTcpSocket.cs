using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Respire.Networking;

internal static class MultipathTcpSocket
{
    internal static async Task<Socket> ConnectAsync(RespireEndpoint endpoint, RespireConnectionOptions options,
        ILogger? logger, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            logger?.MultipathTcpFallback("MPTCP is only supported on Linux");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                RespireConnection.ConfigureSocket(socket, options);
                await socket.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false);
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }

        var addresses = IPAddress.TryParse(endpoint.Host, out var address)
            ? [address] : await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        return await ConnectAddressesAsync(addresses, endpoint.Port, options, logger, cancellationToken).ConfigureAwait(false);
    }

    // Socket(SafeSocketHandle) cannot use .NET's multi-address connect: its descriptor cannot
    // be recreated by the runtime. Give each DNS candidate a fresh, configured native socket.
    internal static async Task<Socket> ConnectAddressesAsync(IPAddress[] addresses, int port,
        RespireConnectionOptions options, ILogger? logger, CancellationToken cancellationToken)
    {
        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6) continue;
            var socket = CreateLinux(address.AddressFamily, logger);
            try
            {
                RespireConnection.ConfigureSocket(socket, options);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
                    return socket;
                }
                catch (SocketException error) { lastError = error; }
            }
            catch { socket.Dispose(); throw; }
            socket.Dispose();
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw lastError ?? new SocketException((int)SocketError.HostNotFound);
    }

    internal static Socket CreateLinux(AddressFamily family, ILogger? logger,
        Func<int, int, int, int>? createSocket = null)
    {
        // Linux ABI values for .NET's supported architectures, not the managed enums.
        // These values are not portable to MIPS or SPARC. SOCK_CLOEXEC prevents descriptor inheritance.
        var domain = family switch
        {
            AddressFamily.InterNetwork => 2,
            AddressFamily.InterNetworkV6 => 10,
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };
        const int streamCloseOnExec = 1 | 0x80000;
        const int protocolMptcp = 262;
        // ProtocolType casts are rejected by System.Native on .NET 8 and .NET 10.
        int descriptor;
        try
        {
            descriptor = createSocket is null
                ? SocketNative(domain, streamCloseOnExec, protocolMptcp)
                : createSocket(domain, streamCloseOnExec, protocolMptcp);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            logger?.MultipathTcpFallback($"Linux socket binding unavailable: {error.GetType().Name}");
            return new Socket(family, SocketType.Stream, ProtocolType.Tcp);
        }
        if (descriptor == -1)
        {
            var error = Marshal.GetLastPInvokeError();
            // Documented MPTCP-unavailable errors only: EINVAL (old kernel),
            // ENOPROTOOPT (sysctl disabled), EPROTONOSUPPORT (not compiled in).
            if (error is 22 or 92 or 93)
            {
                logger?.MultipathTcpFallback($"Linux socket creation returned errno {error}");
                return new Socket(family, SocketType.Stream, ProtocolType.Tcp);
            }
            // The parameterless constructor translates native errno to SocketError on Unix.
            Marshal.SetLastPInvokeError(error);
            throw new SocketException();
        }

        var handle = new SafeSocketHandle((IntPtr)descriptor, ownsHandle: true);
        try { return new Socket(handle); }
        catch { handle.Dispose(); throw; }
    }

    // This blittable signature needs no generated marshalling. DllImport avoids the
    // LibraryImport generator's SkipLocalsInit, which violates our initialization policy.
    [DllImport("libc", EntryPoint = "socket", ExactSpelling = true, SetLastError = true,
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int SocketNative(int domain, int type, int protocol);
}
