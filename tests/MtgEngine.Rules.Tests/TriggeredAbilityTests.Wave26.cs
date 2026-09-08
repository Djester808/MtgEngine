using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// What the vocabulary's wave-14 pass asked the trigger system for: the life an event moved,
/// carried on the firing the way damage already is (CR 603.10), and a main-phase step reference
/// that names neither main phase (CR 505.1) — including the delayed ability that waits for one and
/// is spent at the first (CR 603.7b).
/// </summary>
public sealed partial class TriggeredAbilityTests
{
    /// <summary>The values a firing put on the stack, which is where a resolution reads them from.</summary>
    private static IReadOnlyDictionary<string, string>? Wave26Carried(Outcome outcome) =>
        Assert.Single(outcome.Events.OfType<ObjectCreated>()).Object.Stack?.Chosen;

    private static TriggeredAbilityBoard Wave26Watcher(Trigger when) =>
        Wave23Watcher(new TriggeredAbilityBoard().ActivePlayer(0), when);

    [Fact]
    [RuleRef("603.10")]
    public void A_firing_on_life_being_gained_carries_how_much_life_the_event_gave()
    {
        var board = Wave26Watcher(new GainsLife(new You()));

        // CR 603.10: "each opponent loses that much life" reads the event's own amount, so the
        // firing carries it to the stack object the resolution reads — the life total the board
        // shows by then has moved on and is not a record of the last change to it.
        var carried = Wave26Carried(board.Collect(new LifeChanged(Seat0, 3, "drain")));

        Assert.NotNull(carried);
        Assert.Equal("3", carried[TriggerSystem.TriggeringLifeGainedAmount]);
    }

    [Fact]
    [RuleRef("603.10")]
    public void A_firing_on_life_being_lost_carries_the_amount_as_a_positive_number()
    {
        var board = Wave26Watcher(new LosesLife(new You()));

        // The event's delta is negative, but "that much life" is a quantity: a reader that parsed a
        // minus sign here would make "draw that many cards" draw nothing.
        var carried = Wave26Carried(board.Collect(new LifeChanged(Seat0, -4, "edict")));

        Assert.NotNull(carried);
        Assert.Equal("4", carried[TriggerSystem.TriggeringLifeLostAmount]);
    }

    [Fact]
    [RuleRef("603.10")]
    public void A_gain_carries_no_loss_and_a_loss_carries_no_gain()
    {
        var gain = Wave26Carried(Wave26Watcher(new GainsLife(new You())).Collect(new LifeChanged(Seat0, 3, "drain")));
        var loss = Wave26Carried(Wave26Watcher(new LosesLife(new You())).Collect(new LifeChanged(Seat0, -4, "edict")));

        // The two amounts are two keys and not one signed number, so neither reader can ever answer
        // with the other event's life: a gain of 3 must not read as a loss of 3.
        Assert.NotNull(gain);
        Assert.NotNull(loss);
        Assert.DoesNotContain(TriggerSystem.TriggeringLifeLostAmount, gain);
        Assert.DoesNotContain(TriggerSystem.TriggeringLifeGainedAmount, loss);
    }

    [Fact]
    [RuleRef("603.10")]
    public void A_firing_on_damage_carries_no_life_and_a_firing_on_life_carries_no_damage()
    {
        var damaged = new TriggeredAbilityBoard().ActivePlayer(0);
        var archer = damaged.Id(damaged.Put(TriggeredAbilityBoard.Creature("archer")));
        Wave23Watcher(damaged, new DealsDamage(ObjectFilter.Creature, ToPlayer: new PlayerFilter(Opponent: true)));

        // Life lost is not damage — a cost, an edict and a burn spell all lose life, and only one of
        // them is damage — so neither amount may stand in for the other in either direction.
        var onDamage = Wave26Carried(damaged.Collect(new DamageDealt(archer, null, Seat1, 3, false, false)));
        var onLife = Wave26Carried(Wave26Watcher(new LosesLife(new You())).Collect(new LifeChanged(Seat0, -3, "edict")));

        Assert.NotNull(onDamage);
        Assert.NotNull(onLife);
        Assert.DoesNotContain(TriggerSystem.TriggeringLifeLostAmount, onDamage);
        Assert.DoesNotContain(TriggerSystem.TriggeringDamageAmount, onLife);
    }

