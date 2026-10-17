using Respire.Protocol;

namespace Respire;

internal static partial class AclParser
{
    internal static RespireAclRole? Role(in RespValue value)
    {
        if (value.IsNull) return null;
        var fields = Fields(in value);
        var users = ByteStrings(Take(fields, "users"));
        var selectorsValue = Take(fields, "selectors");
        var items = Items(in selectorsValue);
        var selectors = new RespireAclSelector[items.Length];
        for (var index = 0; index < items.Length; index++) selectors[index] = Selector(in items[index]);
        var commands = Text(Take(fields, "commands"));
        var keys = Patterns(Take(fields, "keys"));
        var channels = fields.Remove("channels", out var channelValue) ? Patterns(in channelValue) : null;
        return new(new(commands, keys, channels, OwnRemaining(fields)), selectors, users);
    }
}

internal static partial class CommandMetadataParser
{
    internal static RespireConfigurationInfo[] Configuration(in RespValue value)
    {
        var items = value.AsArray();
        var result = new RespireConfigurationInfo[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            var fields = Fields(in items[index]);
            var name = Text(Take(fields, "name"));
            var type = Text(Take(fields, "type"));
            var flags = Strings(Take(fields, "flags"));
            var alias = OptionalText(fields, "alias");
            var values = OptionalStrings(fields, "values");
            var range = OptionalStrings(fields, "range");
            if (range.Length is not (0 or 2)) throw new RespireProtocolException("Configuration ranges must contain two bounds.");
            result[index] = new(name, type, flags, alias, values, range, OwnFields(fields));
        }
        return result;
    }
}
