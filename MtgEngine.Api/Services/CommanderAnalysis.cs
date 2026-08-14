using System.Text.Json.Serialization;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>
/// A numeric bar the commander's text sets, already normalised to an inclusive comparison —
/// "greater than 3" arrives here as 4-or-greater, whatever wording the card used.
/// </summary>
public readonly record struct Threshold(string Attribute, int Value, bool OrGreater)
{
    public string Describe() => $"{Attribute} {Value} or {(OrGreater ? "greater" : "less")}";

    public bool IsMetBy(int? actual) =>
        actual is int v && (OrGreater ? v >= Value : v <= Value);
}

/// <summary>What the commander asks of the deck, as structured data.</summary>
public sealed record CommanderRequirements(
    IReadOnlyList<Threshold> Thresholds,
    IReadOnlyList<string> Tribes,
    IReadOnlyList<string> NamedKeywords,
    IReadOnlyList<MtgEngine.Domain.Enums.ManaColor> Colours)
{
    public bool IsEmpty =>
        Thresholds.Count == 0 && Tribes.Count == 0 && NamedKeywords.Count == 0 && Colours.Count == 0;

    public static readonly CommanderRequirements None = new([], [], [], []);
}

/// <summary>
/// Reads a commander's rules text once and returns what it requires, as structured data.
/// </summary>
/// <remarks>
/// This exists to keep prose out of C#. Extracting "power 4 or greater" with a regex meant
/// enumerating phrasings, and the corpus does not cooperate: against 36k cards, patterns for
/// "or greater/less" caught ~992 printings while "or more/fewer" and "greater than" added
/// another ~180 that were missed <em>silently</em>. Every new phrasing would have been
/// another pattern, and every gap invisible.
/// <para>
/// So the split is: the model reads the sentence, which it is good at, and returns a number.
/// Code compares that number against card fields, which the model is bad at — asked to do
/// the comparison itself it called a 3/2 an enabler for a power-4 trigger. One cached call
/// per commander, then pure arithmetic for every card scored against it.
/// </para>
/// </remarks>
public interface ICommanderAnalysis
{
    Task<CommanderRequirements> AnalyseAsync(CardDefinition commander);
}

public sealed class CommanderAnalysis : ICommanderAnalysis
{
    private const string ModelId = "claude-haiku-4-5-20251001";
    private const string CacheVersion = "claude-haiku-4-5-20251001-commander-reqs-v1";

    private readonly IAiCacheService _cache;
    private readonly IAnthropicClient _anthropic;
    private readonly ILogger<CommanderAnalysis> _logger;

    public CommanderAnalysis(
        IAiCacheService cache,
        IAnthropicClient anthropic,
        ILogger<CommanderAnalysis> logger)
    {
        _cache = cache;
        _anthropic = anthropic;
        _logger = logger;
    }

