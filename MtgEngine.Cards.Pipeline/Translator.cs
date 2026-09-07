using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace MtgEngine.Cards.Pipeline;

/// <summary>What one model said about one card: the raw text, the parsed definition if it parsed, and the usage.</summary>
public sealed record Reading(string OracleId, string Model, string Raw, CardDefinition? Definition, string? ParseError, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens);

/// <summary>
/// Turns oracle text into definitions through the Message Batches API. The vocabulary
/// reference is a cached system prefix; the card is the user message; the answer is JSON
/// the serializer must accept. Structured outputs cannot take a recursive schema, so the
/// constraint is the prompt and the check is ours.
/// </summary>
public sealed class Translator(AnthropicClient client, string promptVersion)
{
    public const string SystemPrompt = """
        You translate Magic: The Gathering cards into a closed JSON vocabulary that a rules engine executes.
        Output exactly one JSON object, a CardDefinition, and nothing else: no prose, no code fences.

        How to read a card:
        - One Face per printed face, in Scryfall order; role Main for the front, Back for a transform or modal back face, Adventure/Omen for those, Left/Right for split halves.
        - One Ability per line of rules text, in printed order. A line of comma-separated keywords is one KeywordAbility per keyword. Reminder text in parentheses is not an ability.
        - Set "text" on each ability to the printed line it came from. Set "oracleText" on each face to the face's full text.
        - Keyword abilities: use KeywordAbility with cost as printed ("{1}{G}"), n for a number, "of" as an ObjectFilter for "protection from red", "landwalk", "affinity for artifacts", "enchant creature".
        - "When/Whenever/At" is a TriggeredAbility; "[cost]: [effect]" is an ActivatedAbility; an instant or sorcery's text is one SpellAbility; "as long as", "get +1/+1", "can't" and cost changes are StaticAbility; "if ... would ... instead" and "as ... enters" are ReplacementAbility.
        - Selectors: Self for the card itself; Target for "target"; Each for "each"/"all"; Choose for "choose"; Attached for "equipped creature"/"enchanted creature"; Triggering for "it"/"that creature" inside a trigger; Top for "the top card of your library".
        - When a later step refers back to something, give the producing step a key ("key", "rememberAs", "storeAs") and refer to it with Remembered, RememberedPlayer, Stored, ChosenNumber or ChosenPlayer. Keys are lowercase words.
        - Amounts are records: {"kind":"Fixed","n":3}. X is {"kind":"X"}. Mana is printed text: "{2}{G}", "{X}{R}".
        - Colors, card types and supertypes are arrays of names: ["Green"], ["Creature"], ["Legendary"]. Subtypes are strings.
        - "You" is {"kind":"You"}; "each opponent" is {"kind":"EachOpponent"}; "target player" is TargetPlayer.
        - Tokens: a TokenSpec with name, colors, types, subtypes, power, toughness and abilities; copy the token's data exactly when it is given to you, including scryfallOracleId.
        - If any part of the text cannot be expressed in the vocabulary, put {"kind":"Unsupported","reason":"...","text":"..."} in that position. Never approximate.
        - Omit optional properties you do not need. Do not invent properties: only the parameters listed in the vocabulary exist.
        """;

    public static string SystemWithVocabulary() => SystemPrompt + "\n\n" + VocabularyReference.Text();

    public static string UserMessage(CorpusCard card)
    {
        var payload = new
        {
            name = card.Name,
            layout = card.Layout,
            keywords = card.Keywords,
            faces = card.Faces.Select(f => new { name = f.Name, type_line = f.TypeLine, mana_cost = f.ManaCost, oracle_text = f.OracleText, power = f.Power, toughness = f.Toughness, loyalty = f.Loyalty, defense = f.Defense }),
            tokens = card.Tokens.Select(t => new { scryfall_oracle_id = t.OracleId, name = t.Name, type_line = t.TypeLine, colors = t.Colors, oracle_text = t.OracleText, power = t.Power, toughness = t.Toughness }),
        };
        return "Translate this card. oracleId is \"" + card.OracleId + "\".\n" + JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static List<TextBlockParam> SystemBlockList(string system) =>
        [new() { Text = system, CacheControl = new CacheControlEphemeral() }];

    private static MessageCreateParamsSystem SystemBlocks(string system) => new(SystemBlockList(system));

    /// <summary>The batch endpoint has its own params type with the same shape.</summary>
    private static Anthropic.Models.Messages.Batches.Params ParamsFor(string model, CorpusCard card, string system) => new()
    {
        Model = model,
        MaxTokens = 16000,
        System = new Anthropic.Models.Messages.Batches.ParamsSystem(SystemBlockList(system)),
        Messages = [new() { Role = Role.User, Content = UserMessage(card) }],
    };

    /// <summary>Submit one batch per model for every card; returns the batch ids.</summary>
    public async Task<IReadOnlyDictionary<string, string>> SubmitAsync(IReadOnlyList<CorpusCard> cards, IReadOnlyList<string> models, CancellationToken ct)
    {
        var system = SystemWithVocabulary();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            var requests = new List<Anthropic.Models.Messages.Batches.Request>();
            foreach (var card in cards)
                requests.Add(new() { CustomID = card.OracleId, Params = ParamsFor(model, card, system) });
            var parameters = new Anthropic.Models.Messages.Batches.BatchCreateParams { Requests = requests };
            var batch = await client.Messages.Batches.Create(parameters, cancellationToken: ct);
            ids[model] = batch.ID;
            Console.WriteLine($"submitted {cards.Count} requests to {model}: batch {batch.ID}");
        }

        return ids;
    }

