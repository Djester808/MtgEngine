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
        var raw = await CallAnthropicAsync(request);

        var latestSet       = await ResolveAsync(raw.LatestSet,       request.DeckCardNames);
        var topSynergy      = await ResolveAsync(raw.TopSynergy,      request.DeckCardNames);
        var gameChangers    = await ResolveAsync(raw.GameChangers,     request.DeckCardNames);
        var notableMentions = await ResolveAsync(raw.NotableMentions,  request.DeckCardNames);

        return new DeckSuggestionsDto
        {
            LatestSet       = latestSet,
            TopSynergy      = topSynergy,
            GameChangers    = gameChangers,
            NotableMentions = notableMentions,
        };
    }

    // ---- LLM call ---------------------------------------------------

    private async Task<RawSuggestions> CallAnthropicAsync(DeckSuggestionsRequest req)
    {
        var deckContext = req.DeckCardNames.Length > 0
            ? $"\n\nCards already in the deck ({req.DeckCardNames.Length}):\n{string.Join(", ", req.DeckCardNames)}"
            : string.Empty;

        var prompt = $$"""
            You are a Magic: The Gathering Commander/EDH expert.

            Commander: {{req.CommanderName}}
            Oracle text: {{req.CommanderText}}{{deckContext}}

            Suggest cards NOT already in the deck that would improve it. Use only real, official Magic card names (exact spelling).

            Respond with ONLY this exact JSON (no markdown, no extra text):
            {
              "latestSet": [{"name": "...", "reason": "..."}, ...],
              "topSynergy": [{"name": "...", "reason": "..."}, ...],
              "gameChangers": [{"name": "...", "reason": "..."}, ...],
              "notableMentions": [{"name": "...", "reason": "..."}, ...]
            }

            Rules:
            - latestSet: exactly 4 cards from Magic sets released in 2024 or 2025 that fit this strategy
            - topSynergy: exactly 6 cards with the strongest synergy with this specific commander
            - gameChangers: exactly 4 high-impact cards that define games or close out wins for this strategy
            - notableMentions: exactly 4 solid staples or support cards worth including

            Do not repeat cards between categories. Do not suggest cards already in the deck.
            """;

        var body = new
        {
            model      = ModelId,
            max_tokens = 1500,
            messages   = new[] { new { role = "user", content = prompt } },
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

        text = text.Trim();
        if (text.StartsWith("```")) text = text[(text.IndexOf('\n') + 1)..];
        if (text.EndsWith("```"))  text = text[..text.LastIndexOf("```")].TrimEnd();

        return JsonSerializer.Deserialize<RawSuggestions>(text,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new RawSuggestions();
    }

    // ---- Card resolution --------------------------------------------

    private async Task<SuggestedCardDto[]> ResolveAsync(
        RawCard[] rawCards, string[] deckCardNames)
    {
        var deckSet = new HashSet<string>(deckCardNames, StringComparer.OrdinalIgnoreCase);
        var tasks   = rawCards
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !deckSet.Contains(r.Name))
            .Select(r => ResolveOneAsync(r));
        return await Task.WhenAll(tasks);
    }

    private async Task<SuggestedCardDto> ResolveOneAsync(RawCard raw)
    {
        try
        {
            var def = await _scryfall.GetByNameAsync(raw.Name);
            if (def is null)
                return new SuggestedCardDto { Name = raw.Name, Reason = raw.Reason };

            var printings  = await _scryfall.GetPrintingsAsync(def.OracleId);
            var scryfallId = printings.FirstOrDefault()?.ScryfallId;

            return new SuggestedCardDto
            {
                Name       = raw.Name,
                Reason     = raw.Reason,
                ScryfallId = scryfallId,
                Card       = MapToCardDto(def),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve suggestion: {Name}", raw.Name);
            return new SuggestedCardDto { Name = raw.Name, Reason = raw.Reason };
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
    }
}
