using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Pipeline.Tests;

/// <summary>
/// The roads wave 17 opened: a reveal of the top X with a choice among them, a creature card put
/// onto the battlefield from a graveyard and paid for by its mana value, a spell that exiles or
/// shuffles itself away instead of going to the graveyard, and a grant onto one target creature.
/// Every one that can be played is played through the real engine.
/// </summary>
public sealed class ScenarioStageTestsRemembered
{
    [Fact]
    [RuleRef("701.15a")]
    public void A_reveal_then_choose_gets_no_scenario_until_the_engine_keeps_the_chosen_card()
    {
        // Staged and played in wave 17: the engine put the chosen card into the graveyard with the
        // rest, so the road is withheld rather than reporting that as the card's failure
        // (seam-requests/P19.md). Drop this test with the seam.
        var file = File(
            "wave of cards",
            new SpellAbility(new Sequence(
            [
                new Reveal(new Top(new You(), new X()), RememberAs: "k1"),
                new Move(
                    new Choose(new ObjectFilter(Permanent: true, ManaValue: new Comparison(ComparisonOp.Le, new X())), new You(), UpTo: true, Key: "k2", Among: new Remembered("k1")),
                    Zone.Battlefield),
                new Move(
                    new Each(new ObjectFilter(In: Zone.Library, Also: new AllOf([new IsRemembered("k1"), new NotPredicate(new IsRemembered("k2"))]))),
                    Zone.Graveyard),
            ])),
            CardType.Sorcery,
            "{X}{G}");

        Assert.Null(SmokeScenario.Of(file));
    }

    [Fact]
    [RuleRef("404.1")]
    public void A_creature_card_put_onto_the_battlefield_from_a_graveyard_costs_its_mana_value_in_life()
    {
        var file = File(
            "raise it",
            new SpellAbility(new Sequence(
            [
                new Move(new Target(new ObjectFilter(In: Zone.Graveyard, Types: CardType.Creature)), Zone.Battlefield, UnderControlOf: new You(), RememberAs: "k1"),
                new LoseLife(new ManaValueOf(new Remembered("k1"))),
            ])),
            CardType.Sorcery,
            "{B}");

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        var buried = Assert.Single(scenario!.Setup.Players[0].Graveyard!);
        Assert.Equal([buried.Alias!], Assert.IsType<CastStep>(scenario.Steps[0]).Targets);
        Assert.Contains(scenario.Expect, e => e is InZone { Zone: Zone.Battlefield } back && back.Card == buried.Alias);
        // CR 202.3: the filler creature costs two, so two life is what the caster is left short.
        Assert.Equal(20 - ScenarioFiller.ManaValueOf(buried.Name)!.Value, Assert.Single(scenario.Expect.OfType<Life>()).Value);
        Passes(scenario, file);
    }

    [Fact]
    [RuleRef("608.2m")]
    public void A_spell_that_exiles_itself_is_expected_in_exile_and_not_the_graveyard()
    {
        var file = File("burn after reading", new SpellAbility(new Sequence([new Draw(new Fixed(1)), new Exile(new Self())])), CardType.Instant);

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        Assert.Equal(Zone.Exile, Assert.Single(scenario!.Expect.OfType<InZone>(), z => z.Card == file.Name).Zone);
        Assert.DoesNotContain(scenario.Expect, e => e is ZoneCount { Zone: Zone.Graveyard });
        Passes(scenario, file);
    }

    [Fact]
    [RuleRef("608.2m")]
    public void A_spell_that_only_shuffles_itself_away_gets_no_scenario_until_the_engine_moves_it()
    {
        // Played in wave 17: the engine left such a spell in the graveyard, so the zone is not
        // asserted (seam-requests/P19.md) and a card that does nothing else has nothing to assert.
        Assert.Null(SmokeScenario.Of(File("come back later", new SpellAbility(new Shuffle(IntoLibrary: new Self())), CardType.Sorcery)));
    }

    [Fact]
    [RuleRef("611.2a")]
    public void A_grant_onto_a_target_creature_aims_at_one_and_the_caster_pays_the_life()
    {
        var file = File(
            "borrowed time",
            new SpellAbility(new Sequence(
            [
                new LoseLife(new Fixed(2)),
                new Apply(new GrantKeyword(KeywordKind.Hexproof), new Target(new ObjectFilter(Types: CardType.Creature)), new UntilEndOfTurn()),
            ])),
            CardType.Instant,
            "{B}");

        var scenario = SmokeScenario.Of(file);

        Assert.NotNull(scenario);
        var creature = Assert.Single(scenario!.Setup.Players[1].Battlefield!);
        Assert.Equal([creature.Alias!], Assert.IsType<CastStep>(scenario.Steps[0]).Targets);
        Assert.Equal(18, Assert.Single(scenario.Expect.OfType<Life>()).Value);
        // The event the engine logs when a continuous effect is created (CR 611.2a), as the generator names it.
        Assert.Contains(scenario.Expect, e => e is EventLogged { EventKind: "ContinuousEffectCreated" });
        Passes(scenario, file);
    }

    [Fact]
    [RuleRef("119.4")]
    public void Life_gained_and_lost_by_one_player_is_one_total()
    {
        var scenario = SmokeScenario.Of(File("give and take", new SpellAbility(new Sequence([new GainLife(new Fixed(3)), new LoseLife(new Fixed(1))])), CardType.Instant));

        Assert.NotNull(scenario);
        Assert.Equal(22, Assert.Single(scenario!.Expect.OfType<Life>()).Value);
    }

    private static void Passes(Scenario scenario, CardFile file)
    {
        var result = new ScenarioRunner(new Game(Subsystems.Wave2(), new ScenarioStageRememberedDefinitions(file))).Run(scenario);

        Assert.Null(result.Error);
        Assert.True(result.Passed, string.Join(" | ", result.Results.Where(r => !r.Passed).Select(r => $"{r.Expectation}: {r.Actual}")));
    }

    private static CardFile File(string name, Ability ability, CardType types, string manaCost = "{1}") => new(
        "oracle-" + name.Replace(' ', '-'),
        name,
        OracleHashing.Of([name]),
        IrVersion.Current,
        new CardDefinition("oracle-" + name.Replace(' ', '-'), name, Layout.Normal,
            [new Face(name, FaceRole.Main, new TypeLine(types), ManaCost: manaCost, Abilities: [ability])]),
        ImplementationStatus.Translated,
        new TemplateProvenance("test"));

    private sealed class ScenarioStageRememberedDefinitions(CardFile file) : IDefinitionStore
    {
        public CardDefinition? ByName(string name) =>
            string.Equals(name, file.Name, StringComparison.Ordinal) ? file.Definition : null;

        public CardDefinition? ByOracleId(string oracleId) =>
            string.Equals(oracleId, file.OracleId, StringComparison.Ordinal) ? file.Definition : null;
    }
}
