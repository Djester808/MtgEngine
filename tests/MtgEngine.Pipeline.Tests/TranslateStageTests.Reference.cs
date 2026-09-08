using MtgEngine.Cards.Pipeline.Translate;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// What the translator is actually told. The vocabulary is closed (CARD_ENGINE_PLAN.md, rule 2),
/// so a record the prompt never names is a record the model cannot be expected to write — and the
/// pilot's disagreements were, over and over, two readings of a record nothing had spelled.
/// </summary>
public sealed class TranslateStageReferenceTests
{
    /// <summary>
    /// What the cached prefix may grow to. It is sent with every request in every batch, so its
    /// size is a bill before it is a limit — at four characters to the token today's prefix is
    /// about a sixth of the answer budget <c>AnthropicTranslationClient</c> allows (16,000 tokens)
    /// away from being the largest thing in the call. The number is a ceiling with room to grow a
    /// half again, not a measurement: a change that doubles the reference should have to say so.
    /// </summary>
    // V9 to V11 (waves 15 to 19) grew the reference from 68k to 103k characters; the readings now travel
    // as files read by agents, so the budget is the reader's, not an API cache window's. V14 and V15
    // (waves 25 and 26) took it to 142k: twenty records with their one-spelling lines.
    private const int PrefixBudget = 160_000;

    /// <summary>The records added after the pilot's prompt was written; each needs its one reading said.</summary>
    private static readonly string[] Later =
    [
        nameof(MayPay),
        nameof(TappedForMana),
        nameof(ManaSpent),
        nameof(CreatedTokenThisTurn),
        "HasBasicLandType",
        "PayOnlyFrom",
        "OfPlayer",
        nameof(AttachedPlayer),
        "ManaColors",
    ];

    [Fact]
    public void Every_name_table_record_is_named_by_the_reference_or_the_schema()
    {
        var reference = VocabularyReference.Text();
        var schema = CardFileSerializer.DefinitionSchema().ToJsonString();

        // The four bases the reference itself skips are the file's own records — provenance, a
        // scenario's source, its steps and its expectations. A translator writes a definition and
        // never a scenario, so naming them in the prompt would be teaching a vocabulary the model
        // is asked not to use (VocabularyReference.Text).
        var written = CardFileSerializer.PolymorphicBases
            .Where(b => b != typeof(Provenance) && b != typeof(ScenarioSource) && b != typeof(ScenarioStep) && b != typeof(Expectation));

        var missing = written
            .SelectMany(b => CardFileSerializer.RegisteredNames(b).Values)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !reference.Contains(name, StringComparison.Ordinal) && !schema.Contains(name, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0, "the translator is never told about: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_records_the_vocabulary_gained_after_the_pilot_have_a_spelling_rule()
    {
        var prompt = TranslationPrompt.SystemWithVocabulary();

        var unsaid = Later.Where(name => !TranslationPrompt.LaterSpellings.Contains(name, StringComparison.Ordinal)).ToList();

        Assert.True(unsaid.Count == 0, "no one spelling is given for: " + string.Join(", ", unsaid));
        Assert.Contains("## One spelling", prompt, StringComparison.Ordinal);
        Assert.Contains("## One spelling, continued", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cached_prefix_stays_inside_the_budget_an_answer_needs()
    {
        var prompt = TranslationPrompt.SystemWithVocabulary();

        Assert.True(
            prompt.Length < PrefixBudget,
            FormattableString.Invariant($"the cached prefix is {prompt.Length} characters, past the {PrefixBudget} it is allowed."));
    }

    [Fact]
    public void A_changed_prompt_is_a_changed_version_so_readings_never_mix()
    {
        var version = TranslationPrompt.VersionOf("retranslate");

        Assert.StartsWith("retranslate-", version, StringComparison.Ordinal);
        Assert.Equal(TranslationPrompt.Hash(TranslationPrompt.SystemWithVocabulary()), version["retranslate-".Length..]);
        Assert.NotEqual(TranslationPrompt.Hash(TranslationPrompt.SystemPrompt), version["retranslate-".Length..]);
    }
}
