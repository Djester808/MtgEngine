using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 23: the monarch the state has nowhere to write, a change of targets that can name a player,
/// the excess of a damage event (CR 120.4a) and the mandatory "if you do" (CR 608.2c).
/// </summary>
public sealed partial class InterpreterTests
{
    /* ------------------------------------------------------------------------------------------ *
     * CR 724.1 — the monarch.
     * ------------------------------------------------------------------------------------------ */

    [Fact]
    [RuleRef("115.7a")]
    public void An_any_target_slot_offers_every_legal_object_and_every_player()
    {
        // CR 115.4: "any target" is a creature, a planeswalker or a battle, or a player. The slot
        // has no filter of its own, and the arm used to read that as "no candidates at all", so a
        // burn spell could not be retargeted.
        var table = new InterpreterTable();
        var creature = table.Permanent(table.B);
        var land = table.Permanent(table.A, CardType.Land, power: null, toughness: null);
        var bolt = table.OnStack(
            table.A,
            new DealDamage(new Fixed(3), new AnyTarget()),
            new StackEntry(null, null, [creature], [], [], null, false));

        table.Run(new ChangeTarget(new Each(new ObjectFilter(In: Zone.Stack))), table.Context(bolt));

        var offered = table.Pending.Options;
        Assert.Contains(offered, o => o.Object == creature);
        Assert.DoesNotContain(offered, o => o.Object == land);

        // Both seats, and each one carries the player it stands for so the answer needs no parsing.
        Assert.Equal(2, offered.Count(o => o.Player is not null));
        Assert.Contains(offered, o => o.Player == table.A);
        Assert.Contains(offered, o => o.Player == table.B);
    }

    [Fact]
    [RuleRef("115.7")]
    public void A_player_named_as_the_new_target_is_written_on_the_stack_entry()
    {
        // CR 115.1: a player is a target like any other, so the change writes the seat and drops
        // the object it used to point at.
        var table = new InterpreterTable();
        var creature = table.Permanent(table.B);
        var bolt = table.OnStack(
            table.A,
            new DealDamage(new Fixed(3), new AnyTarget()),
            new StackEntry(null, null, [creature], [], [], null, false));

        // The change resolves off the stack itself, so the answer comes back to this very ability.
        var twist = table.OnStack(table.A, new ChangeTarget(new Each(new ObjectFilter(In: Zone.Stack, Other: true))));
        table.Resolve(twist);
        var events = table.Answer("player:1");

        var changed = Assert.Single(events.OfType<StackTargetsChanged>());
        Assert.Equal(table.B, Assert.Single(changed.TargetPlayers));
        Assert.Empty(changed.Targets);
        Assert.Equal(table.B, Assert.Single(table.Object(bolt).Stack!.TargetPlayers));
        Assert.Empty(table.Object(bolt).Stack!.Targets);
    }

    [Fact]
    [RuleRef("115.7a")]
    public void A_spell_whose_only_target_is_a_player_is_counted_and_offered_seats()
    {
        // The count used to read entry.Targets alone, so a spell that had targeted only a player
        // asked for nothing and changed nothing. CR 115.7a: as many targets as the spell has.
        var table = new InterpreterTable();
        var spell = table.OnStack(
            table.A,
            new Cards.Draw(new Fixed(1), new TargetPlayer(PlayerFilter.Any)),
            new StackEntry(null, null, [], [table.A], [], null, false));

        var twist = table.OnStack(table.A, new ChangeTarget(new Each(new ObjectFilter(In: Zone.Stack, Other: true))));
        table.Resolve(twist);

        Assert.Equal(1, table.Pending.Min);
        Assert.Equal(1, table.Pending.Max);
        Assert.All(table.Pending.Options, o => Assert.NotNull(o.Player));

        var events = table.Answer("player:1");
        Assert.Equal(table.B, Assert.Single(Assert.Single(events.OfType<StackTargetsChanged>()).TargetPlayers));
    }

