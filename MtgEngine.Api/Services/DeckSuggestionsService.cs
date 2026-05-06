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
    private readonly string _apiKey;
    private readonly ILogger<DeckSuggestionsService> _logger;

    private const string ModelId = "claude-haiku-4-5-20251001";

    public DeckSuggestionsService(
        IScryfallService scryfall,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<DeckSuggestionsService> logger)
    {
        _scryfall    = scryfall;
        _httpFactory = httpFactory;
        _apiKey      = config["Anthropic:ApiKey"] ?? throw new InvalidOperationException("Anthropic:ApiKey not configured");
        _logger      = logger;
    }

    public async Task<DeckSuggestionsDto> GetSuggestionsAsync(DeckSuggestionsRequest request)
    {
        var cmdDef    = await _scryfall.GetByOracleIdAsync(request.CommanderOracleId);
        var cmdColors = cmdDef?.ColorIdentity.ToHashSet() ?? new HashSet<ManaColor>();

        var recentSets     = await _scryfall.GetRecentSetCodesAsync(6);
        var recentCardNames = await _scryfall.GetRecentCardNamesAsync(recentSets, cmdColors);

        var raw = await CallAnthropicAsync(request, recentCardNames);

        var latestSet       = await ResolveAsync(raw.LatestSet,       request.DeckCardNames, cmdColors, recentSets,  requireGameChanger: false);
        var topSynergy      = await ResolveAsync(raw.TopSynergy,      request.DeckCardNames, cmdColors, null,        requireGameChanger: false);
        var gameChangers    = await ResolveAsync(raw.GameChangers,     request.DeckCardNames, cmdColors, null,        requireGameChanger: true);
        var notableMentions = await ResolveAsync(raw.NotableMentions,  request.DeckCardNames, cmdColors, null,        requireGameChanger: false);

        // Deduplicate across all categories: each card appears in at most one section
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SuggestedCardDto[] Dedup(SuggestedCardDto[] cards) =>
            cards.Where(c => seen.Add(c.Name)).ToArray();

        latestSet       = Dedup(latestSet);
        topSynergy      = Dedup(topSynergy);
        gameChangers    = Dedup(gameChangers);
        notableMentions = Dedup(notableMentions);

        return new DeckSuggestionsDto
        {
            LatestSet       = latestSet,
            TopSynergy      = topSynergy,
            GameChangers    = gameChangers,
            NotableMentions = notableMentions,
        };
    }

    // ---- LLM call ---------------------------------------------------

    private async Task<RawSuggestions> CallAnthropicAsync(DeckSuggestionsRequest req, string[] recentCardNames)
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

        var prompt = $$"""
            You are a Magic: The Gathering Commander/EDH expert.

            Commander: {{req.CommanderName}}
            Oracle text: {{req.CommanderText}}{{deckContext}}{{tagsContext}}{{recentContext}}

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
            - gameChangers: exactly 4 high-impact cards that define games or close out wins for this strategy
            - notableMentions: exactly 4 solid staples or support cards worth including
            - score: 0-100 compatibility percentage with this commander and existing deck

            Do not repeat cards between categories. Do not suggest cards already in the deck.
            """;

        var body = new
        {
            model       = ModelId,
            max_tokens  = 1500,
            temperature = 0,
            messages    = new[] { new { role = "user", content = prompt } },
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
        var doc      = JsonDocument.Parse(respJson);
        var text     = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? "{}";

        text = ExtractJsonObject(text);

        return JsonSerializer.Deserialize<RawSuggestions>(text,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new RawSuggestions();
    }

    // ---- Card resolution --------------------------------------------

    private async Task<SuggestedCardDto[]> ResolveAsync(
        RawCard[] rawCards,
        string[] deckCardNames,
        HashSet<ManaColor> cmdColors,
        IReadOnlySet<string>? recentSets,
        bool requireGameChanger)
    {
        var deckSet = new HashSet<string>(deckCardNames, StringComparer.OrdinalIgnoreCase);
        var tasks   = rawCards
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !deckSet.Contains(r.Name))
            .Select(r => ResolveOneAsync(r, cmdColors, recentSets, requireGameChanger));
        var results = await Task.WhenAll(tasks);
        return results.Where(r => r is not null).Select(r => r!).ToArray();
    }

    private async Task<SuggestedCardDto?> ResolveOneAsync(
        RawCard raw,
        HashSet<ManaColor> cmdColors,
        IReadOnlySet<string>? recentSets,
        bool requireGameChanger)
    {
        try
        {
            var def = await _scryfall.GetByNameAsync(raw.Name);
            if (def is null)
                return requireGameChanger ? null : new SuggestedCardDto { Name = raw.Name, Reason = raw.Reason, Score = raw.Score };

            // Color identity check — filter cards that exceed the commander's color identity
            if (cmdColors.Count > 0)
            {
                bool isLegal = def.ColorIdentity.All(c => c == ManaColor.Colorless || cmdColors.Contains(c));
                if (!isLegal) return null;
            }

            // Game Changer check — only official GC-designated cards allowed in this category
            if (requireGameChanger && !def.GameChanger) return null;

            var printings = await _scryfall.GetPrintingsAsync(def.OracleId);

            // Recent-set check (latestSet category only)
            if (recentSets is { Count: > 0 })
            {
                bool hasRecentPrinting = printings.Any(p => p.SetCode is not null && recentSets.Contains(p.SetCode));
                if (!hasRecentPrinting) return null;
            }

            var scryfallId = printings.FirstOrDefault()?.ScryfallId;

            return new SuggestedCardDto
            {
                Name       = raw.Name,
                Reason     = raw.Reason,
                Score      = raw.Score,
                ScryfallId = scryfallId,
                Card       = MapToCardDto(def),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve suggestion: {Name}", raw.Name);
            return new SuggestedCardDto { Name = raw.Name, Reason = raw.Reason, Score = raw.Score };
        }
    }

    // ---- Mapping ----------------------------------------------------

    private static CardDto MapToCardDto(CardDefinition def) => new()
    {
        CardId             = def.OracleId,
        OracleId           = def.OracleId,
        Name               = def.Name,
        ManaCost           = string.IsNullOrEmpty(def.ManaCostRaw) ? def.ManaCost.ToString() : def.ManaCostRaw,
        ManaValue          = def.Cmc,
        CardTypes          = def.CardTypes.ToString().Split(", ")
                                .Where(t => Enum.IsDefined(typeof(CardTypeDto), t))
                                .Select(t => Enum.Parse<CardTypeDto>(t))
                                .ToArray(),
        Subtypes           = [..def.Subtypes],
        Supertypes         = [..def.Supertypes],
        OracleText         = def.OracleText,
        Power              = def.Power,
        Toughness          = def.Toughness,
        StartingLoyalty    = def.StartingLoyalty,
        Keywords           = def.Keywords.ToString().Split(", ")
                                .Where(k => !string.IsNullOrEmpty(k) && k != "None")
                                .ToArray(),
        ImageUriNormal     = def.ImageUriNormal,
        ImageUriNormalBack = def.ImageUriNormalBack,
        ImageUriSmall      = def.ImageUriSmall,
        ImageUriArtCrop    = def.ImageUriArtCrop,
        ColorIdentity      = def.ColorIdentity
                                .Select(c => c switch
                                {
                                    ManaColor.White => ManaColorDto.W,
                                    ManaColor.Blue  => ManaColorDto.U,
                                    ManaColor.Black => ManaColorDto.B,
                                    ManaColor.Red   => ManaColorDto.R,
                                    ManaColor.Green => ManaColorDto.G,
                                    _               => ManaColorDto.C,
                                })
                                .ToArray(),
        FlavorText         = def.FlavorText,
        Artist             = def.Artist,
        SetCode            = def.SetCode,
        Rarity             = def.Rarity,
        Legalities         = def.Legalities.ToDictionary(kv => kv.Key, kv => kv.Value),
        GameChanger        = def.GameChanger,
    };

    // ---- Raw JSON shapes --------------------------------------------

    private sealed class RawSuggestions
    {
        [JsonPropertyName("latestSet")]       public RawCard[] LatestSet       { get; set; } = [];
        [JsonPropertyName("topSynergy")]      public RawCard[] TopSynergy      { get; set; } = [];
        [JsonPropertyName("gameChangers")]    public RawCard[] GameChangers    { get; set; } = [];
        [JsonPropertyName("notableMentions")] public RawCard[] NotableMentions { get; set; } = [];
    }

    private sealed class RawCard
    {
        [JsonPropertyName("name")]   public string Name   { get; set; } = string.Empty;
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
        [JsonPropertyName("score")]  public int    Score  { get; set; }
    }

    private static string ExtractJsonObject(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0) return "{}";
        int depth = 0; bool inString = false; bool escaped = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (escaped)              { escaped = false; continue; }
            if (c == '\\' && inString){ escaped = true;  continue; }
            if (c == '"')             { inString = !inString; continue; }
            if (inString)               continue;
            if (c == '{') depth++;
            else if (c == '}') { if (--depth == 0) return text[start..(i + 1)]; }
        }
        return text[start..];
    }
}
