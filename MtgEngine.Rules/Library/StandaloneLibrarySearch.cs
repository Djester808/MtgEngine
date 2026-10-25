using System.Text.Json;
using MtgEngine.Cards;

namespace MtgEngine.Rules.Library;

/// <summary>A server-created search instruction that can resume without a card source.</summary>
internal static class StandaloneLibrarySearch
{
    private const string Key = ":standalone-library-search";

    public static bool IsPrivateKey(string key) => string.Equals(key, Key, StringComparison.Ordinal);

    public static ImmutableDictionary<string, string> Save(Search search,
        ImmutableDictionary<string, string>? marks = null) =>
        (marks ?? ImmutableDictionary<string, string>.Empty).SetItem(Key, JsonSerializer.Serialize(search));

    public static Search? Read(ImmutableDictionary<string, string> extra) =>
        extra.TryGetValue(Key, out var saved) ? JsonSerializer.Deserialize<Search>(saved) : null;
}
