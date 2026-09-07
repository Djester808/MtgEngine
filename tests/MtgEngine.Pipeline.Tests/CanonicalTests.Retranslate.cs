using System.Text.Json.Nodes;
using MtgEngine.Cards.Pipeline.Agree;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// The third canonical pass: the families the subscription re-translation's 46 first differences
/// fell into that are one meaning written two ways. Every test here comes in a pair — the two
/// spellings agree after the pass, and a difference that changes the card still disputes — because
/// a canonical rule that cannot fail the second half is a rubber stamp.
/// </summary>
public sealed partial class CanonicalTests
{
    /// <summary>The same rewrite on a hand-written tree, which is what the readings actually are.</summary>
    private static void SameTree(JsonNode left, JsonNode right) =>
        Assert.Null(JsonDiff.Between(Canonical.Of(left), Canonical.Of(right)));

    private static void DiffersTree(JsonNode left, JsonNode right) =>
        Assert.NotNull(JsonDiff.Between(Canonical.Of(left), Canonical.Of(right)));

    [Fact]
    public void A_basic_land_type_already_says_land()
    {
        // CR 205.3i: only a land has a land type, so the written Land says nothing more.
        Same(
            Spell(new Search(new ObjectFilter(Types: CardType.Land, Subtypes: ["Forest"]), Zone.Battlefield)),
            Spell(new Search(new ObjectFilter(Subtypes: ["Forest"]), Zone.Battlefield)));
        Same(
            Spell(new Search(new ObjectFilter(Types: CardType.Land, Subtypes: ["Island", "Swamp"]), Zone.Battlefield)),
            Spell(new Search(new ObjectFilter(Subtypes: ["Island", "Swamp"]), Zone.Battlefield)));

        // And the definition really loses it, so the card file carries one spelling too.
        var searched = (Search)Do(Canonical.Of(Spell(new Search(new ObjectFilter(Types: CardType.Land, Subtypes: ["Plains"]), Zone.Battlefield))));
        Assert.Null(searched.For.Types);
        Assert.Equal(["Plains"], searched.For.Subtypes);
    }

    [Fact]
    public void A_type_a_subtype_does_not_imply_is_kept()
    {
        // A creature type says nothing about the card type: "Forest Dryad" is not "Forest".
        Differs(
            Spell(new Search(new ObjectFilter(Types: CardType.Creature, Subtypes: ["Dryad"]), Zone.Battlefield)),
            Spell(new Search(new ObjectFilter(Subtypes: ["Dryad"]), Zone.Battlefield)));

        // A land type the rules data does not name is not rules data we hold, so Land stays.
        Differs(
            Spell(new Search(new ObjectFilter(Types: CardType.Land, Subtypes: ["Gate"]), Zone.Battlefield)),
            Spell(new Search(new ObjectFilter(Subtypes: ["Gate"]), Zone.Battlefield)));

        // "Artifact land" means both types; only the whole type line of Land is implied.
        Differs(
            Spell(new Search(new ObjectFilter(Types: CardType.Land | CardType.Artifact, Subtypes: ["Forest"]), Zone.Battlefield)),
            Spell(new Search(new ObjectFilter(Subtypes: ["Forest"]), Zone.Battlefield)));
    }

    [Fact]
    public void Your_spells_on_a_modifier_of_your_costs_is_the_modifier_saying_it_twice()
    {
        Same(
            Card(new StaticAbility(new CostModifier(new ObjectFilter(ControlledBy: new You()), new Fixed(1), new You()))),
            Card(new StaticAbility(new CostModifier(ObjectFilter.Any, new Fixed(1), new You()))));

        var modifier = (CostModifier)((StaticAbility)Canonical.Of(
            Card(new StaticAbility(new CostModifier(new ObjectFilter(ControlledBy: new You()), new Fixed(1), new You()))))
            .Faces[0].AbilityList[0]).Effect;
        Assert.Null(modifier.Spells.ControlledBy);
    }

    [Fact]
    public void A_modifier_of_someone_elses_costs_keeps_whose_spells_it_reads()
    {
        // "Spells your opponents cast cost {1} more" pays from an opponent; a filter naming You
        // there is a different card, and nothing may fold it away.
        Differs(
            Card(new StaticAbility(new CostModifier(new ObjectFilter(ControlledBy: new You()), new Fixed(1), new EachOpponent()))),
            Card(new StaticAbility(new CostModifier(ObjectFilter.Any, new Fixed(1), new EachOpponent()))));

        // And whose costs change is still a reading of the card.
        Differs(
            Card(new StaticAbility(new CostModifier(ObjectFilter.Any, new Fixed(1), new You()))),
            Card(new StaticAbility(new CostModifier(ObjectFilter.Any, new Fixed(1), new EachOpponent()))));
    }

