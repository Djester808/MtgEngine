using MtgEngine.Cards;
using MtgEngine.Cards.Pipeline.Scenarios;
using MtgEngine.Rules.Scenarios;

namespace MtgEngine.Pipeline.Tests;

/// <summary>
/// Wave 28's generator roads: a land whose own arrival sacrifices lands is given the lands to
/// sacrifice (CR 701.16), and a resolution that discards at random says nothing about which card
/// it took (CR 701.9b). Every definition here is invented; nothing reads a real card.
/// </summary>
public sealed class ScenarioStageTestsWave28
{
    private const string Field = "wave28 field";
    private const string Wager = "wave28 wager";

    /// <summary>
    /// CR 701.16 with CR 701.16b: "when this enters, sacrifice two lands" over an empty battlefield
    /// has exactly one land to take — the one that just arrived — so the card eats itself and the
    /// arrival the scenario asserts cannot hold. The bodies the choice will take are stood by the
    /// setup, older than the card under test (CR 400.7), and the play then shows the sacrifice.
    /// </summary>
    [Fact]
    [RuleRef("701.16")]
    public void A_land_whose_arrival_sacrifices_lands_stands_the_lands_it_will_take()
    {
        var scenario = SmokeScenario.Of(Land(Field, Sacrificing(2)));

        Assert.NotNull(scenario);
        var stood = scenario.Setup.Players[0].Battlefield ?? [];
        Assert.Equal(2, stood.Count);
        Assert.All(stood, card => Assert.Equal(ScenarioFiller.Land, card.Name));

        // Each stood land is named in a graveyard, and the land under test is still standing.
        foreach (var card in stood)
            Assert.Contains(new InZone(card.Alias!, Zone.Graveyard, "A"), scenario.Expect);
        Assert.Contains(new InZone(Field, Zone.Battlefield), scenario.Expect);
        Assert.Contains(new ZoneCount("A", Zone.Graveyard, 2), scenario.Expect);
    }

    /// <summary>
    /// The falsifier for the road above: without the bodies the play asserts nothing but the
    /// arrival, which is the scenario that failed. A land whose arrival sacrifices creatures gets
    /// creatures, so the road is not "stand two of whatever the card is".
    /// </summary>
    [Fact]
    [RuleRef("701.16a")]
    public void The_bodies_a_sacrifice_stands_are_the_kind_its_own_filter_admits()
    {
        var scenario = SmokeScenario.Of(Land(Field, new TriggeredAbility(
            new EntersBattlefield(new ObjectFilter(Also: new IsThisObject())),
            new Sacrifice(Choice: new ObjectFilter(Types: CardType.Creature), N: new Fixed(1)))));

        Assert.NotNull(scenario);
        var stood = Assert.Single(scenario.Setup.Players[0].Battlefield ?? []);
        Assert.Equal(ScenarioFiller.Creature, stood.Name);
        Assert.Contains(new ZoneCount("A", Zone.Graveyard, 1), scenario.Expect);
    }

    /// <summary>
    /// CR 701.9b: "search your library for a card, put it into your hand, discard a card at random,
    /// then shuffle". The discard may take the card the search just found, so nothing names it —
    /// what holds either way is the size of the hand it left and the size of the graveyard it
    /// reached, and the hand is counted even though the two moves cancel.
    /// </summary>
    [Fact]
    [RuleRef("701.9b")]
    public void A_random_discard_does_not_name_the_card_the_search_found()
    {
        var scenario = SmokeScenario.Of(Sorcery(Wager, Gambling(DiscardChooser.Random)));

        Assert.NotNull(scenario);
        var found = Assert.Single(scenario.Setup.Players[0].Library ?? [], c => c.Alias is not null);
        Assert.DoesNotContain(scenario.Expect, e => e is InZone zone && string.Equals(zone.Card, found.Alias, StringComparison.Ordinal));

        // Three cards left in hand — one searched in, one discarded out — and the spell plus the
        // discarded card in the graveyard.
        Assert.Contains(new ZoneCount("A", Zone.Hand, 3), scenario.Expect);
        Assert.Contains(new ZoneCount("A", Zone.Graveyard, 2), scenario.Expect);
        Assert.Contains(new Shuffled("A"), scenario.Expect);
    }

    /// <summary>
    /// The falsifier: the same card with a discard its owner chooses still names the card the
    /// search found, so the silence above is about the randomness and not about searches.
    /// </summary>
    [Fact]
    [RuleRef("701.19")]
    public void A_chosen_discard_still_names_the_card_the_search_found()
    {
        var scenario = SmokeScenario.Of(Sorcery(Wager, Gambling(DiscardChooser.Owner)));

        Assert.NotNull(scenario);
        var found = Assert.Single(scenario.Setup.Players[0].Library ?? [], c => c.Alias is not null);
        Assert.Contains(new InZone(found.Alias!, Zone.Hand), scenario.Expect);
        Assert.Contains(new ZoneCount("A", Zone.Graveyard, 2), scenario.Expect);
    }

    /// <summary>CR 701.16: the trigger Lotus Field's shape has — sacrifice N of a kind, chosen by its controller.</summary>
    private static TriggeredAbility Sacrificing(int n) => new(
        new EntersBattlefield(new ObjectFilter(Also: new IsThisObject())),
        new Sacrifice(Choice: new ObjectFilter(Types: CardType.Land), N: new Fixed(n)));

    /// <summary>CR 701.19 with CR 701.9b: search into the hand, then discard, then shuffle.</summary>
    private static SpellAbility Gambling(DiscardChooser by) => new(new Sequence(
    [
        new Search(new ObjectFilter(), Zone.Hand, ShuffleAfter: true),
        new Discard(By: by),
    ]));

    private static CardFile Land(string name, params Ability[] abilities) =>
        File(name, new TypeLine(CardType.Land), null, abilities);

    private static CardFile Sorcery(string name, params Ability[] abilities) =>
        File(name, new TypeLine(CardType.Sorcery), "{R}", abilities);

    private static CardFile File(string name, TypeLine types, string? manaCost, Ability[] abilities)
    {
        var oracleId = "oracle-" + name.Replace(' ', '-');
        return new CardFile(
            oracleId,
            name,
            OracleHashing.Of([name]),
            IrVersion.Current,
            new CardDefinition(oracleId, name, Layout.Normal, [new Face(name, FaceRole.Main, types, ManaCost: manaCost, Abilities: abilities)]),
            ImplementationStatus.Translated,
            new TemplateProvenance("test"));
    }
}
