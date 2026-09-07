using System.Text.Json.Nodes;
using MtgEngine.Cards.Pipeline.Agree;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// The fourth canonical pass: "equipped creature" inside a trigger's filter. The pair rule holds
/// here as everywhere — the two spellings agree on a trigger's filter, and the same predicate
/// anywhere else is a reading the pass may not touch.
/// </summary>
public sealed partial class CanonicalTests
{
    /// <summary>The inverted spelling the pilot cards wrote: the Auras on this Equipment, never the creature it is on.</summary>
    private static Predicate EquippedByAttachedToSelf() => new AttachedTo(new Self());

    /// <summary>The spelling that names the equipped creature from the Equipment's side (CR 301.5c).</summary>
    private static Predicate EquippedByOneOfAttached() => new IsOneOf(new Attached());

    private static Ability WheneverDies(ObjectFilter what) => new TriggeredAbility(new Dies(what), new Draw(new Fixed(2)));

    [Fact]
    public void Equipped_creature_in_a_trigger_filter_is_one_of_attached()
    {
        Same(
            Card(WheneverDies(new ObjectFilter(Also: EquippedByAttachedToSelf()))),
            Card(WheneverDies(new ObjectFilter(Also: EquippedByOneOfAttached()))));
        Same(
            Card(new TriggeredAbility(new DealsDamage(new ObjectFilter(Types: CardType.Creature, Also: EquippedByAttachedToSelf()), Combat: true), new Draw(new Fixed(1)))),
            Card(new TriggeredAbility(new DealsDamage(new ObjectFilter(Types: CardType.Creature, Also: EquippedByOneOfAttached()), Combat: true), new Draw(new Fixed(1)))));

        // And the definition really carries the new spelling, so the card file does too.
        var dies = (Dies)((TriggeredAbility)Canonical.Of(Card(WheneverDies(new ObjectFilter(Also: EquippedByAttachedToSelf())))).Faces[0].AbilityList[0]).When;
        Assert.IsType<Attached>(Assert.IsType<IsOneOf>(dies.What.Also).Of);
    }

    [Fact]
    public void Equipped_creature_among_other_conditions_in_a_trigger_filter_is_rewritten_too()
    {
        // "Whenever equipped creature or the chosen creature dies": the member is rewritten, the rest stays.
        Same(
            Card(WheneverDies(new ObjectFilter(Also: new AnyOf([EquippedByAttachedToSelf(), new IsRemembered("it")])))),
            Card(WheneverDies(new ObjectFilter(Also: new AnyOf([EquippedByOneOfAttached(), new IsRemembered("it")])))));

        var dies = (Dies)((TriggeredAbility)Canonical.Of(
            Card(WheneverDies(new ObjectFilter(Also: new AllOf([new IsRemembered("it"), EquippedByAttachedToSelf()]))))).Faces[0].AbilityList[0]).When;
        var all = Assert.IsType<AllOf>(dies.What.Also);
        Assert.IsType<IsRemembered>(all.Of[0]);
        Assert.IsType<Attached>(Assert.IsType<IsOneOf>(all.Of[1]).Of);
    }

    [Fact]
    public void Attached_to_self_outside_a_trigger_filter_is_a_reading()
    {
        // "Destroy all Auras attached to it" is exactly what AttachedTo(Self) says in an effect.
        Differs(
            Spell(new Destroy(new Each(new ObjectFilter(Also: EquippedByAttachedToSelf())))),
            Spell(new Destroy(new Each(new ObjectFilter(Also: EquippedByOneOfAttached())))));

        // Inside a trigger's effect it is still an effect's filter, not the trigger's.
        Differs(
            Card(new TriggeredAbility(new Dies(new ObjectFilter(Also: EquippedByOneOfAttached())), new Destroy(new Each(new ObjectFilter(Also: EquippedByAttachedToSelf()))))),
            Card(new TriggeredAbility(new Dies(new ObjectFilter(Also: EquippedByOneOfAttached())), new Destroy(new Each(new ObjectFilter(Also: EquippedByOneOfAttached()))))));

        var destroy = (Destroy)Do(Canonical.Of(Spell(new Destroy(new Each(new ObjectFilter(Also: EquippedByAttachedToSelf()))))));
        Assert.IsType<Self>(Assert.IsType<AttachedTo>(((Each)destroy.What).Filter.Also).To);
    }

    [Fact]
    public void Attached_to_another_object_in_a_trigger_filter_is_kept()
    {
        // "Whenever an Aura attached to that creature dies" hangs the other way and stays as written.
        Differs(
            Card(WheneverDies(new ObjectFilter(Also: new AttachedTo(new Triggering())))),
            Card(WheneverDies(new ObjectFilter(Also: EquippedByOneOfAttached()))));
        Differs(
            Card(WheneverDies(new ObjectFilter(Also: new AttachedTo(new Remembered("it"))))),
            Card(WheneverDies(new ObjectFilter(Also: EquippedByOneOfAttached()))));
    }

    [Fact]
    public void The_pilot_cards_trigger_shape_agrees_with_the_one_spelling()
    {
        // The tree as the three pilot files have it (a Dies with only the predicate in its filter).
        static JsonObject Trigger(JsonObject also) => new()
        {
            ["kind"] = nameof(TriggeredAbility),
            ["when"] = new JsonObject { ["kind"] = nameof(Dies), ["what"] = new JsonObject { ["also"] = also } },
            ["do"] = new JsonObject { ["kind"] = nameof(Draw), ["n"] = new JsonObject { ["kind"] = nameof(Fixed), ["n"] = 2 } },
        };

        SameTree(
            Trigger(new JsonObject { ["kind"] = nameof(AttachedTo), ["to"] = new JsonObject { ["kind"] = nameof(Self) } }),
            Trigger(new JsonObject { ["kind"] = nameof(IsOneOf), ["of"] = new JsonObject { ["kind"] = nameof(Attached) } }));

        // The filters read are derived from the trigger records, so the coverage is printable.
        Assert.Equal(["what"], Canonical.TriggerFiltersRead[nameof(Dies)]);
        Assert.Contains("source", Canonical.TriggerFiltersRead[nameof(DealsDamage)]);
        Assert.Contains("who", Canonical.TriggerFiltersRead[nameof(Attacks)]);
        Assert.False(Canonical.TriggerFiltersRead.ContainsKey(nameof(AtStep)));
    }
}
