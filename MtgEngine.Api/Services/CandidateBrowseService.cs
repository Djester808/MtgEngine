using MtgEngine.Api.Dtos;
using MtgEngine.Api.Mapping;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public sealed class CandidateBrowseService(IScryfallService cards, ICandidateRanking ranking)
{
    private const int LatestSetScopeCount = 3;
    public async Task<CandidatePoolDto> BrowseAsync(string commanderOracleId, string? q, string? scope,
        string? types, int? cmcMin, int? cmcMax, string? focus, string? rank, int limit, int offset)
    {
        if (string.IsNullOrWhiteSpace(commanderOracleId))
            throw new InvalidRequestException("Choose a commander.");

        var commander = await cards.GetByOracleIdAsync(commanderOracleId);
        if (commander is null)
            throw new ResourceNotFoundException("Commander not found.");

        IReadOnlySet<string>? setCodes = null;
        bool gameChangersOnly = false;
        if (string.Equals(scope, "latest", StringComparison.OrdinalIgnoreCase))
            setCodes = await cards.GetRecentSetCodesAsync(maxSets: LatestSetScopeCount);
        else if (string.Equals(scope, "gamechangers", StringComparison.OrdinalIgnoreCase))
            gameChangersOnly = true;

        // Unknown type names are ignored rather than rejected: the filter is a
        // convenience, and a typo should not turn the whole browse list into an error.
        var typeFlags = CardType.None;
        foreach (var name in (types ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Enum.TryParse<CardType>(name, ignoreCase: true, out var parsed))
                typeFlags |= parsed;

        var focusTags = (focus ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        CardDefinition[] selected;
        int total;
        Dictionary<string, int> scoreOf = new(StringComparer.OrdinalIgnoreCase);

        if (string.Equals(rank, "synergy", StringComparison.OrdinalIgnoreCase))
        {
            var ranked = await ranking.RankAsync(new RankRequest(
                commander, q, setCodes, gameChangersOnly, typeFlags, cmcMin, cmcMax,
                focusTags, Math.Clamp(limit, 1, 200), Math.Max(0, offset)));

            selected = [.. ranked.Cards.Select(c => c.Card)];
            total = ranked.Total;
            foreach (var c in ranked.Cards)
                scoreOf[c.Card.OracleId] = c.Score;
        }
        else
        {
            (selected, total) = await cards.GetCandidatePoolAsync(
                commander.ColorIdentity.ToHashSet(), commander, q, setCodes, gameChangersOnly,
                typeFlags, cmcMin, cmcMax,
                Math.Clamp(limit, 1, 200), Math.Max(0, offset));
        }

        var rows = await Task.WhenAll(selected.Select(async d =>
        {
            var printings = await cards.GetPrintingsAsync(d.OracleId);

            // Prefer the printing from the set being browsed, so a card reprinted into
            // the latest set is labelled with that set rather than its oldest one.
            var printing = printings.FirstOrDefault(p =>
                    p.SetCode is not null && setCodes?.Contains(p.SetCode) == true)
                ?? printings.FirstOrDefault();

            // Ranked responses carry the score, so the client renders the same number it
            // was ordered by instead of fetching it again and risking a different answer.
            return new CandidateCardDto(
                DomainMapper.ToDto(d), printing?.ScryfallId, printing?.SetCode?.ToUpperInvariant(), printing?.SetName,
                scoreOf.TryGetValue(d.OracleId, out var score) && score >= 0 ? score : null);
        }));

        return new CandidatePoolDto(total, rows);
    }
}
