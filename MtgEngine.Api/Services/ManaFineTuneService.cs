using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Mapping;

namespace MtgEngine.Api.Services;

public interface IManaFineTuneService
{
    Task<ManaFineTuneDto> GetFineTuneAsync(ManaFineTuneRequest request);
}

public sealed class ManaFineTuneService : IManaFineTuneService
{
    private readonly IAnthropicClient _anthropic;
    private readonly IAiCacheService _cache;
    private readonly ICardLookup _cards;
    private readonly ILogger<ManaFineTuneService> _logger;

    private const string ModelId = "claude-haiku-4-5-20251001";

    /// <summary>Bump when the model, the prompt, or the response shape changes.</summary>
    private const string CacheVersion = "claude-haiku-4-5-20251001-manatune-v3";

    public ManaFineTuneService(
        IAnthropicClient anthropic,
        IAiCacheService cache,
        ICardLookup cards,
        ILogger<ManaFineTuneService> logger)
    {
        _anthropic = anthropic;
        _cache = cache;
        _cards = cards;
        _logger = logger;
    }

    public Task<ManaFineTuneDto> GetFineTuneAsync(ManaFineTuneRequest req)
    {
        // AvgCmc is rounded to the same 1dp the prompt renders, so trivially different
        // floats do not produce different cache keys for an identical prompt.
        var keyParts = new[]
            {
                req.Format,
                string.Join(',', req.ActiveColors.OrderBy(c => c, StringComparer.Ordinal)),
                req.CurrentLands.ToString(),
                req.RecommendedLands.ToString(),
                req.AvgCmc.ToString("F1"),
            }
            .Concat(req.DeckCardNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        return _cache.GetOrCreateAsync(
            "mana-tune", CacheVersion, keyParts, () => BuildFineTuneAsync(req));
    }

    private async Task<ManaFineTuneDto> BuildFineTuneAsync(ManaFineTuneRequest req)
    {
        var colorNames = req.ActiveColors.Length > 0
            ? string.Join(", ", req.ActiveColors.Select(c => c switch
            {
                "W" => "White",
                "U" => "Blue",
                "B" => "Black",
                "R" => "Red",
                "G" => "Green",
                _ => c,
            }))
            : "Colorless";

        var deckContext = req.DeckCardNames.Length > 0
            ? $"\nCards in deck: {string.Join(", ", req.DeckCardNames)}"
            : string.Empty;

        var format = string.IsNullOrWhiteSpace(req.Format) ? "unknown" : req.Format;

        var prompt = $$"""
            You are a Magic: The Gathering mana base expert.

            Deck info:
            - Format: {{format}}
            - Colors: {{colorNames}}
            - Current land count: {{req.CurrentLands}}
            - Recommended land count: {{req.RecommendedLands}}
            - Average CMC (non-land): {{req.AvgCmc:F1}}{{deckContext}}

            Provide specific mana base fine-tuning advice for this deck. Focus on:
            1. Whether the land count should be adjusted and why
            2. Specific dual lands, fetch lands, or utility lands by exact card name suited to the colors and format
            3. Any mana acceleration (ramp) if the CMC warrants it

            Respond with ONLY this exact JSON (no markdown, no extra text):
            {
              "advice": ["tip 1", "tip 2"],
              "landSuggestions": [
                {"name": "Exact Card Name", "reason": "why this helps"},
                ...
              ]
            }

            Rules:
            - advice: 2–4 concise, actionable tips tailored to this specific deck and format
            - landSuggestions: 4–6 specific land cards (exact MTG names) that fit the color identity and format
            - Only suggest lands NOT already in the deck
            - Only use real, officially printed Magic card names
            """;

        var respJson = await _anthropic.SendAsync(new AnthropicRequest(
            ModelId,
            MaxTokens: 800,
            Messages: [new { role = "user", content = prompt }])
        {
            Operation = "mana-tune",
        });

        var raw = AnthropicResponse.DeserializeJson<RawFineTune>(respJson) ?? new RawFineTune();

        // `= []` initializers only cover absent properties — an explicit "landSuggestions":
        // null in the reply overwrites them with null and would NRE in grounding.
        return new ManaFineTuneDto
        {
            Advice = raw.Advice ?? [],
            LandSuggestions = await GroundSuggestionsAsync(raw.LandSuggestions ?? [], req),
        };
    }

    /// <summary>
    /// Resolves each model-proposed land against the card database, so a suggestion is a
    /// real card the client can add directly (newest printing preselected). Hallucinated
    /// names and cards already in the deck are dropped rather than shown as dead text.
    /// </summary>
    /// <remarks>Internal for tests: the model call above it is faked away there.</remarks>
    internal async Task<ManaLandSuggestion[]> GroundSuggestionsAsync(
        RawLandSuggestion[] proposed, ManaFineTuneRequest req)
    {
        var inDeck = new HashSet<string>(
            req.DeckCardNames.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        var seenOracleIds = new HashSet<string>(StringComparer.Ordinal);
        var grounded = new List<ManaLandSuggestion>(proposed.Length);

        foreach (var l in proposed)
        {
            var name = l.Name?.Trim() ?? string.Empty;
            if (name.Length == 0 || inDeck.Contains(name))
                continue;

            var def = await _cards.GetByNameAsync(name);
            if (def is null)
            {
                _logger.LogInformation("Mana-tune suggestion dropped, unknown card: {Name}", name);
                continue;
            }
            // The section claims "Suggested Lands" — hold the model to it.
            if (!def.IsLand)
            {
                _logger.LogInformation("Mana-tune suggestion dropped, not a land: {Name}", def.Name);
                continue;
            }
            // Re-check under the canonical name: the model may cite one face of an MDFC
            // the deck stores as "Front // Back", or an alternate spelling.
            if (inDeck.Contains(def.Name.Trim()))
                continue;
            // The same card proposed twice (or via two resolvable spellings) shows once.
            if (!seenOracleIds.Add(def.OracleId))
                continue;

            var printings = await _cards.GetPrintingsAsync(def.OracleId);
            grounded.Add(new ManaLandSuggestion
            {
                Name = def.Name,
                Reason = l.Reason,
                ScryfallId = printings.FirstOrDefault()?.ScryfallId,
                Card = DomainMapper.ToDto(def),
            });
        }

        if (grounded.Count < proposed.Length)
            _logger.LogInformation(
                "Mana-tune grounding kept {Kept}/{Proposed} land suggestions",
                grounded.Count, proposed.Length);

        return [.. grounded];
    }

    private sealed class RawFineTune
    {
        [JsonPropertyName("advice")] public string[] Advice { get; set; } = [];
        [JsonPropertyName("landSuggestions")] public RawLandSuggestion[] LandSuggestions { get; set; } = [];
    }

    internal sealed class RawLandSuggestion
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    }
}
