namespace Respire.Networking;

/// <summary>Shared Unix transport validation for public options and physical connections.</summary>
internal static class UnixSocketConfiguration
{
    internal static void Validate(string path, bool useTls,
        RespireMaintenanceNotificationMode maintenanceNotifications, string optionName)
    {
        if (!RespireEndpoint.IsValidUnixPath(path))
            throw new RespireConfigurationException(
                $"{optionName} with port zero requires an absolute Unix socket path. " +
                "TCP ports must be between 1 and 65535; use RespireEndpoint.UnixSocket(path) for a socket.");
        if (useTls)
            throw new RespireConfigurationException("Redis Unix sockets do not use TLS. Configure filesystem access controls instead.");
        if (maintenanceNotifications == RespireMaintenanceNotificationMode.Enabled)
            throw new RespireConfigurationException("Unix sockets cannot use maintenance handoffs to advertised TCP endpoints.");
    }
}