    [Fact]
    [RuleRef("115.6")]
    public void A_player_who_cannot_be_targeted_is_never_offered_as_a_new_target()
    {
        // CR 115.6 with 702.16j: the candidates are read exactly as the resolution's own re-check
        // reads them, so nothing is offered that could not then resolve.
        var table = new InterpreterTable();
        table.Layers.Restrict(table.B, PlayerLimits.None with { ProtectionFromEverything = true });
        var creature = table.Permanent(table.B);
        var bolt = table.OnStack(
            table.A,
            new DealDamage(new Fixed(3), new AnyTarget()),
            new StackEntry(null, null, [creature], [], [], null, false));

        table.Run(new ChangeTarget(new Each(new ObjectFilter(In: Zone.Stack))), table.Context(bolt));

        Assert.DoesNotContain(table.Pending.Options, o => o.Player == table.B);
        Assert.Contains(table.Pending.Options, o => o.Player == table.A);
    }

    /* ------------------------------------------------------------------------------------------ *
     * CR 120.4a — excess damage.
     * ------------------------------------------------------------------------------------------ */

    [Fact]
    [RuleRef("120.4a")]
    public void Excess_damage_goes_where_the_effect_names_and_lethal_stays_on_the_creature()
    {
        // "Deals damage equal to its power to target creature. If it has trample, excess damage is
        // dealt to that creature's controller." A 5-power source into a 2/2: two are lethal, three
        // are excess, and the creature is dealt two — not five and not zero.
        var table = new InterpreterTable();
        var victim = table.Permanent(table.B, toughness: 2);
        var source = table.Permanent(table.A, power: 5);

        var events = table.Run(
            new DealDamage(new Fixed(5), new Each(new ObjectFilter(Types: CardType.Creature, ControlledBy: new EachOpponent())), ExcessTo: new ControllerOf(new Target(new ObjectFilter()))),
            table.Context(source) with { Targets = [victim] });

        var toCreature = Assert.Single(events.OfType<DamageDealt>(), d => d.ToObject == victim);
        Assert.Equal(2, toCreature.Amount);
        var toPlayer = Assert.Single(events.OfType<DamageDealt>(), d => d.ToPlayer is not null);
        Assert.Equal(3, toPlayer.Amount);
        Assert.Equal(table.B, toPlayer.ToPlayer);
        Assert.Equal(17, table.Life(table.B));
    }

    [Fact]
    [RuleRef("120.4a")]
    public void The_excess_is_read_from_the_damage_already_marked_on_the_creature()
    {
        // CR 120.6: what would be lethal is the toughness less the damage already marked, so a 4/4
        // with three marked has one point of lethal left and the rest is excess.
        var table = new InterpreterTable();
        var victim = table.Permanent(table.B, toughness: 4);
        table.Mark(victim, 3);
        var source = table.Permanent(table.A, power: 3);

        var events = table.Run(
            new DealDamage(new Fixed(3), new Each(new ObjectFilter(Types: CardType.Creature, ControlledBy: new EachOpponent())), ExcessTo: new ControllerOf(new Target(new ObjectFilter()))),
            table.Context(source) with { Targets = [victim] });

        Assert.Equal(1, Assert.Single(events.OfType<DamageDealt>(), d => d.ToObject == victim).Amount);
        Assert.Equal(2, Assert.Single(events.OfType<DamageDealt>(), d => d.ToPlayer is not null).Amount);
    }

    [Fact]
    [RuleRef("120.4a")]
    public void No_excess_is_redirected_while_the_condition_the_card_gates_it_on_is_false()
    {
        // The gate is read as the damage is dealt (CR 608.2), and a source without trample deals
        // the whole amount to the creature: excess damage is a thing this effect does not say.
        var table = new InterpreterTable();
        var victim = table.Permanent(table.B, toughness: 2);
        var source = table.Permanent(table.A, power: 5);

        var events = table.Run(
            new DealDamage(
                new Fixed(5),
                new Each(new ObjectFilter(Types: CardType.Creature, ControlledBy: new EachOpponent())),
                ExcessTo: new ControllerOf(new Target(new ObjectFilter())),
                ExcessIf: new HasKeyword(new Self(), KeywordKind.Trample)),
            table.Context(source) with { Targets = [victim] });

        Assert.Equal(5, Assert.Single(events.OfType<DamageDealt>()).Amount);
        Assert.Equal(20, table.Life(table.B));
    }

