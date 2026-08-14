using System.Text.Json;
using System.Text.RegularExpressions;

namespace MtgEngine.Api.Services;

/// <summary>
/// Outcome of identifying a card from a photo.
/// </summary>
/// <remarks>
/// <see cref="Error"/> covers only recoverable, request-level outcomes -- the image was
/// processed but no card could be read from it. Provider failures throw
/// <see cref="AiUpstreamException"/> and surface as 502, matching the other AI endpoints.
/// </remarks>
/// <remarks>
/// <see cref="SetName"/> and <see cref="SetCode"/> are only ever populated from
/// Scryfall, never from the model. The model's set guess is treated purely as a
/// hint: it is read from a small printed symbol and is frequently wrong, so it is
/// discarded unless it matches a real printing of the identified card.
/// </remarks>
public sealed record CardVisionResult
{
    /// <summary>Scryfall's canonical name when resolved, otherwise the model's raw reading.</summary>
    public string? CardName { get; init; }
    public string? CollectorNumber { get; init; }

    /// <summary>Verified set name, or null when the printing could not be determined.</summary>
    public string? SetName { get; init; }
    public string? SetCode { get; init; }

    public string? OracleId { get; init; }
    public string? ScryfallId { get; init; }

    /// <summary>True when the card name resolved against Scryfall.</summary>
    public bool Verified { get; init; }

    public string? Error { get; init; }
}

public sealed class CardVisionService
{
    private readonly IAnthropicClient _anthropic;
    private readonly ICardLookup _scryfall;
    private readonly ILogger<CardVisionService> _logger;

    private const string ModelId = "claude-sonnet-4-6";

    public CardVisionService(
        IAnthropicClient anthropic,
        ICardLookup scryfall,
        ILogger<CardVisionService> logger)
    {
        _anthropic = anthropic;
        _scryfall = scryfall;
        _logger = logger;
    }

