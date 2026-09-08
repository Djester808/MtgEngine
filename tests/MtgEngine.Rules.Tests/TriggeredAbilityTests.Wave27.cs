using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;
using DamagePrevented = MtgEngine.Cards.DamagePrevented;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 27's three asks of the trigger system: a trigger on damage being prevented with the amount
/// it prevented (CR 615.13), the Nth spell a player casts in a turn (CR 603.2), and the payment
/// made for the triggering spell riding on the firing (CR 400.7d).
/// </summary>
public sealed partial class TriggeredAbilityTests
{
    /// <summary>
    /// A prevention effect being applied, standing in for the event <c>Replacement/**</c> is asked
    /// to emit in <c>seam-requests/E6j.md</c>. Nothing in the engine raises one yet, so the shape
    /// the matcher reads is exercised here from the same interface the real record will implement.
    /// </summary>
    private sealed record Wave27Prevention(
        ObjectId Source,
        ObjectId? ToObject,
        PlayerId? ToPlayer,
        int Prevented,
        ObjectId? PreventedBy) : GameEvent, IDamagePrevented
    {
        public override GameState Apply(GameState state) => state;
    }

    private static ObjectId Wave27Watcher(TriggeredAbilityBoard board, Trigger when) =>
        board.Put(TriggeredAbilityBoard.Definition("watcher", CardType.Enchantment, [Ability(when)])).Object.Id;

    /// <summary>The values the firing put on its stack object, which is where a resolution reads them.</summary>
    private static IReadOnlyDictionary<string, string>? Wave27Carried(Outcome outcome) =>
        Assert.Single(outcome.Events.OfType<ObjectCreated>()).Object.Stack?.Chosen;

    /// <summary>A spell on the stack whose entry records what was paid for it, CR 601.2h.</summary>
    private static ObjectId Wave27Spell(TriggeredAbilityBoard board, string name, int caster, ImmutableDictionary<Color, int>? spent = null)
    {
        var seat = new PlayerId(caster);
        var created = new ObjectCreated(
            new GameObject(
                new ObjectId(board.State.NextObjectId),
                TriggeredAbilityBoard.Definition(name, CardType.Instant),
                0,
                seat,
                seat,
                ZoneKey.Shared(Zone.Stack),
                board.State.NextTimestamp)
            {
                Stack = spent is null ? null : new StackEntry(null, null, [], [], [], null, IsCopy: false, ManaSpent: spent),
            },
            -1);
        board.Fold(created);
        return created.Object.Id;
    }

    /// <summary>Folds the cast into the turn's record the way the log does, then collects on it.</summary>
    private static Outcome Wave27Cast(TriggeredAbilityBoard board, ObjectId spell, int caster)
    {
        var cast = new Events.SpellCast(spell, new PlayerId(caster), Zone.Hand);
        board.Fold(cast);
        return board.Collect(cast);
    }

    [Fact]
    [RuleRef("615.13")]
    public void A_prevention_fires_the_trigger_and_carries_what_it_prevented()
    {
        var board = new TriggeredAbilityBoard().ActivePlayer(0);
        var burn = board.Id(board.Put(TriggeredAbilityBoard.Creature("burner"), controller: 1));
        var watcher = Wave27Watcher(board, new DamagePrevented());

        // CR 615.13: the ability triggers each time a prevention effect is applied and prevents some
        // or all of the damage, and "that much damage" is the amount prevented — 2 of the 5 that
        // would have been dealt, not the 3 that still were.
        var outcome = board.Collect(new Wave27Prevention(burn, watcher, null, 2, watcher));

        Assert.Single(outcome.Events.OfType<TriggerFired>());
        var carried = Wave27Carried(outcome);
        Assert.NotNull(carried);
        Assert.Equal("2", carried[TriggerSystem.TriggeringPreventedDamageAmount]);
    }

    [Fact]
    [RuleRef("615.13")]
    public void The_prevented_amount_is_never_read_as_damage_dealt()
    {
        var board = new TriggeredAbilityBoard().ActivePlayer(0);
        var burn = board.Id(board.Put(TriggeredAbilityBoard.Creature("burner"), controller: 1));
        var watcher = Wave27Watcher(board, new DamagePrevented());

        // Dealt and prevented are two numbers on one event, so the firing carries only the one its
        // own event is about: a reader that fell back on the damage key would deal nothing at all
        // for damage prevented in full.
        var carried = Wave27Carried(board.Collect(new Wave27Prevention(burn, watcher, null, 4, watcher)));

        Assert.NotNull(carried);
        Assert.DoesNotContain(TriggerSystem.TriggeringDamageAmount, carried);
    }