    [Fact]
    [RuleRef("120.4a")]
    public void The_excess_is_redirected_once_the_condition_holds()
    {
        var table = new InterpreterTable();
        var victim = table.Permanent(table.B, toughness: 2);
        var source = table.Permanent(table.A, power: 5, keywords: [KeywordKind.Trample]);

        var events = table.Run(
            new DealDamage(
                new Fixed(5),
                new Each(new ObjectFilter(Types: CardType.Creature, ControlledBy: new EachOpponent())),
                ExcessTo: new ControllerOf(new Target(new ObjectFilter())),
                ExcessIf: new HasKeyword(new Self(), KeywordKind.Trample)),
            table.Context(source) with { Targets = [victim] });

        Assert.Equal(2, Assert.Single(events.OfType<DamageDealt>(), d => d.ToObject == victim).Amount);
        Assert.Equal(3, Assert.Single(events.OfType<DamageDealt>(), d => d.ToPlayer is not null).Amount);
    }

    [Fact]
    [RuleRef("702.2c")]
    public void A_deathtouch_source_makes_every_point_over_one_excess()
    {
        // CR 702.2c with 120.4a: one point of a deathtouch source's damage is lethal, so a 5-power
        // deathtouch creature leaves four points of excess whatever the victim's toughness is.
        var table = new InterpreterTable();
        var victim = table.Permanent(table.B, toughness: 4);
        var source = table.Permanent(table.A, power: 5, keywords: [KeywordKind.Deathtouch]);

        var events = table.Run(
            new DealDamage(new Fixed(5), new Each(new ObjectFilter(Types: CardType.Creature, ControlledBy: new EachOpponent())), ExcessTo: new ControllerOf(new Target(new ObjectFilter()))),
            table.Context(source) with { Targets = [victim] });

        Assert.Equal(1, Assert.Single(events.OfType<DamageDealt>(), d => d.ToObject == victim).Amount);
        Assert.Equal(4, Assert.Single(events.OfType<DamageDealt>(), d => d.ToPlayer is not null).Amount);
    }

    /* ------------------------------------------------------------------------------------------ *
     * CR 608.2c and 118.12 — the mandatory "if you do".
     * ------------------------------------------------------------------------------------------ */

    [Fact]
    [RuleRef("608.2c")]
    public void The_then_half_of_a_paid_cost_runs_once_the_cost_was_paid()
    {
        // CR 608.2c: "Pay 3 life. If you do, draw a card" is one instruction. The cost is paid, so
        // the second half happens — and it is a child of the payment, not the next step of a
        // sequence, so nothing runs it twice.
        var table = new InterpreterTable();
        var source = table.Permanent(table.A);

        table.Run(
            new PayCost(new PayLife(new Fixed(3)), Then: new GainLife(new Fixed(10))),
            table.Context(source));

        Assert.Equal(27, table.Life(table.A));
    }

    [Fact]
    [RuleRef("118.12")]
    public void The_then_half_is_skipped_when_the_cost_could_not_be_paid()
    {
        // CR 118.12: an unpayable cost is a cost that was not paid, so the "if you do" half does
        // not run — and the resolution carries on rather than crashing. Nothing this player
        // controls can be sacrificed but the source itself is not a creature, so the cost is unpayable.
        var table = new InterpreterTable();
        var source = table.Permanent(table.A, CardType.Enchantment, power: null, toughness: null);

        var events = table.Run(
            new Sequence(
            [
                new PayCost(new SacrificeCost(new ObjectFilter(Types: CardType.Creature)), Then: new GainLife(new Fixed(10))),
                new GainLife(new Fixed(1)),
            ]),
            table.Context(source));

        Assert.Equal(21, table.Life(table.A));
        Assert.DoesNotContain(events, e => e is LifeChanged { Delta: 10 });
    }
}
