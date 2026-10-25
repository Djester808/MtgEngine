using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>Resolves each oracle once before applying the format's pure construction rules.</summary>
public sealed class CommanderDeckAdmission(ICardLookup cards)
{
    public async Task ValidateAsync(IReadOnlyList<string> deck, IReadOnlyList<string> commanders, CancellationToken ct = default)
    {
        var definitions = new Dictionary<string, CardDefinition>(StringComparer.Ordinal);
        foreach (var id in deck.Concat(commanders).Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            definitions.Add(id, await cards.GetByOracleIdAsync(id, ct).ConfigureAwait(false)
                ?? throw new InvalidRequestException("Card metadata is unavailable; this deck cannot be validated yet."));
        }
        CommanderDeckValidator.Validate([.. deck.Select(id => definitions[id])], [.. commanders.Select(id => definitions[id])]);
    }
}