    [Fact]
    [RuleRef("615.12")]
    public void A_prevention_that_prevented_nothing_triggers_nothing()
    {
        var board = new TriggeredAbilityBoard().ActivePlayer(0);
        var burn = board.Id(board.Put(TriggeredAbilityBoard.Creature("burner"), controller: 1));
        var watcher = Wave27Watcher(board, new DamagePrevented());

        // CR 615.12: an applicable prevention effect is still applied to unpreventable damage and
        // prevents none of it. CR 615.13 asks that some damage was prevented, so nothing triggers.
        var outcome = board.Collect(new Wave27Prevention(burn, watcher, null, 0, watcher));

        Assert.Empty(outcome.Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("615.13")]
    public void This_way_watches_only_the_prevention_this_object_applied()
    {
        var board = new TriggeredAbilityBoard().ActivePlayer(0);
        var burn = board.Id(board.Put(TriggeredAbilityBoard.Creature("burner"), controller: 1));
        var elsewhere = board.Id(board.Put(TriggeredAbilityBoard.Definition("shield", CardType.Enchantment), controller: 1));
        var watcher = Wave27Watcher(board, new DamagePrevented(ThisWay: true));

        // "When damage is prevented this way" names this object's own prevention. Somebody else's
        // shield stopping damage is not this card's doing, and without the scope every card of this
        // shape in the game would fire on it.
        Assert.Single(board.Collect(new Wave27Prevention(burn, watcher, null, 3, watcher)).Events.OfType<TriggerFired>());
        Assert.Empty(board.Collect(new Wave27Prevention(burn, watcher, null, 3, elsewhere)).Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("615.13")]
    public void The_by_half_narrows_which_damage_the_prevention_was_of()
    {
        var board = new TriggeredAbilityBoard().ActivePlayer(0);
        var creature = board.Id(board.Put(TriggeredAbilityBoard.Creature("burner"), controller: 1));
        var artifact = board.Id(board.Put(TriggeredAbilityBoard.Definition("engine", CardType.Artifact), controller: 1));
        var watcher = Wave27Watcher(board, new DamagePrevented(By: ObjectFilter.Creature));

        // By is about the source of the damage the prevention stopped, so a prevention of an
        // artifact's damage is a different event and does not fire a creature-damage watcher.
        Assert.Single(board.Collect(new Wave27Prevention(creature, watcher, null, 1, watcher)).Events.OfType<TriggerFired>());
        Assert.Empty(board.Collect(new Wave27Prevention(artifact, watcher, null, 1, watcher)).Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("603.2")]
    public void The_nth_spell_trigger_fires_on_that_cast_alone()
    {
        var board = new TriggeredAbilityBoard().ActivePlayer(0);
        Wave27Watcher(board, new Cards.SpellCast(new ObjectFilter(Spell: true), new You(), Nth: 2));

        var first = Wave27Cast(board, Wave27Spell(board, "one", 0), 0);
        var second = Wave27Cast(board, Wave27Spell(board, "two", 0), 0);
        var third = Wave27Cast(board, Wave27Spell(board, "three", 0), 0);

        // CR 603.2: "their second spell each turn" is that cast and no later one. The wrong reading —
        // an ungated trigger beside a "two or more spells this turn" condition — passes the second
        // cast and then fires again on the third.
        Assert.Empty(first.Events.OfType<TriggerFired>());
        Assert.Single(second.Events.OfType<TriggerFired>());
        Assert.Empty(third.Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("603.2")]
    public void The_count_is_per_player_so_an_opponents_cast_does_not_advance_it()
    {
        var board = new TriggeredAbilityBoard().ActivePlayer(0);
        Wave27Watcher(board, new Cards.SpellCast(new ObjectFilter(Spell: true), new You(), Nth: 2));

        Wave27Cast(board, Wave27Spell(board, "theirs", 1), 1);
        var mine = Wave27Cast(board, Wave27Spell(board, "mine", 0), 0);
        var second = Wave27Cast(board, Wave27Spell(board, "second", 0), 0);

        // The count is the caster's own, so the opponent's spell is not one of yours: your first
        // cast is still your first, and your second is what the ability watches for.
        Assert.Empty(mine.Events.OfType<TriggerFired>());
        Assert.Single(second.Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("603.2")]
    public void A_trigger_with_no_nth_fires_on_every_cast()
    {
        var board = new TriggeredAbilityBoard().ActivePlayer(0);
        Wave27Watcher(board, new Cards.SpellCast(new ObjectFilter(Spell: true), new You()));

        // The field is null for "a spell", and the ungated trigger must keep firing on every cast —
        // gating it would silently stop every card that never counted.
        Assert.Single(Wave27Cast(board, Wave27Spell(board, "one", 0), 0).Events.OfType<TriggerFired>());
        Assert.Single(Wave27Cast(board, Wave27Spell(board, "two", 0), 0).Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("400.7d")]
    public void A_firing_on_a_cast_carries_what_was_spent_to_cast_it()
    {
        var board = new TriggeredAbilityBoard().ActivePlayer(0);
        Wave27Watcher(board, new Cards.SpellCast(new ObjectFilter(Spell: true), new You()));
        var paid = ImmutableDictionary<Color, int>.Empty.SetItem(Color.Red, 2).SetItem(Color.Blue, 1);

        // CR 400.7d: what was paid to cast the spell is information this ability may reference, and
        // by the time it resolves the spell may have resolved and ceased to exist (CR 400.7), so
        // the payment rides on the firing.
        var carried = Wave27Carried(Wave27Cast(board, Wave27Spell(board, "paid", 0, paid), 0));

        Assert.NotNull(carried);
        Assert.Equal("3", carried[TriggerSystem.ManaSpentOnTriggeringAmount]);
        Assert.Equal("2", carried[TriggerSystem.ManaSpentOnTriggeringColorPrefix + Color.Red]);
        Assert.Equal("1", carried[TriggerSystem.ManaSpentOnTriggeringColorPrefix + Color.Blue]);
        Assert.DoesNotContain(TriggerSystem.ManaSpentOnTriggeringColorPrefix + Color.Green, carried);
    }

    [Fact]
    [RuleRef("400.7d")]
    public void A_cast_that_cost_no_mana_carries_a_zero_rather_than_nothing()
    {
        var board = new TriggeredAbilityBoard().ActivePlayer(0);
        Wave27Watcher(board, new Cards.SpellCast(new ObjectFilter(Spell: true), new You()));

        // "If no mana was spent to cast it" is this amount compared against zero. A key that were
        // absent when nothing was paid would read the same as a firing that watched no spell at
        // all, and the commonest sentence CR 400.7d exists for could never be answered.
        var carried = Wave27Carried(Wave27Cast(board, Wave27Spell(board, "free", 0), 0));

        Assert.NotNull(carried);
        Assert.Equal("0", carried[TriggerSystem.ManaSpentOnTriggeringAmount]);
    }
}