    /// <summary>Poll until every batch has ended, then collect the readings.</summary>
    public async Task<IReadOnlyList<Reading>> CollectAsync(IReadOnlyDictionary<string, string> batches, TimeSpan poll, CancellationToken ct)
    {
        var readings = new List<Reading>();
        foreach (var (model, batchId) in batches)
        {
            while (true)
            {
                var status = await client.Messages.Batches.Retrieve(batchId, cancellationToken: ct);
                var counts = status.RequestCounts;
                Console.WriteLine($"{model} {batchId}: {status.ProcessingStatus} succeeded={counts.Succeeded} errored={counts.Errored} processing={counts.Processing}");
                if (string.Equals(status.ProcessingStatus.ToString(), "ended", StringComparison.OrdinalIgnoreCase))
                    break;
                await Task.Delay(poll, ct);
            }

            await foreach (var item in client.Messages.Batches.ResultsStreaming(batchId, cancellationToken: ct))
                readings.Add(ToReading(model, item));
        }

        return readings;
    }

    private static Reading ToReading(string model, Anthropic.Models.Messages.Batches.MessageBatchIndividualResponse item)
    {
        var oracleId = item.CustomID;
        var value = item.Result.Value;
        if (value is Anthropic.Models.Messages.Batches.MessageBatchSucceededResult ok)
        {
            var text = string.Concat(ok.Message.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
            var (definition, error) = Parse(text);
            var usage = ok.Message.Usage;
            return new Reading(oracleId, model, text, definition, error, usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens ?? 0, usage.CacheCreationInputTokens ?? 0);
        }

        return new Reading(oracleId, model, string.Empty, null, $"batch result: {value?.GetType().Name}", 0, 0, 0, 0);
    }

    public static (CardDefinition? Definition, string? Error) Parse(string text)
    {
        var json = Unfence(text);
        try
        {
            return (CardFileSerializer.ReadDefinition(json), null);
        }
        catch (JsonException ex)
        {
            return (null, ex.Message);
        }
    }

    private static string Unfence(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = trimmed.IndexOf('\n', StringComparison.Ordinal);
            var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && end > firstLine)
                trimmed = trimmed[(firstLine + 1)..end].Trim();
        }

        var start = trimmed.IndexOf('{', StringComparison.Ordinal);
        var stop = trimmed.LastIndexOf('}');
        return start >= 0 && stop > start ? trimmed[start..(stop + 1)] : trimmed;
    }

    /// <summary>One repair round for a reading that did not parse or lint: the error goes back with the original card.</summary>
    public async Task<Reading> RepairAsync(CorpusCard card, Reading failed, string problem, CancellationToken ct)
    {
        var system = SystemWithVocabulary();
        var parameters = new MessageCreateParams
        {
            Model = failed.Model,
            MaxTokens = 16000,
            System = SystemBlocks(system),
            Messages =
            [
                new() { Role = Role.User, Content = UserMessage(card) },
                new() { Role = Role.Assistant, Content = failed.Raw.Length > 0 ? failed.Raw : "{}" },
                new() { Role = Role.User, Content = "That answer was rejected: " + problem + "\nReturn the corrected CardDefinition JSON object only." },
            ],
        };
        var response = await client.Messages.Create(parameters, cancellationToken: ct);
        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        var (definition, error) = Parse(text);
        return new Reading(card.OracleId, failed.Model, text, definition, error, response.Usage.InputTokens, response.Usage.OutputTokens, response.Usage.CacheReadInputTokens ?? 0, response.Usage.CacheCreationInputTokens ?? 0);
    }

    public string PromptVersion => promptVersion;

    public static string Hash(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return System.Convert.ToHexStringLower(bytes)[..12];
    }
}