    [Fact]
    public void A_spell_cast_trigger_already_watches_spells()
    {
        Same(
            Card(new TriggeredAbility(new SpellCast(new ObjectFilter(Spell: true), new You()), new Draw(new Fixed(1)))),
            Card(new TriggeredAbility(new SpellCast(ObjectFilter.Any, new You()), new Draw(new Fixed(1)))));

        var trigger = (SpellCast)((TriggeredAbility)Canonical.Of(
            Card(new TriggeredAbility(new SpellCast(new ObjectFilter(Spell: true, Types: CardType.Instant), new You()), new Draw(new Fixed(1)))))
            .Faces[0].AbilityList[0]).When;
        Assert.Null(trigger.Spell.Spell);
        Assert.Equal(CardType.Instant, trigger.Spell.Types);
    }

    [Fact]
    public void The_spell_flag_is_a_reading_everywhere_else()
    {
        // Off the trigger it separates a spell on the stack from a permanent, which is the filter.
        Differs(
            Spell(new Search(new ObjectFilter(Spell: true), Zone.Battlefield)),
            Spell(new Search(ObjectFilter.Any, Zone.Battlefield)));

        // And who cast the spell the trigger watches is still a reading.
        Differs(
            Card(new TriggeredAbility(new SpellCast(ObjectFilter.Any, new You()), new Draw(new Fixed(1)))),
            Card(new TriggeredAbility(new SpellCast(ObjectFilter.Any, new EachOpponent()), new Draw(new Fixed(1)))));
    }

    [Fact]
    public void The_candidate_measured_among_a_filter_is_self()
    {
        // Query.Amounts.Among binds the candidate as the source, so Self is what the inner amount
        // reads and Triggering in that position is the same reading spelled with the wrong word.
        Same(
            Spell(new GainLife(new GreatestAmong(ObjectFilter.Creature, new PowerOf(new Triggering())))),
            Spell(new GainLife(new GreatestAmong(ObjectFilter.Creature, new PowerOf(new Self())))));
        Same(
            Spell(new GainLife(new TotalAmong(ObjectFilter.Creature, new ToughnessOf(new Triggering())))),
            Spell(new GainLife(new TotalAmong(ObjectFilter.Creature, new ToughnessOf(new Self())))));

        var among = (GreatestAmong)((GainLife)Do(Canonical.Of(
            Spell(new GainLife(new GreatestAmong(ObjectFilter.Creature, new PowerOf(new Triggering()))))))).N;
        Assert.IsType<Self>(((PowerOf)among.Of).Of);
    }

    [Fact]
    public void The_triggering_object_outside_the_measured_amount_is_kept()
    {
        // The other object of a trigger is not the candidate, so the role is a real difference.
        Differs(
            Spell(new GainLife(new GreatestAmong(ObjectFilter.Creature, new PowerOf(new Triggering(TriggerRole.Other))))),
            Spell(new GainLife(new GreatestAmong(ObjectFilter.Creature, new PowerOf(new Self())))));

        // Outside an Among there is no candidate at all: "its power" is the trigger's object.
        Differs(
            Spell(new GainLife(new PowerOf(new Triggering()))),
            Spell(new GainLife(new PowerOf(new Self()))));
    }

    [Fact]
    public void A_default_written_out_is_the_same_as_leaving_it_out()
    {
        var written = new JsonObject
        {
            ["kind"] = nameof(Search),
            ["for"] = new JsonObject { ["types"] = new JsonArray(JsonValue.Create(nameof(CardType.Creature))) },
            ["to"] = nameof(Zone.Battlefield),
            ["from"] = nameof(Zone.Library),
            ["shuffleOnlyIfFound"] = false,
        };
        var omitted = new JsonObject
        {
            ["kind"] = nameof(Search),
            ["for"] = new JsonObject { ["types"] = new JsonArray(JsonValue.Create(nameof(CardType.Creature))) },
            ["to"] = nameof(Zone.Battlefield),
        };
        SameTree(written, omitted);

        // The rule is derived from the constructors, not listed, so its coverage is printable.
        Assert.Contains("shuffleOnlyIfFound", Canonical.DefaultsThatNeedNoWriting[nameof(Search)]);
        Assert.Contains("shuffleAfter", Canonical.DefaultsThatNeedNoWriting[nameof(Search)]);
    }

    [Fact]
    public void A_value_that_is_not_the_default_is_kept()
    {
        var found = new JsonObject
        {
            ["kind"] = nameof(Search),
            ["for"] = new JsonObject(),
            ["to"] = nameof(Zone.Battlefield),
            ["shuffleOnlyIfFound"] = true,
        };
        var always = new JsonObject
        {
            ["kind"] = nameof(Search),
            ["for"] = new JsonObject(),
            ["to"] = nameof(Zone.Battlefield),
        };
        DiffersTree(found, always);

        // A nullable flag has no default to write out: false there is a constraint, not noise.
        DiffersTree(
            new JsonObject { ["kind"] = nameof(Tap), ["what"] = new JsonObject { ["kind"] = nameof(Target), ["filter"] = new JsonObject { ["tapped"] = false } } },
            new JsonObject { ["kind"] = nameof(Tap), ["what"] = new JsonObject { ["kind"] = nameof(Target), ["filter"] = new JsonObject() } });
    }
}
