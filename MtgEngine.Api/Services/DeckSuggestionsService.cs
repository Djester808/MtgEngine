using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MtgEngine.Api.Dtos;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

public interface IDeckSuggestionsService
{
    Task<DeckSuggestionsDto> GetSuggestionsAsync(DeckSuggestionsRequest request);
}

public sealed class DeckSuggestionsService : IDeckSuggestionsService
{
    private readonly IScryfallService _scryfall;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IAiCacheService _cache;
    private readonly string _apiKey;
    private readonly ILogger<DeckSuggestionsService> _logger;

    private const string ModelId = "claude-haiku-4-5-20251001";

    /// <summary>Bump when the model or the prompt changes, to invalidate cached responses.</summary>
    // v2: gameChangers grounded in the official Scryfall list.
    // v3: response carries rejection diagnostics; v2 payloads would deserialise with
    //     an empty Diagnostics block and misreport nothing as having been rejected.
    private const string CacheVersion = "claude-haiku-4-5-20251001-suggestions-v3";

    public DeckSuggestionsService(
        IScryfallService scryfall,
        IHttpClientFactory httpFactory,
        IAiCacheService cache,
        IConfiguration config,
        ILogger<DeckSuggestionsService> logger)
    {
        _scryfall = scryfall;
        _httpFactory = httpFactory;
        _cache = cache;
        _apiKey = SecretConfig.AnthropicApiKey(config);
        _logger = logger;
    }