    public async Task<CardVisionResult> IdentifyCardAsync(
        string imageBase64, string mediaType)
    {
        // This is transcription, not generation, so sampling stays pinned at the client's
        // default temperature of 0: repeated scans of the same card must not drift.
        var request = new AnthropicRequest(
            ModelId,
            MaxTokens: 128,
            Messages:
            [
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "image",
                            source = new
                            {
                                type = "base64",
                                media_type = mediaType,
                                data = imageBase64,
                            },
                        },
                        new
                        {
                            type = "text",
                            text = """
                                Someone is holding a Magic: The Gathering card up to the camera.
                                Read the following from the card and reply with JSON only — no markdown, no explanation:

                                1. "cardName": The card name in the large bold font at the very top of the card. Spell it exactly as printed, including apostrophes.
                                2. "collectorNumber": The number at the bottom-left of the card, like "105/280" or "105". Extract only the digits before the slash. null if unreadable.
                                3. "setName": Identify the Magic: The Gathering set this card is from by recognising the set symbol icon printed in the middle-right area of the card (between the art and the text box). Return the full English set name, e.g. "Guilds of Ravnica", "Ikoria: Lair of Behemoths", "Dominaria United". Return null unless you can actually make out the symbol — a wrong set name is worse than no set name, and do not infer the set from the card name.

                                If you cannot read the card name at all, return: {"cardName":null,"collectorNumber":null,"setName":null}

                                Example valid response: {"cardName":"Healer's Hawk","collectorNumber":"11","setName":"Guilds of Ravnica"}
                                """,
                        },
                    },
                },
            ])
        {
            Operation = "vision",
        };

        // A provider failure throws out of SendAsync and surfaces as a 502, same as every
        // other AI endpoint. Only "the image was fine but no card was readable" gets past
        // here, and that stays a 200 with Error set.
        var rawBody = await _anthropic.SendAsync(request);

        var text = AnthropicResponse.ExtractText(rawBody).Trim();

        _logger.LogInformation("Claude vision response: {Text}", text);

        if (string.IsNullOrEmpty(text))
            return new CardVisionResult();

        // Salvage the JSON object from markdown fences or surrounding prose.
        var json = AnthropicResponse.ExtractJsonObject(text);

        string? rawName, rawCollector, rawSet;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;

            rawName = ReadString(root, "cardName");

            rawCollector = ReadString(root, "collectorNumber");
            if (rawCollector is not null)
                rawCollector = Regex.Match(rawCollector, @"^\d+").Value;
            if (string.IsNullOrEmpty(rawCollector))
                rawCollector = null;

            rawSet = ReadString(root, "setName");
        }
        catch (JsonException)
        {
            _logger.LogWarning("Could not parse JSON from Claude vision: {Text}", text);
            return new CardVisionResult { Error = "Could not parse Claude response" };
        }

        return await VerifyAsync(rawName, rawCollector, rawSet);
    }

    /// <summary>
    /// Resolves the model's reading against Scryfall. The card name is the only field
    /// the model reads as plain text, so it is the anchor; the set is re-derived from
    /// card data rather than trusted.
    /// </summary>
    private async Task<CardVisionResult> VerifyAsync(string? rawName, string? rawCollector, string? rawSet)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return new CardVisionResult { CollectorNumber = rawCollector };

        var def = await _scryfall.GetByNameAsync(rawName);
        if (def is null)
        {
            // Name did not resolve, so the set guess cannot be corroborated -- drop it
            // rather than pass an unverifiable value to the client.
            _logger.LogInformation("Vision: '{Name}' did not resolve against Scryfall", rawName);
            return new CardVisionResult
            {
                CardName = rawName,
                CollectorNumber = rawCollector,
                Verified = false,
            };
        }

        var printings = await _scryfall.GetPrintingsAsync(def.OracleId);

        // Prefer the model's set, but only as a hint that must match a real printing.
        var chosen = rawSet is null
            ? null
            : printings.FirstOrDefault(p =>
                string.Equals(p.SetName, rawSet, StringComparison.OrdinalIgnoreCase));

        if (chosen is null && rawCollector is not null)
        {
            // Collector numbers are read as printed text (more reliable than the set
            // symbol) but repeat across sets, so only accept an unambiguous match.
            var wanted = NormalizeCollectorNumber(rawCollector);
            var byNumber = printings
                .Where(p => NormalizeCollectorNumber(p.CollectorNumber) == wanted)
                .ToArray();
            if (byNumber.Length == 1)
                chosen = byNumber[0];
        }

        if (chosen is null && rawSet is not null)
        {
            _logger.LogInformation(
                "Vision: discarding unverifiable set '{Set}' for '{Card}'", rawSet, def.Name);
        }

        return new CardVisionResult
        {
            CardName = def.Name,                    // canonical spelling, not the model's
            CollectorNumber = chosen?.CollectorNumber ?? rawCollector,
            SetName = chosen?.SetName,              // null when the printing is unknown
            SetCode = chosen?.SetCode,
            OracleId = def.OracleId,
            // Fall back to any printing so the client still has an image to show.
            ScryfallId = chosen?.ScryfallId ?? printings.FirstOrDefault()?.ScryfallId,
            Verified = true,
        };
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? NullIfBlank(el.GetString()?.Trim())
            : null;

    private static string? NullIfBlank(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>
    /// Reduces a collector number to its comparable numeric core.
    /// </summary>
    /// <remarks>
    /// The two sides are written differently: the model transcribes what is printed on
    /// the card (often zero-padded, e.g. "0082") while Scryfall stores the canonical
    /// form ("82"), and printings carry suffixes such as "82a" or "★82". Comparing raw
    /// strings silently never matches.
    /// </remarks>
    internal static string NormalizeCollectorNumber(string? collectorNumber)
    {
        if (string.IsNullOrWhiteSpace(collectorNumber))
            return string.Empty;

        var digits = Regex.Match(collectorNumber, @"\d+").Value;
        if (digits.Length == 0)
            return string.Empty;

        var trimmed = digits.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }
}
