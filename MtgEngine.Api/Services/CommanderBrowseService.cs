using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Enums;

namespace MtgEngine.Api.Services;

public sealed class CommanderBrowseService(IScryfallService cards, ICommanderStatsService stats)
{
    public async Task<CommanderSummaryDto[]> SearchAsync(string? q, int limit, string? set)
    {
        limit = Math.Clamp(limit, 1, 200);

        var matches = await cards.SearchCommandersAsync(q, limit, set);
        if (matches.Length == 0)
            return [];

        // Deck counts come from the ranked list; anything not in it simply has none.
        var ranked = await stats.GetTopCommandersAsync(limit: 200);
        var counts = ranked.ToDictionary(c => c.OracleId, c => c.DeckCount, StringComparer.OrdinalIgnoreCase);

        var results = matches
            .Select(d => new
            {
                Def = d,
                Count = counts.TryGetValue(d.OracleId, out var n) ? n : 0,
            })
            // Popular commanders first, then alphabetical, so the list is stable.
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Def.Name, StringComparer.OrdinalIgnoreCase)
            .Select((x, i) => new CommanderSummaryDto
            {
                OracleId = x.Def.OracleId,
                Name = x.Def.Name,
                ImageUri = x.Def.ImageUriNormal,
                ImageUriArtCrop = x.Def.ImageUriArtCrop,
                ColorIdentity = [.. x.Def.ColorIdentity.Select(c => c switch
                {
                    ManaColor.White => "W",
                    ManaColor.Blue => "U",
                    ManaColor.Black => "B",
                    ManaColor.Red => "R",
                    ManaColor.Green => "G",
                    _ => "C",
                })],
                ManaCost = string.IsNullOrEmpty(x.Def.ManaCostRaw) ? null : x.Def.ManaCostRaw,
                DeckCount = x.Count,
                Rank = i + 1,
            })
            .ToArray();

        return results;
    }
}
