namespace Respire.Search;

// Keep the same descriptors and mutation declarations as the former generated dispatch.
internal static class RespireSearchCommands
{
    internal static readonly RespireCommand DictionaryAdd = RespireCommand.Create("FT.DICTADD");
    internal static readonly RespireCommand DictionaryDelete = RespireCommand.Create("FT.DICTDEL");
    internal static readonly RespireCommand DictionaryDump = RespireCommand.Create("FT.DICTDUMP", RespireCacheMutation.ReadOnly);
    internal static readonly RespireCommand SpellCheck = RespireCommand.Create("FT.SPELLCHECK", RespireCacheMutation.ReadOnly);
    internal static readonly RespireCommand AddSuggestion = RespireCommand.Create("FT.SUGADD");
    internal static readonly RespireCommand DeleteSuggestion = RespireCommand.Create("FT.SUGDEL");
    internal static readonly RespireCommand GetSuggestions = RespireCommand.Create("FT.SUGGET", RespireCacheMutation.ReadOnly);
    internal static readonly RespireCommand GetSuggestionCount = RespireCommand.Create("FT.SUGLEN", RespireCacheMutation.ReadOnly);
    internal static readonly RespireCommand ConfigGet = RespireCommands.Search.FT_CONFIG_GET;
    internal static readonly RespireCommand ConfigSet = RespireCommands.Search.FT_CONFIG_SET;
    internal static readonly RespireCommand UpdateSynonyms = RespireCommand.Create("FT.SYNUPDATE");
    internal static readonly RespireCommand GetSynonyms = RespireCommand.Create("FT.SYNDUMP", RespireCacheMutation.ReadOnly);
    internal static readonly RespireCommand GetTagValues = RespireCommand.Create("FT.TAGVALS", RespireCacheMutation.ReadOnly);
    internal static readonly RespireCommand ListIndexes = RespireCommand.Create("FT._LIST", RespireCacheMutation.ReadOnly);
    internal static readonly RespireCommand AddAlias = RespireCommand.Create("FT.ALIASADD");
    internal static readonly RespireCommand DeleteAlias = RespireCommand.Create("FT.ALIASDEL");
    internal static readonly RespireCommand UpdateAlias = RespireCommand.Create("FT.ALIASUPDATE");
    internal static readonly RespireCommand ListAliases = RespireCommand.Create("FT.ALIASLIST", RespireCacheMutation.ReadOnly);
    internal static readonly RespireCommand Create = RespireCommand.Create("FT.CREATE");
    internal static readonly RespireCommand Drop = RespireCommand.Create("FT.DROPINDEX");
    internal static readonly RespireCommand Alter = RespireCommand.Create("FT.ALTER");
    internal static readonly RespireCommand Info = RespireCommand.Create("FT.INFO", RespireCacheMutation.ReadOnly);
    internal static readonly RespireCommand Search = RespireCommand.Create("FT.SEARCH", RespireCacheMutation.ReadOnly);
    internal static readonly RespireCommand Aggregate = RespireCommand.Create("FT.AGGREGATE", RespireCacheMutation.ReadOnly);
    internal static readonly RespireCommand Hybrid = RespireCommand.Create("FT.HYBRID", RespireCacheMutation.ReadOnly);
    internal static readonly RespireCommand Profile = RespireCommand.Create("FT.PROFILE", RespireCacheMutation.ReadOnly);
    internal static readonly RespireCommand Explain = RespireCommand.Create("FT.EXPLAIN", RespireCacheMutation.ReadOnly);
    internal static readonly RespireCommand ExplainCli = RespireCommand.Create("FT.EXPLAINCLI", RespireCacheMutation.ReadOnly);
    internal static readonly RespireCommand CursorRead = RespireCommands.Search.FT_CURSOR_READ;
    internal static readonly RespireCommand CursorDelete = RespireCommands.Search.FT_CURSOR_DEL;
}