    [Fact]
    [RuleRef("603.10")]
    public void A_life_change_of_nothing_fires_nothing_and_carries_nothing()
    {
        var board = Wave26Watcher(new GainsLife(new You()));

        // CR 119.3: gaining 0 life is not gaining life, so there is no firing to carry an amount.
        Assert.Empty(board.Collect(new LifeChanged(Seat0, 0, "nothing")).Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("505.1")]
    public void A_main_phase_reference_fires_at_the_precombat_main_phase()
    {
        var board = Wave26Watcher(new AtStep(Step.Main, new You()));

        // CR 505.1: the two main phases are "individually and collectively known as the main phase",
        // and Step.Main is the collective reading, so the first one begins it.
        var fired = Assert.Single(board.Collect(new StepBegan(Step.PrecombatMain)).Events.OfType<TriggerFired>());

        Assert.Equal(Seat0, fired.SubjectPlayer);
    }

    [Fact]
    [RuleRef("505.1")]
    public void A_main_phase_reference_fires_at_the_postcombat_main_phase_too()
    {
        var board = Wave26Watcher(new AtStep(Step.Main, new You()));

        // The half a reading loses: an ability made during combat would never see another precombat
        // main phase this turn, so admitting only that one fires at the wrong time or not at all.
        var fired = Assert.Single(board.Collect(new StepBegan(Step.PostcombatMain)).Events.OfType<TriggerFired>());

        Assert.Equal(Seat0, fired.SubjectPlayer);
    }

    [Fact]
    [RuleRef("505.1")]
    public void A_named_main_phase_still_matches_only_itself()
    {
        var board = Wave26Watcher(new AtStep(Step.PrecombatMain, new You()));

        // Only the collective reference admits both: a card that names one main phase means that one,
        // so the widened matcher must not widen PrecombatMain as well.
        Assert.Empty(board.Collect(new StepBegan(Step.PostcombatMain)).Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("505.1")]
    public void A_step_that_is_no_main_phase_fires_no_main_phase_reference()
    {
        var board = Wave26Watcher(new AtStep(Step.Main, new You()));

        Assert.Empty(board.Collect(new StepBegan(Step.Upkeep)).Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("505.1")]
    public void Your_next_main_phase_is_still_only_yours()
    {
        var board = Wave23Watcher(new TriggeredAbilityBoard().ActivePlayer(1), new AtStep(Step.Main, new You()));

        // CR 603.2 with 505.1: whose turn it is is asked the same way whichever main phase began, so
        // widening the step must not widen whose phase it is.
        Assert.Empty(board.Collect(new StepBegan(Step.PrecombatMain)).Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("603.7b")]
    public void A_delayed_ability_waiting_for_a_main_phase_fires_at_the_first_one_and_is_spent()
    {
        var board = new TriggeredAbilityBoard().ActivePlayer(0);
        var source = board.Id(board.Put(TriggeredAbilityBoard.Definition("source", CardType.Enchantment)));
        board.Fold(new DelayedTriggerCreated(new DelayedTriggerState(
            board.State.NextTimestamp,
            new AtStep(Step.Main, new You()),
            new Draw(new Fixed(1)),
            source,
            Seat0,
            Once: true,
            ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty)));

        // CR 603.7b: "at the beginning of your next main phase" is one main phase, whichever begins
        // first; the firing spends the ability, so the other one this turn finds nothing waiting.
        board.Fold(new StepBegan(Step.PrecombatMain));
        var first = board.Collect(new StepBegan(Step.PrecombatMain));
        board.Fold([.. first.Events]);
        var second = board.Collect(new StepBegan(Step.PostcombatMain));

        Assert.Single(first.Events.OfType<TriggerFired>());
        Assert.Empty(board.State.DelayedTriggers);
        Assert.Empty(second.Events.OfType<TriggerFired>());
    }

    [Fact]
    [RuleRef("505.1")]
    public void A_delayed_ability_made_during_combat_fires_at_the_postcombat_main_phase()
    {
        var board = new TriggeredAbilityBoard().ActivePlayer(0);
        var source = board.Id(board.Put(TriggeredAbilityBoard.Definition("source", CardType.Enchantment)));
        board.Fold(new StepBegan(Step.DeclareAttackers), new DelayedTriggerCreated(new DelayedTriggerState(
            board.State.NextTimestamp,
            new AtStep(Step.Main, new You()),
            new Draw(new Fixed(1)),
            source,
            Seat0,
            Once: true,
            ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty)));

        // The whole reason CR 505.1's collective reading is needed: this turn's precombat main phase
        // is over, so a reading that waited for one would hold this ability until a later turn.
        board.Fold(new StepBegan(Step.PostcombatMain));

        Assert.Single(board.Collect(new StepBegan(Step.PostcombatMain)).Events.OfType<TriggerFired>());
    }
}