    public Task<DeckSuggestionsDto> GetSuggestionsAsync(DeckSuggestionsRequest request)
    {
        // Deck contents and tags are sorted so that merely reordering cards -- which
        // does not change the request semantically -- still hits the cache.
        var keyParts = new[] { request.CommanderOracleId, request.CommanderName }
            .Concat(request.DeckCardNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            .Append("|tags|")
            .Concat(request.DeckTags.Concat(request.SuggestionTags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase));

        return _cache.GetOrCreateAsync(
            "suggestions", CacheVersion, keyParts, () => BuildSuggestionsAsync(request));
    }

    private async Task<DeckSuggestionsDto> BuildSuggestionsAsync(DeckSuggestionsRequest request)
    {
        var cmdDef = await _scryfall.GetByOracleIdAsync(request.CommanderOracleId);
        var cmdColors = cmdDef?.ColorIdentity.ToHashSet() ?? new HashSet<ManaColor>();

        var recentSets = await _scryfall.GetRecentSetCodesAsync(6);
        var allRecentNames = await _scryfall.GetRecentCardNamesAsync(recentSets, cmdColors);

        // The data layer returns every match; cap the prompt here. Seeded on the
        // commander so repeat requests produce an identical, cacheable prompt.
        var recentCardNames = DeterministicSample.Take(
            allRecentNames, 80, request.CommanderOracleId);

        // "Game Changer" is an official Scryfall-flagged list, not a vibe. ResolveAsync
        // rejects anything not on it, so the model must choose from the real list --
        // otherwise the category silently comes back empty.
        var gameChangerNames = await _scryfall.GetGameChangerNamesAsync(cmdColors);

        var raw = await CallAnthropicAsync(request, recentCardNames, gameChangerNames);

        var (latestSet, rejLatest) = await ResolveAsync(raw.LatestSet, request.DeckCardNames, cmdColors, recentSets, requireGameChanger: false);
        var (topSynergy, rejSynergy) = await ResolveAsync(raw.TopSynergy, request.DeckCardNames, cmdColors, null, requireGameChanger: false);
        var (gameChangers, rejGc) = await ResolveAsync(raw.GameChangers, request.DeckCardNames, cmdColors, null, requireGameChanger: true);
        var (notableMentions, rejNotable) = await ResolveAsync(raw.NotableMentions, request.DeckCardNames, cmdColors, null, requireGameChanger: false);

        var rejections = new List<string>();
        rejections.AddRange(rejLatest);
        rejections.AddRange(rejSynergy);
        rejections.AddRange(rejGc);
        rejections.AddRange(rejNotable);

        int proposed = raw.LatestSet.Length + raw.TopSynergy.Length
                     + raw.GameChangers.Length + raw.NotableMentions.Length;

        // Deduplicate across all categories: each card appears in at most one section
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SuggestedCardDto[] Dedup(SuggestedCardDto[] cards)
        {
            var kept = cards.Where(c => seen.Add(c.Name)).ToArray();
            for (int i = kept.Length; i < cards.Length; i++)
                rejections.Add(Rejection.DuplicateAcrossCategories);
            return kept;
        }

        latestSet = Dedup(latestSet);
        topSynergy = Dedup(topSynergy);
        gameChangers = Dedup(gameChangers);
        notableMentions = Dedup(notableMentions);

        int accepted = latestSet.Length + topSynergy.Length
                     + gameChangers.Length + notableMentions.Length;

        var byReason = rejections
            .GroupBy(r => r)
            .ToDictionary(g => g.Key, g => g.Count());

        if (byReason.Count > 0)
        {
            _logger.LogInformation(
                "Suggestions for {Commander}: {Accepted}/{Proposed} accepted; rejected {Rejected}",
                request.CommanderName, accepted, proposed,
                string.Join(", ", byReason.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        // A category that the model filled but validation emptied is a defect, not a
        // quiet no-op -- surface it loudly. This is exactly how gameChangers silently
        // returned nothing on every request.
        WarnIfFullyRejected(nameof(raw.LatestSet), raw.LatestSet.Length, latestSet.Length);
        WarnIfFullyRejected(nameof(raw.TopSynergy), raw.TopSynergy.Length, topSynergy.Length);
        WarnIfFullyRejected(nameof(raw.GameChangers), raw.GameChangers.Length, gameChangers.Length);
        WarnIfFullyRejected(nameof(raw.NotableMentions), raw.NotableMentions.Length, notableMentions.Length);

        return new DeckSuggestionsDto
        {
            LatestSet = latestSet,
            TopSynergy = topSynergy,
            GameChangers = gameChangers,
            NotableMentions = notableMentions,
            Diagnostics = new SuggestionDiagnosticsDto
            {
                Proposed = proposed,
                Accepted = accepted,
                Rejected = byReason,
            },
        };
    }

    // ---- LLM call ---------------------------------------------------

    private async Task<RawSuggestions> CallAnthropicAsync(
        DeckSuggestionsRequest req, string[] recentCardNames, string[] gameChangerNames)
    {
        var deckContext = req.DeckCardNames.Length > 0
            ? $"\n\nCards already in the deck ({req.DeckCardNames.Length}):\n{string.Join(", ", req.DeckCardNames)}"
            : string.Empty;

        var allTags = req.DeckTags.Concat(req.SuggestionTags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var tagsContext = allTags.Length > 0
            ? $"\n\nDeck style / focus tags: {string.Join(", ", allTags)}\nLet these tags strongly guide your suggestions (e.g. 'budget' → prefer affordable cards; 'combo' → lean into synergistic combos)."
            : string.Empty;

        var recentContext = recentCardNames.Length > 0
            ? $"\n\nRecent cards available for the latestSet category (choose the best 4 from this list):\n{string.Join(", ", recentCardNames)}"
            : string.Empty;

        var gameChangerContext = gameChangerNames.Length > 0
            ? $"\n\nOfficial Game Changer cards legal in this commander's colour identity " +
              $"(the gameChangers category MUST be chosen from this exact list — any other card will be rejected):\n" +
              string.Join(", ", gameChangerNames)
            : string.Empty;

        var prompt = $$"""
            You are a Magic: The Gathering Commander/EDH expert.

            Commander: {{req.CommanderName}}
            Oracle text: {{req.CommanderText}}{{deckContext}}{{tagsContext}}{{recentContext}}{{gameChangerContext}}

            Suggest cards NOT already in the deck that would improve it. Use only real, official Magic card names (exact spelling).
            Only suggest cards that are legal in the commander's color identity.

            Respond with ONLY this exact JSON (no markdown, no extra text):
            {
              "latestSet": [{"name": "...", "reason": "...", "score": 85}, ...],
              "topSynergy": [{"name": "...", "reason": "...", "score": 85}, ...],
              "gameChangers": [{"name": "...", "reason": "...", "score": 85}, ...],
              "notableMentions": [{"name": "...", "reason": "...", "score": 85}, ...]
            }

            Rules:
            - latestSet: exactly 4 cards chosen from the "Recent cards available" list above that best fit this strategy (MUST use names exactly as given)
            - topSynergy: exactly 6 cards with the strongest synergy with this specific commander
            - gameChangers: exactly 4 cards taken verbatim from the "Official Game Changer cards" list above, picking those that best fit this strategy. Do not invent entries for this category — cards outside that list are discarded. If the list has fewer than 4 entries, return all of them.
            - notableMentions: exactly 4 solid staples or support cards worth including
            - score: 0-100 compatibility percentage with this commander and existing deck

            Do not repeat cards between categories. Do not suggest cards already in the deck.
            """;

        var body = new
        {
            model = ModelId,
            max_tokens = 1500,
            temperature = 0,
            messages = new[] { new { role = "user", content = prompt } },
        };

        var http = _httpFactory.CreateClient("AnthropicApi");
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        httpReq.Headers.Add("x-api-key", _apiKey);
        httpReq.Headers.Add("anthropic-version", "2023-06-01");

        var resp = await http.SendAsync(httpReq);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogError("Anthropic suggestions {Status}: {Body}", resp.StatusCode, err);
            throw new HttpRequestException($"{resp.StatusCode}: {err}");
        }

        var respJson = await resp.Content.ReadAsStringAsync();
        return AnthropicResponse.DeserializeJson<RawSuggestions>(respJson) ?? new RawSuggestions();
    }

    // ---- Card resolution --------------------------------------------

    private void WarnIfFullyRejected(string category, int proposed, int accepted)
    {
        if (proposed > 0 && accepted == 0)
        {
            _logger.LogWarning(
                "Suggestion category '{Category}' rejected all {Proposed} proposals — " +
                "prompt and validation rules are likely out of sync",
                category, proposed);
        }
    }

    /// <summary>Why a proposed card did not make it into the response.</summary>
    private static class Rejection
    {
        public const string AlreadyInDeck = "already-in-deck";
        public const string BlankName = "blank-name";
        public const string UnknownCard = "unknown-card";
        public const string ColorIdentity = "color-identity";
        public const string NotGameChanger = "not-a-game-changer";
        public const string NotRecentPrinting = "no-recent-printing";
        public const string DuplicateAcrossCategories = "duplicate-across-categories";
        public const string LookupFailed = "lookup-failed";
    }

    private sealed record Resolution(SuggestedCardDto? Card, string? RejectedBecause);

    private async Task<(SuggestedCardDto[] Cards, List<string> Rejections)> ResolveAsync(
        RawCard[] rawCards,
        string[] deckCardNames,
        HashSet<ManaColor> cmdColors,
        IReadOnlySet<string>? recentSets,
        bool requireGameChanger)
    {
        var deckSet = new HashSet<string>(deckCardNames, StringComparer.OrdinalIgnoreCase);
        var rejections = new List<string>();

        var candidates = new List<RawCard>();
        foreach (var r in rawCards)
        {
            if (string.IsNullOrWhiteSpace(r.Name)) rejections.Add(Rejection.BlankName);
            else if (deckSet.Contains(r.Name)) rejections.Add(Rejection.AlreadyInDeck);
            else candidates.Add(r);
        }

        var results = await Task.WhenAll(candidates.Select(r =>
            ResolveOneAsync(r, cmdColors, recentSets, requireGameChanger)));

        var cards = new List<SuggestedCardDto>();
        foreach (var res in results)
        {
            if (res.Card is not null) cards.Add(res.Card);
            else if (res.RejectedBecause is not null) rejections.Add(res.RejectedBecause);
        }

        return (cards.ToArray(), rejections);
    }

    private async Task<Resolution> ResolveOneAsync(
        RawCard raw,
        HashSet<ManaColor> cmdColors,
        IReadOnlySet<string>? recentSets,
        bool requireGameChanger)
    {
        try
        {
            var def = await _scryfall.GetByNameAsync(raw.Name);
            if (def is null)
            {
                // Outside the strict category, an unresolved name is still shown (the
                // model may know a card the local bulk data does not) -- but it is
                // counted, so a spike in hallucinated names is visible.
                return requireGameChanger
                    ? new Resolution(null, Rejection.UnknownCard)
                    : new Resolution(
                        new SuggestedCardDto { Name = raw.Name, Reason = raw.Reason, Score = raw.Score },
                        Rejection.UnknownCard);
            }

            // Color identity check — filter cards that exceed the commander's color identity
            if (cmdColors.Count > 0)
            {
                bool isLegal = def.ColorIdentity.All(c => c == ManaColor.Colorless || cmdColors.Contains(c));
                if (!isLegal)
                    return new Resolution(null, Rejection.ColorIdentity);
            }

            // Game Changer check — only official GC-designated cards allowed in this category
            if (requireGameChanger && !def.GameChanger)
                return new Resolution(null, Rejection.NotGameChanger);

            var printings = await _scryfall.GetPrintingsAsync(def.OracleId);

            // Recent-set check (latestSet category only)
            if (recentSets is { Count: > 0 })
            {
                bool hasRecentPrinting = printings.Any(p => p.SetCode is not null && recentSets.Contains(p.SetCode));
                if (!hasRecentPrinting)
                    return new Resolution(null, Rejection.NotRecentPrinting);
            }

            var scryfallId = printings.FirstOrDefault()?.ScryfallId;

            return new Resolution(new SuggestedCardDto
            {
                Name = raw.Name,
                Reason = raw.Reason,
                Score = raw.Score,
                ScryfallId = scryfallId,
                Card = MapToCardDto(def),
            }, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve suggestion: {Name}", raw.Name);
            return new Resolution(
                new SuggestedCardDto { Name = raw.Name, Reason = raw.Reason, Score = raw.Score },
                Rejection.LookupFailed);
        }
    }

    // ---- Mapping ----------------------------------------------------

    private static CardDto MapToCardDto(CardDefinition def) => new()
    {
        CardId = def.OracleId,
        OracleId = def.OracleId,
        Name = def.Name,
        ManaCost = string.IsNullOrEmpty(def.ManaCostRaw) ? def.ManaCost.ToString() : def.ManaCostRaw,
        ManaValue = def.Cmc,
        CardTypes = def.CardTypes.ToString().Split(", ")
                                .Where(t => Enum.IsDefined(typeof(CardTypeDto), t))
                                .Select(t => Enum.Parse<CardTypeDto>(t))
                                .ToArray(),
        Subtypes = [.. def.Subtypes],
        Supertypes = [.. def.Supertypes],
        OracleText = def.OracleText,
        Power = def.Power,
        Toughness = def.Toughness,
        StartingLoyalty = def.StartingLoyalty,
        Keywords = def.Keywords.ToString().Split(", ")
                                .Where(k => !string.IsNullOrEmpty(k) && k != "None")
                                .ToArray(),
        ImageUriNormal = def.ImageUriNormal,
        ImageUriNormalBack = def.ImageUriNormalBack,
        ImageUriSmall = def.ImageUriSmall,
        ImageUriArtCrop = def.ImageUriArtCrop,
        ColorIdentity = def.ColorIdentity
                                .Select(c => c switch
                                {
                                    ManaColor.White => ManaColorDto.W,
                                    ManaColor.Blue => ManaColorDto.U,
                                    ManaColor.Black => ManaColorDto.B,
                                    ManaColor.Red => ManaColorDto.R,
                                    ManaColor.Green => ManaColorDto.G,
                                    _ => ManaColorDto.C,
                                })
                                .ToArray(),
        FlavorText = def.FlavorText,
        Artist = def.Artist,
        SetCode = def.SetCode,
        Rarity = def.Rarity,
        Legalities = def.Legalities.ToDictionary(kv => kv.Key, kv => kv.Value),
        GameChanger = def.GameChanger,
    };

    // ---- Raw JSON shapes --------------------------------------------

    private sealed class RawSuggestions
    {
        [JsonPropertyName("latestSet")] public RawCard[] LatestSet { get; set; } = [];
        [JsonPropertyName("topSynergy")] public RawCard[] TopSynergy { get; set; } = [];
        [JsonPropertyName("gameChangers")] public RawCard[] GameChangers { get; set; } = [];
        [JsonPropertyName("notableMentions")] public RawCard[] NotableMentions { get; set; } = [];
    }

    private sealed class RawCard
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
        [JsonPropertyName("score")] public int Score { get; set; }
    }
}
