using System.Collections.Immutable;
using MtgEngine.Cards;

namespace MtgEngine.Api.Services;

/// <summary>
/// What the deck gate is allowed to know about a card: how far along it is, and — when it is
/// excluded — why.
/// </summary>
/// <remarks>
/// The gate reads one status field and nothing else (the fourth rule of the card plan: a card is
/// enforced, badged honour-system, or refused, and there is no fourth state). The name and the
/// reason ride along because a refusal has to be readable by the player who wrote the deck, not
/// because the gate reasons about them.
/// </remarks>
public sealed record CardStatus(string OracleId, string Name, ImplementationStatus Status, string? Reason = null)
{
    /// <summary>An oracle id no file covers: not translated, and named by its id because nothing knows better.</summary>
    public static CardStatus Unknown(string oracleId) =>
        new(oracleId, oracleId, ImplementationStatus.Untranslated);
}

/// <summary>The definitions this server can play, and how far along each one is.</summary>
public interface ICardFilePool : IDefinitionStore
{
    CardStatus StatusOf(string oracleId);

    int Count { get; }
}

/// <summary>
/// Every file under <c>cards/</c>, read once at startup.
/// </summary>
/// <remarks>
/// <c>cards/</c> is generated: only the pipeline writes it, and this only ever reads it. The
/// directory being absent or empty is a normal state — a fresh checkout has no card files, and
/// the board is still meant to come up, with every deck admitted honour-system or refused
/// depending on the table. So a missing directory is an empty pool, not a startup failure.
/// <para>
/// A file that will not parse is skipped and logged rather than thrown: one bad file out of
/// thousands must not take the server down, and the pipeline's own gate is where a malformed
/// file is supposed to be caught.
/// </para>
/// </remarks>
public sealed class CardFilePool : ICardFilePool
{
    private readonly ImmutableDictionary<string, CardFile> _byOracleId;
    private readonly ImmutableDictionary<string, CardFile> _byName;

    public CardFilePool(string directory, ILogger<CardFilePool>? logger = null)
    {
        var byOracleId = ImmutableDictionary.CreateBuilder<string, CardFile>(StringComparer.OrdinalIgnoreCase);
        var byName = ImmutableDictionary.CreateBuilder<string, CardFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Files(directory))
        {
            CardFile file;
            try
            {
                file = CardFileSerializer.Read(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or NotSupportedException)
            {
                logger?.LogWarning(ex, "Skipping unreadable card file {Path}.", Path.GetFileName(path));
                continue;
            }

            byOracleId[file.OracleId] = file;
            byName[file.Name] = file;
        }

        _byOracleId = byOracleId.ToImmutable();
        _byName = byName.ToImmutable();
    }

    public int Count => _byOracleId.Count;

    public CardDefinition? ByName(string name) =>
        name is not null && _byName.TryGetValue(name, out var file) ? file.Definition : null;

    public CardDefinition? ByOracleId(string oracleId) =>
        oracleId is not null && _byOracleId.TryGetValue(oracleId, out var file) ? file.Definition : null;

    public CardStatus StatusOf(string oracleId)
    {
        if (oracleId is null || !_byOracleId.TryGetValue(oracleId, out var file))
            return CardStatus.Unknown(oracleId ?? string.Empty);

        var reason = file.Source is ExcludedProvenance excluded ? excluded.Reason : null;
        return new CardStatus(file.OracleId, file.Name, file.Status, reason);
    }

    private static IEnumerable<string> Files(string directory) =>
        string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)
            ? []
            : Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
}
