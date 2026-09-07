using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using AbilityActivated = MtgEngine.Rules.Events.AbilityActivated;
using CardZone = MtgEngine.Cards.Zone;
using LandPlayed = MtgEngine.Rules.Events.LandPlayed;
using SpellCast = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Playing a land, casting a spell, activating an ability and resolving the stack: CR 305, 601,
/// 602, 605 and 608. Each test names the rule it holds, and each refusal is checked by its rule
/// number rather than its wording, because the wording is a message and the rule is the contract.
/// </summary>
public sealed partial class CastingTests
{
    private readonly RecordingInterpreter interpreter = new();
    private readonly CastingSystem casting;

    public CastingTests() => casting = new CastingSystem(new CastingFakeZones(), new FixedAmounts(), new PrintedCharacteristics(), interpreter);

    [Fact]
    public void A_land_is_played_to_the_battlefield_and_a_second_one_is_refused()
    {
        // CR 305.1 and 305.2: one land a turn, played from hand during your main phase.
        var table = new CastingTable();
        var first = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Hand);
        var second = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Hand);

        var played = casting.PlayLand(table.State, new PlayLand(CastingTable.A, first));
        Assert.Contains(played.Events, e => e is LandPlayed);
        var land = Assert.IsType<LandPlayed>(played.Events.Last());
        Assert.Equal(ZoneKey.Of(CardZone.Battlefield, CastingTable.A), played.State.Objects[land.Land].Zone);
        Assert.Equal(1, played.State.Player(CastingTable.A).LandsPlayedThisTurn);

        var refusal = casting.Check(played.State, new PlayLand(CastingTable.A, second));
        Assert.Equal("305.2", refusal?.Rule);
    }

    [Fact]
    public void A_land_is_refused_outside_a_main_phase_and_on_another_players_turn()
    {
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Hand);

        table.MoveToStep(Step.Upkeep);
        Assert.Equal("305.1", casting.Check(table.State, new PlayLand(CastingTable.A, land))?.Rule);

        var other = new CastingTable();
        var theirs = other.Add(TestFaces.Land(), CastingTable.B, CardZone.Hand);
        other.Priority(CastingTable.B);
        Assert.Equal("305.1", casting.Check(other.State, new PlayLand(CastingTable.B, theirs))?.Rule);
    }

    [Fact]
    public void A_creature_is_cast_from_hand_and_the_pool_pays_for_it()
    {
        // CR 601.2a–i: the card becomes a spell on the stack, the cost is paid, the spell is cast.
        var table = new CastingTable();
        var card = table.Add(TestFaces.Creature("{1}{G}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, 1));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card));

        var spell = Assert.IsType<SpellCast>(cast.Events.Last());
        Assert.Equal(StackZone.Key, cast.State.Objects[spell.Spell].Zone);
        Assert.Equal(spell.Spell, StackZone.Top(cast.State));
        Assert.Equal(ManaPool.Empty, cast.State.Player(CastingTable.A).Mana);
        Assert.False(cast.State.Objects.ContainsKey(card));
    }

    [Fact]
    public void The_spell_on_the_stack_carries_the_targets_modes_and_x_from_the_command()
    {
        // CR 601.2b–d: modes, targets and X are chosen as the spell is put on the stack.
        var table = new CastingTable();
        var card = table.Add(TestFaces.Sorcery("{X}"), CastingTable.A, CardZone.Hand);
        var victim = table.Add(TestFaces.Creature(), CastingTable.B, CardZone.Battlefield);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 2, 0, 0));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card, Targets: [victim], Modes: [1], X: 2));

        var spell = Assert.IsType<SpellCast>(cast.Events.Last());
        var entry = cast.State.Objects[spell.Spell].Stack;
        Assert.NotNull(entry);
        Assert.Equal(2, entry.X);
        Assert.Equal(new[] { victim }, entry.Targets);
        Assert.Equal(new[] { 1 }, entry.Modes);
    }

    [Fact]
    public void A_spell_with_no_mana_in_the_pool_is_refused_naming_601_2h()
    {
        var table = new CastingTable();
        var card = table.Add(TestFaces.Creature("{1}{G}"), CastingTable.A, CardZone.Hand);

        Assert.Equal("601.2h", casting.Check(table.State, new CastSpell(CastingTable.A, card))?.Rule);
        Assert.Equal(ManaPool.Empty, table.State.Player(CastingTable.A).Mana);
    }

    [Fact]
    public void A_sorcery_is_refused_on_the_opponents_turn_and_an_instant_is_not()
    {
        // CR 307.1 for the sorcery; CR 304.1 for the instant, which needs only priority.
        var table = new CastingTable();
        var sorcery = table.Add(TestFaces.Sorcery("{1}"), CastingTable.B, CardZone.Hand);
        var instant = table.Add(TestFaces.Instant("{1}"), CastingTable.B, CardZone.Hand);
        table.AddMana(CastingTable.B, new ManaPool(0, 0, 0, 0, 0, 2));
        table.Priority(CastingTable.B);

        Assert.Equal("307.1", casting.Check(table.State, new CastSpell(CastingTable.B, sorcery))?.Rule);
        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.B, instant)));
    }

    [Fact]
    public void A_creature_without_flash_is_refused_while_something_is_on_the_stack()
    {
        // CR 302.1: a creature is cast during a main phase with an empty stack; flash lifts that, CR 702.8b.
        var table = new CastingTable();
        var first = table.Add(TestFaces.Creature("{1}"), CastingTable.A, CardZone.Hand);
        var second = table.Add(TestFaces.Creature("{1}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 4));
        table.Apply(casting.Cast(table.State, new CastSpell(CastingTable.A, first)).Events);

        Assert.Equal("302.1", casting.Check(table.State, new CastSpell(CastingTable.A, second))?.Rule);

        var withFlash = table.Add(TestFaces.Creature("{1}", 2, 2, new KeywordAbility(KeywordKind.Flash)), CastingTable.A, CardZone.Hand);
        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, withFlash)));
    }

    [Fact]
    public void A_permanent_spell_resolves_onto_the_battlefield_summoning_sick()
    {
        // CR 608.3 and 302.6: the permanent enters under its controller's control, summoning sick.
        var table = new CastingTable();
        var card = table.Add(TestFaces.Creature("{1}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));
        table.Apply(casting.Cast(table.State, new CastSpell(CastingTable.A, card)).Events);

        var resolved = casting.ResolveTop(table.State);

        Assert.Contains(resolved.Events, e => e is StackObjectResolved);
        Assert.True(StackZone.IsEmpty(resolved.State));
        var permanent = resolved.State.Objects.Values.Single(o => o.Zone == ZoneKey.Of(CardZone.Battlefield, CastingTable.A));
        Assert.True(permanent.SummoningSick);
        Assert.Empty(interpreter.Resolved);
    }

    [Fact]
    public void A_sorcery_resolves_through_the_interpreter_and_goes_to_the_graveyard()
    {
        // CR 608.2c and 608.2m.
        var table = new CastingTable();
        var card = table.Add(TestFaces.Sorcery("{1}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));
        table.Apply(casting.Cast(table.State, new CastSpell(CastingTable.A, card)).Events);

        var resolved = casting.ResolveTop(table.State);

        Assert.Single(interpreter.Resolved);
        Assert.True(StackZone.IsEmpty(resolved.State));
        Assert.Contains(resolved.State.Objects.Values, o => o.Zone == ZoneKey.Of(CardZone.Graveyard, CastingTable.A));
    }

    [Fact]
    public void An_empty_stack_refuses_a_resolution()
    {
        var table = new CastingTable();
        var refused = Assert.Throws<RefusedException>(() => casting.ResolveTop(table.State));
        Assert.Equal("608.1", refused.Rule);
    }

    [Fact]
    public void A_mana_ability_resolves_without_using_the_stack()
    {
        // CR 605.1a and 605.3a: it needs no target, adds mana, and does not use the stack.
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land("{G}"), CastingTable.A, CardZone.Battlefield, summoningSick: false);

        var activated = casting.Activate(table.State, new ActivateAbility(CastingTable.A, land, 0));

        Assert.True(StackZone.IsEmpty(activated.State));
        Assert.Equal(new ManaPool(0, 0, 0, 0, 1, 0), activated.State.Player(CastingTable.A).Mana);
        Assert.True(activated.State.Objects[land].Tapped);
        Assert.Contains(activated.Events, e => e is AbilityActivated);
        Assert.Contains(activated.Events, e => e is ManaChanged);
    }

    [Fact]
    public void A_mana_ability_of_any_color_takes_the_color_from_the_command()
    {
        // CR 608.2d: the choice is announced with the activation, so no payment is suspended.
        var table = new CastingTable();
        var rock = table.Add(
            TestFaces.Artifact("{2}", new ActivatedAbility([new TapSelf()], new Cards.AddMana(new ManaProduction(AnyColor: true)))),
            CastingTable.A,
            CardZone.Battlefield,
            summoningSick: false);

        var activated = casting.Activate(table.State, new ActivateAbility(CastingTable.A, rock, 0, CostChoices: ["U"]));
        Assert.Equal(new ManaPool(0, 1, 0, 0, 0, 0), activated.State.Player(CastingTable.A).Mana);

        var refusal = casting.Check(table.State, new ActivateAbility(CastingTable.A, rock, 0));
        Assert.Equal("608.2d", refusal?.Rule);
    }

    [Fact]
    public void A_tap_cost_is_refused_when_the_permanent_is_already_tapped()
    {
        // CR 118.4: an unpayable cost can't be paid.
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Battlefield, tapped: true, summoningSick: false);

        Assert.Equal("118.4", casting.Check(table.State, new ActivateAbility(CastingTable.A, land, 0))?.Rule);
    }

    [Fact]
    public void A_creatures_tap_ability_is_refused_while_it_is_summoning_sick_unless_it_has_haste()
    {
        // CR 302.6, with haste lifting it, CR 702.10b.
        var table = new CastingTable();
        var sick = table.Add(
            TestFaces.Creature("{1}", 1, 1, new ActivatedAbility([new TapSelf()], new Cards.AddMana(new ManaProduction(Fixed: "{G}")))),
            CastingTable.A,
            CardZone.Battlefield);
        Assert.Equal("302.6", casting.Check(table.State, new ActivateAbility(CastingTable.A, sick, 0))?.Rule);

        var hasty = table.Add(
            TestFaces.Creature("{1}", 1, 1, new ActivatedAbility([new TapSelf()], new Cards.AddMana(new ManaProduction(Fixed: "{G}"))), new KeywordAbility(KeywordKind.Haste)),
            CastingTable.A,
            CardZone.Battlefield);
        Assert.Null(casting.Check(table.State, new ActivateAbility(CastingTable.A, hasty, 0)));
    }

    [Fact]
    public void A_non_mana_ability_goes_on_the_stack_and_resolves_off_it()
    {
        // CR 602.2a and 608.2m: the ability is a stack object that ceases to exist as it resolves.
        var table = new CastingTable();
        var engine = table.Add(
            TestFaces.Artifact("{2}", new ActivatedAbility([new TapSelf(), new PayLife(new Fixed(2))], new Draw(new Fixed(1)))),
            CastingTable.A,
            CardZone.Battlefield,
            summoningSick: false);

        var activated = casting.Activate(table.State, new ActivateAbility(CastingTable.A, engine, 0));
        Assert.Equal(18, activated.State.Player(CastingTable.A).Life);
        var onStack = StackZone.Top(activated.State);
        Assert.NotNull(onStack);
        Assert.Equal(engine, activated.State.Objects[onStack.Value].Stack?.Source);

        var resolved = casting.ResolveTop(activated.State);
        Assert.Equal(new[] { onStack.Value }, interpreter.Resolved);
        Assert.True(StackZone.IsEmpty(resolved.State));
        Assert.False(resolved.State.Objects.ContainsKey(onStack.Value));
    }

    [Fact]
    public void A_cost_this_slice_cannot_pay_is_refused_rather_than_ignored()
    {
        var table = new CastingTable();
        var engine = table.Add(
            TestFaces.Artifact("{2}", new ActivatedAbility([new DiscardHand()], new Draw(new Fixed(1)))),
            CastingTable.A,
            CardZone.Battlefield,
            summoningSick: false);

        Assert.Equal("601.2h", casting.Check(table.State, new ActivateAbility(CastingTable.A, engine, 0))?.Rule);
    }

    [Fact]
    public void Mana_pools_empty_at_the_end_of_a_step()
    {
        // CR 106.4. The turn system calls this; the events are what the log keeps.
        var table = new CastingTable();
        table.AddMana(CastingTable.A, new ManaPool(1, 0, 0, 0, 2, 0));

        var emptied = casting.EmptyPools(table.State);

        Assert.Single(emptied.Events);
        Assert.IsType<ManaPoolEmptied>(emptied.Events[0]);
        Assert.Equal(ManaPool.Empty, emptied.State.Player(CastingTable.A).Mana);
        Assert.Empty(casting.EmptyPools(emptied.State).Events);
    }

    [Fact]
    public void An_ability_is_refused_without_priority_and_when_the_index_is_not_an_ability()
    {
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Battlefield, summoningSick: false);

        Assert.Equal("602.1", casting.Check(table.State, new ActivateAbility(CastingTable.A, land, 3))?.Rule);
        table.Priority(CastingTable.B);
        Assert.Equal("602.2a", casting.Check(table.State, new ActivateAbility(CastingTable.A, land, 0))?.Rule);
    }
}
