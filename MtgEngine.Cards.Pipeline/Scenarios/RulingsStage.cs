using System.Text.Json;
using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>One ruling to turn into a scenario: the card it is about, and what it says.</summary>
public sealed record RulingRequest(string OracleId, string CardName, string Comment, string? PublishedAt = null);

/// <summary>What came back for one ruling: the scenario, or why the answer could not be used.</summary>
public sealed record RulingScenario(RulingRequest Ruling, Scenario? Scenario, string? Error = null);

public sealed record RulingsStageOptions(string Model = "claude-sonnet-5", int PollSeconds = 20);

/// <summary>
/// Turns each Scryfall ruling into a <see cref="Scenario"/> through the batch client, with
/// <see cref="CardFileSerializer.ScenarioSchema"/> as the reference the model writes against.
/// </summary>
/// <remarks>
/// The model is asked for JSON only and is never trusted: the answer is validated on our side by
/// deserialising it into the real records, and anything that does not deserialise is reported as
/// an error rather than stored. A scenario is checked by the runner, not by a second model —
/// CARD_ENGINE_PLAN.md § "Models" — so there is no agreement pass here.
/// </remarks>
public sealed class RulingsStage(ITranslationClient client, RulingsStageOptions? options = null)
{
    private readonly RulingsStageOptions _options = options ?? new RulingsStageOptions();

    /// <summary>The cached system prefix: what a scenario is, and the exact shape of one.</summary>
    public static string SystemPrompt() =>
        """
        You turn a Magic: The Gathering ruling into one scenario: a board, the steps that reproduce
        what the ruling describes, and the expectations that would fail if the ruling were not
        obeyed. Answer with a single JSON object matching the schema below and nothing else — no
        prose, no code fence. Name every card by its exact printed name; the runner resolves names
        through the definition store, so a card that is not on the board cannot be referred to.
        Prefer the smallest board that shows the point of the ruling.

        SCHEMA

        """ + CardFileSerializer.ScenarioSchema().ToJsonString();

    public static string User(RulingRequest ruling)
    {
        ArgumentNullException.ThrowIfNull(ruling);
        return $"Card: {ruling.CardName}\nRuling: {ruling.Comment}\n\nWrite the scenario.";
    }

    /// <summary>Submit every ruling as one batch, wait for it, and validate each answer.</summary>
    public async Task<IReadOnlyList<RulingScenario>> Run(IReadOnlyList<RulingRequest> rulings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rulings);
        if (rulings.Count == 0)
            return [];

        var system = SystemPrompt();
        var byId = rulings.Select((r, i) => (Id: $"ruling-{i}", Ruling: r)).ToDictionary(x => x.Id, x => x.Ruling, StringComparer.Ordinal);
        var batch = await client.SubmitBatch(_options.Model, [.. byId.Select(kv => (kv.Key, system, User(kv.Value)))], ct).ConfigureAwait(false);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var status = await client.Poll(batch, ct).ConfigureAwait(false);
            if (status.Ended)
                break;
            await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), ct).ConfigureAwait(false);
        }

        var scenarios = new List<RulingScenario>(rulings.Count);
        await foreach (var item in client.Results(batch, ct).ConfigureAwait(false))
        {
            if (!byId.TryGetValue(item.CustomId, out var ruling))
                continue;
            scenarios.Add(item.Error is { Length: > 0 }
                ? new RulingScenario(ruling, null, item.Error)
                : Parse(ruling, item.Text));
        }

        return scenarios;
    }

    /// <summary>Validation is ours: the answer either deserialises into the real records or it is an error.</summary>
    public static RulingScenario Parse(RulingRequest ruling, string text)
    {
        ArgumentNullException.ThrowIfNull(ruling);
        var json = Json(text ?? string.Empty);
        if (json.Length == 0)
            return new RulingScenario(ruling, null, "The answer contained no JSON object.");

        try
        {
            var scenario = JsonSerializer.Deserialize<Scenario>(json, CardFileSerializer.Options);
            if (scenario is null)
                return new RulingScenario(ruling, null, "The answer deserialised to nothing.");
            var named = scenario with
            {
                Id = $"ruling-{ruling.OracleId}-{Hash(ruling.Comment)}",
                Source = new RulingSource(ruling.OracleId, ruling.Comment, ruling.PublishedAt),
            };
            return named.Setup.Players.Count == 0
                ? new RulingScenario(ruling, null, "The scenario has no players.")
                : new RulingScenario(ruling, named);
        }
        catch (JsonException ex)
        {
            return new RulingScenario(ruling, null, ex.Message);
        }
    }

    /// <summary>The outermost JSON object in the answer, so a stray sentence or a code fence does not lose the scenario.</summary>
    private static string Json(string text)
    {
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : string.Empty;
    }

    private static string Hash(string text) =>
        System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..8];
}
