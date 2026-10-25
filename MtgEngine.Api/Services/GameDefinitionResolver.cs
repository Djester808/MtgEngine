using System.Collections.Immutable;
using MtgEngine.Cards;

namespace MtgEngine.Api.Services;

/// <summary>Printed card data from the existing catalog, without a claim of executable rules coverage.</summary>
public interface IPrintedCardLookup
{
    Task<CardDefinition?> FindPrintedAsync(string oracleId, CancellationToken ct = default);
}

/// <summary>Resolves the definitions for an admitted deck; compiled rules take precedence over catalog data.</summary>
public sealed class GameDefinitionResolver(ICardFilePool compiled, IPrintedCardLookup? catalog = null)
{
    public async Task<IDefinitionStore> ResolveAsync(IEnumerable<string> oracleIds, bool allowHonourSystem, CancellationToken ct)
    {
        var definitions = ImmutableDictionary.CreateBuilder<string, CardDefinition>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var id in oracleIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var definition = compiled.ByOracleId(id);
            if (definition is null && allowHonourSystem && catalog is not null
                && compiled.StatusOf(id).Status != ImplementationStatus.Excluded)
                definition = await catalog.FindPrintedAsync(id, ct).ConfigureAwait(false);
            if (definition is null)
                missing.Add(id);
            else
                definitions[id] = definition;
        }
        if (missing.Count > 0)
        {
            var names = string.Join(", ", missing.Take(DeckGate.NamedInRefusal).Select(id => compiled.StatusOf(id).Name));
            var rest = missing.Count > DeckGate.NamedInRefusal ? "; and more" : string.Empty;
            throw new InvalidRequestException($"Card definitions unavailable for {missing.Count} card(s): {names}{rest}. Choose another deck or retry after the server's card data is updated.");
        }
        return new Snapshot(definitions.ToImmutable(), compiled);
    }

    private sealed class Snapshot(ImmutableDictionary<string, CardDefinition> definitions, IDefinitionStore compiled) : IDefinitionStore
    {
        public CardDefinition? ByOracleId(string oracleId) => definitions.GetValueOrDefault(oracleId) ?? compiled.ByOracleId(oracleId);
        public CardDefinition? ByName(string name) => definitions.Values.FirstOrDefault(card => string.Equals(card.Name, name, StringComparison.OrdinalIgnoreCase)) ?? compiled.ByName(name);
    }
}