    public async Task<CommanderRequirements> AnalyseAsync(CardDefinition commander)
    {
        var text = commander.OracleText ?? string.Empty;

        // A commander with no rules text asks nothing of the deck beyond its colours.
        if (string.IsNullOrWhiteSpace(text))
            return new CommanderRequirements([], [.. commander.Subtypes], [], commander.ColorIdentity);

        var extracted = await _cache.GetOrCreateAsync(
            "commander-reqs", CacheVersion, [commander.OracleId], () => CallAsync(commander, text));

        // `= []` initializers only cover ABSENT properties — an explicit `"key": null`
        // in the model's reply overwrites them with null, and this value is cached, so
        // one bad payload would otherwise re-throw on every later read.
        var extractedThresholds = extracted.Thresholds ?? [];
        var extractedTribes = extracted.Tribes ?? [];
        var extractedKeywords = extracted.Keywords ?? [];

        var thresholds = new List<Threshold>();
        foreach (var t in extractedThresholds)
        {
            if (!ValidAttributes.Contains(t.Attribute, StringComparer.OrdinalIgnoreCase))
            {
                // The comparison is done against a specific field, so an attribute we have
                // no field for cannot be checked and must not be invented.
                _logger.LogWarning(
                    "Commander {Commander}: ignoring requirement on unknown attribute '{Attribute}'",
                    commander.Name, t.Attribute);
                continue;
            }

            thresholds.Add(new Threshold(
                t.Attribute.ToLowerInvariant(), t.Value, t.OrGreater));
        }

        // The commander's own subtypes are a tribe by definition; anything the model spotted
        // in the text is added to them.
        var tribes = new List<string>(commander.Subtypes);
        foreach (var tribe in extractedTribes)
            if (!tribes.Contains(tribe, StringComparer.OrdinalIgnoreCase))
                tribes.Add(tribe);

        _logger.LogInformation(
            "Commander {Commander} requires: {Thresholds}; tribes {Tribes}; keywords {Keywords}",
            commander.Name,
            thresholds.Count > 0 ? string.Join(", ", thresholds.Select(t => t.Describe())) : "no numeric thresholds",
            tribes.Count > 0 ? string.Join("/", tribes) : "none",
            extractedKeywords.Length > 0 ? string.Join("/", extractedKeywords) : "none");

        return new CommanderRequirements(
            thresholds, tribes, extractedKeywords, commander.ColorIdentity);
    }

    /// <summary>Attributes a card actually has a field for, and can therefore be checked against.</summary>
    private static readonly string[] ValidAttributes = ["power", "toughness", "mana value"];

    private async Task<ExtractedJson> CallAsync(CardDefinition commander, string text)
    {
        var prompt = $$"""
            Read this Magic: The Gathering commander and report what its abilities require
            from the rest of the deck. Report only what the text actually says.

            Commander: {{commander.Name}}
            Type: {{CardFactSheet.TypeLineOf(commander.Supertypes, CardFactSheet.TypeNames(commander.CardTypes), commander.Subtypes)}}
            Oracle text:
            {{text}}

            Respond with ONLY valid JSON in exactly this shape (no markdown, no extra text):
            {"thresholds":[{"attribute":"power|toughness|mana value","value":<integer>,"orGreater":<true|false>}],
             "tribes":["<creature type>"],
             "keywords":["<keyword>"]}

            Rules:
            - thresholds: every numeric bar the text sets on a card's power, toughness or
              mana value, in ANY phrasing. "power 4 or greater", "power 4 or more" and
              "power greater than 3" are all the same requirement: value 4, orGreater true.
              "mana value 3 or less" is value 3, orGreater false. Normalise strictly-greater
              and strictly-less phrasings to the inclusive equivalent.
            - Only include a threshold the deck can satisfy by choosing cards. Ignore numbers
              that describe an amount of damage, life, counters, cards drawn, or tokens made.
            - tribes: creature types the text names as mattering. Not the commander's own
              types unless the text names them too. Singular form.
            - keywords: keyword abilities the text names, whether the commander has them or
              cares about them elsewhere.
            - Empty arrays where there is nothing to report. Do not invent requirements.
            """;

        var json = await _anthropic.SendAsync(new AnthropicRequest(
            ModelId,
            MaxTokens: 500,
            Messages: [new { role = "user", content = prompt }])
        {
            Operation = "commander analysis",
        });

        return AnthropicResponse.DeserializeJson<ExtractedJson>(json) ?? new ExtractedJson();
    }

    private sealed class ExtractedJson
    {
        [JsonPropertyName("thresholds")] public ThresholdJson[] Thresholds { get; set; } = [];
        [JsonPropertyName("tribes")] public string[] Tribes { get; set; } = [];
        [JsonPropertyName("keywords")] public string[] Keywords { get; set; } = [];
    }

    private sealed class ThresholdJson
    {
        [JsonPropertyName("attribute")] public string Attribute { get; set; } = string.Empty;
        [JsonPropertyName("value")] public int Value { get; set; }
        [JsonPropertyName("orGreater")] public bool OrGreater { get; set; }
    }
}
