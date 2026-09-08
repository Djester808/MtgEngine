using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Pipeline.Tests;

/// <summary>
/// Wave 21's generator roads: a spell whose own last step shuffles it into its owner's library ends
/// there and not in the graveyard, now that E9p made that shuffle a real move. The search half of
/// the wave — a destination that depends on the card found — is in
/// <see cref="ScenarioStageTestsSearches"/>, beside the rest of the search family. Every definition
/// here is invented; nothing reads a real card.
/// </summary>
public sealed class ScenarioStageTestsWave21
{
    /// <summary>
    /// CR 608.2m with CR 400.7: a resolving spell is put into its owner's graveyard as the final
    /// part of its resolution only if it is still on the stack. "Shuffle this card into its owner's
    /// library" has already moved it, so the graveyard never gets it and the library is one longer
    /// than the search left it. Green Sun's Zenith failed on exactly that expectation.
    /// </summary>
    [Fact]
    [RuleRef("608.2m")]
    public void A_spell_that_shuffles_itself_into_its_library_ends_there_and_not_in_the_graveyard()
    {
        var file = Spell(
            "wave21 zenith",
            new SpellAbility(new Sequence(
            [
                new Search(new ObjectFilter(Types: CardType.Creature), Zone.Battlefield),
                new Shuffle(IntoLibrary: new Self()),
            ])),
            CardType.Sorcery);

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Equal(Zone.Library, Assert.Single(scenario!.Expect.OfType<InZone>(), z => z.Card == file.Name).Zone);

        // The search took one card out and the spell put itself back, so neither the library nor
        // the graveyard changed and neither is counted; the battlefield is the one that did.
        Assert.Equal(1, Assert.Single(scenario.Expect.OfType<ZoneCount>()).Count);
        Assert.DoesNotContain(scenario.Expect, e => e is InZone { Zone: Zone.Graveyard });
        Passes(scenario, file);
    }

    /// <summary>
    /// An activation of a source on the battlefield is a road of its own (<see cref="SmokeActivation"/>):
    /// the source is not a spell, so nothing about where a resolved spell goes is said for it.
    /// </summary>
    [Fact]
    [RuleRef("608.2m")]
    public void An_activation_that_shuffles_its_source_away_is_not_read_as_a_spell_ending_in_a_library()
    {
        var file = Spell(
            "wave21 spinning shuffle",
            new ActivatedAbility([new TapSelf()], new Sequence([new Draw(new Fixed(1)), new Shuffle(IntoLibrary: new Self())])),
            CardType.Artifact);

        var scenario = Assert.Single(SmokeActivation.Of(file));

        Assert.DoesNotContain(scenario.Expect, e => e is InZone { Zone: Zone.Library } z && z.Card == file.Name);
    }

    private static void Passes(Scenario scenario, CardFile file)
    {
        var result = new ScenarioRunner(new Game(Subsystems.Wave2(), new ScenarioStageWave21Definitions(file))).Run(scenario);

        Assert.Null(result.Error);
        Assert.True(result.Passed, string.Join(" | ", result.Results.Where(r => !r.Passed).Select(r => $"{r.Expectation}: {r.Actual}")));
    }

    private static CardFile Spell(string name, Ability ability, CardType types, string manaCost = "{1}") => new(
        "oracle-" + name.Replace(' ', '-'),
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition("oracle-" + name.Replace(' ', '-'), name, Layout.Normal,
            [new Face(name, FaceRole.Main, new TypeLine(types), ManaCost: manaCost, Abilities: [ability])]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));

    private sealed class ScenarioStageWave21Definitions(CardFile file) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) =>
            string.Equals(name, file.Name, StringComparison.Ordinal) ? file.Definition : null;

        public CardDefinition? ByOracleId(string oracleId) =>
            string.Equals(oracleId, file.OracleId, StringComparison.Ordinal) ? file.Definition : null;
    }
}
