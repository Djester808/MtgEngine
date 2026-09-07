using System.Text.Json.Nodes;
using MtgEngine.Cards.Pipeline.Agree;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// Wave 17's rules for the twelve still-disputed cards: the first differences that are one meaning
/// written two ways. As before, every rule comes with the difference it must not hide.
/// </summary>
public sealed partial class CanonicalTests
{
    [Fact]
    public void Spells_cast_this_turn_already_counts_spells()
    {
        // CR 608.1: SpellsCastThisTurn counts spells, so spell: true on its filter says its own name.
        var flagged = new JsonObject
        {
            ["kind"] = nameof(SpellsCastThisTurn),
            ["by"] = new JsonObject { ["kind"] = nameof(EachOpponent) },
            ["matching"] = new JsonObject { ["spell"] = true, ["types"] = new JsonArray(JsonValue.Create(nameof(CardType.Instant))) },
        };
        var plain = new JsonObject
        {
            ["kind"] = nameof(SpellsCastThisTurn),
            ["by"] = new JsonObject { ["kind"] = nameof(EachOpponent) },
            ["matching"] = new JsonObject { ["types"] = new JsonArray(JsonValue.Create(nameof(CardType.Instant))) },
        };
        SameTree(flagged, plain);

        // Who cast them is still a reading.
        var yours = new JsonObject
        {
            ["kind"] = nameof(SpellsCastThisTurn),
            ["by"] = new JsonObject { ["kind"] = nameof(You) },
            ["matching"] = new JsonObject { ["types"] = new JsonArray(JsonValue.Create(nameof(CardType.Instant))) },
        };
        DiffersTree(plain, yours);
    }

    [Fact]
    public void A_permanent_flag_on_a_battlefield_filter_says_where_it_already_is()
    {
        // CR 110.1: "all permanents you control" read on the battlefield are permanents already.
        Same(
            Spell(new Apply(new GrantKeyword(KeywordKind.Hexproof), new Each(new ObjectFilter(Permanent: true, ControlledBy: new You())), new UntilEndOfTurn())),
            Spell(new Apply(new GrantKeyword(KeywordKind.Hexproof), new Each(new ObjectFilter(ControlledBy: new You())), new UntilEndOfTurn())));

        var granted = (Apply)Do(Canonical.Of(
            Spell(new Apply(new GrantKeyword(KeywordKind.Hexproof), new Each(new ObjectFilter(Permanent: true, ControlledBy: new You())), new UntilEndOfTurn()))));
        Assert.Null(((Each)granted.To).Filter.Permanent);
    }

    [Fact]
    public void A_permanent_flag_in_any_other_zone_picks_cards_out()
    {
        // "A permanent card" in a library is a constraint; the search's filter is read in the library.
        Differs(
            Spell(new Search(new ObjectFilter(Permanent: true), Zone.Battlefield)),
            Spell(new Search(ObjectFilter.Any, Zone.Battlefield)));

        // And inside a move, whose filter is read wherever the card says (CR 400.1).
        Differs(
            Spell(new Move(new Each(new ObjectFilter(In: Zone.Graveyard, Permanent: true)), Zone.Hand)),
            Spell(new Move(new Each(new ObjectFilter(In: Zone.Graveyard)), Zone.Hand)));
    }

    [Fact]
    public void An_effects_by_of_you_is_the_default_under_another_name()
    {
        // CR 109.5: the controller of the ability does it unless the card names someone else.
        Same(
            Spell(new Reorder(new Top(new You(), new Fixed(3)), Placement.AnyOrder, new You())),
            Spell(new Reorder(new Top(new You(), new Fixed(3)), Placement.AnyOrder)));
        Same(
            Spell(new Goad(new Target(ObjectFilter.Creature), new You())),
            Spell(new Goad(new Target(ObjectFilter.Creature))));

        // Someone else doing it is the card.
        Differs(
            Spell(new Reorder(new Top(new You(), new Fixed(3)), Placement.AnyOrder, new EachOpponent())),
            Spell(new Reorder(new Top(new You(), new Fixed(3)), Placement.AnyOrder)));
    }

    [Fact]
    public void A_triggers_by_of_you_is_a_reading()
    {
        // A trigger with no By watches every player; You there narrows it (CR 603.2).
        Differs(
            Card(new TriggeredAbility(new AbilityActivated(ObjectFilter.Any, By: new You()), new Draw(new Fixed(1)))),
            Card(new TriggeredAbility(new AbilityActivated(ObjectFilter.Any), new Draw(new Fixed(1)))));
    }
}
