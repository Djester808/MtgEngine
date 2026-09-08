using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// What the vocabulary added for the trigger system to read: the draw a player's own draw step
/// spares (CR 121.1 with CR 504.1), the declaration of attackers as one event (CR 508.1), and the
/// damage a firing carries for its own resolution to read (CR 603.10).
/// </summary>
public sealed partial class TriggeredAbilityTests
{
    /// <summary>The watcher that fires on every draw an opponent takes but the first of their draw step.</summary>
    private static TriggeredAbilityBoard Wave23DrawWatcher(TriggeredAbilityBoard board) =>
        Wave23Watcher(board, new Draws(new EachOpponent(), ExceptFirstInDrawStep: true));

    private static TriggeredAbilityBoard Wave23Watcher(TriggeredAbilityBoard board, Trigger when)
    {
        board.Put(TriggeredAbilityBoard.Definition("watcher", CardType.Enchantment, [Ability(when)]));
        return board;
    }

    /// <summary>A card in the opponent's hand for a draw event to name, and the batch that draws it.</summary>
    private static ObjectId Wave23Card(TriggeredAbilityBoard board, string name) =>
        board.Put(TriggeredAbilityBoard.Definition(name, CardType.Instant), controller: 1, zone: Zone.Hand).Object.Id;

    private static IReadOnlyList<TriggerFired> Wave23Batch(TriggeredAbilityBoard board, params GameEvent[] batch)
    {
        board.Fold(batch);
        return [.. board.Collect(batch).Events.OfType<TriggerFired>()];
    }

    [Fact]
    [RuleRef("121.1")]
    public void The_first_card_a_player_draws_in_their_own_draw_step_is_spared_and_a_second_one_fires()
    {
        var board = Wave23DrawWatcher(new TriggeredAbilityBoard()).ActivePlayer(1);
        var first = Wave23Card(board, "first");
        var second = Wave23Card(board, "second");

        // CR 504.1: the turn-based draw is the first card of that step, so the exception spares it;
        // a second draw taken as the same draw step's first action is not spared.
        var fired = Wave23Batch(board, new StepBegan(Step.Draw), new ObjectDrawn(new PlayerId(1), first), new ObjectDrawn(new PlayerId(1), second));

        var one = Assert.Single(fired);
        Assert.Equal(second, Assert.Single(one.Subjects));
    }

    [Fact]
    [RuleRef("121.1")]
    public void The_same_draw_fires_when_the_trigger_does_not_spare_the_draw_step()
    {
        var board = Wave23Watcher(new TriggeredAbilityBoard(), new Draws(new EachOpponent())).ActivePlayer(1);
        var drawn = Wave23Card(board, "first");

        // Without the flag the very same batch fires on the turn-based draw, so the spared draw above
        // is the flag's doing and not the board's.
        var fired = Wave23Batch(board, new StepBegan(Step.Draw), new ObjectDrawn(new PlayerId(1), drawn));

        Assert.Equal(drawn, Assert.Single(Assert.Single(fired).Subjects));
    }

    [Fact]
    [RuleRef("121.1")]
    public void A_draw_outside_a_draw_step_is_spared_nothing()
    {
        var board = Wave23DrawWatcher(new TriggeredAbilityBoard()).ActivePlayer(1);
        var drawn = Wave23Card(board, "cantrip");

        // The exception is about a draw step, so the first card drawn in a main phase fires.
        var fired = Wave23Batch(board, new StepBegan(Step.PrecombatMain), new ObjectDrawn(new PlayerId(1), drawn));

        Assert.Equal(drawn, Assert.Single(Assert.Single(fired).Subjects));
    }

    [Fact]
    [RuleRef("121.1")]
    public void A_draw_in_another_players_draw_step_is_not_spared()
    {
        var board = Wave23DrawWatcher(new TriggeredAbilityBoard()).ActivePlayer(0);
        var drawn = Wave23Card(board, "held");

        // "In each of their draw steps": seat 1 draws during seat 0's draw step, which spares nothing.
        var fired = Wave23Batch(board, new StepBegan(Step.Draw), new ObjectDrawn(new PlayerId(1), drawn));

        Assert.Equal(drawn, Assert.Single(Assert.Single(fired).Subjects));
    }

    [Fact]
    [RuleRef("121.1")]
    public void Each_draw_step_spares_one_draw_of_its_own()
    {
        var board = Wave23DrawWatcher(new TriggeredAbilityBoard()).ActivePlayer(1);
        var first = Wave23Card(board, "first");
        var extra = Wave23Card(board, "extra");

        // The spared draw is per draw step and not per turn, so an extra draw step spares one more.
        Assert.Empty(Wave23Batch(board, new StepBegan(Step.Draw), new ObjectDrawn(new PlayerId(1), first)));
        Assert.Empty(Wave23Batch(board, new StepBegan(Step.Draw), new ObjectDrawn(new PlayerId(1), extra)));
    }

    [Fact]
    [RuleRef("508.1")]
    public void Attacking_with_creatures_fires_once_for_the_declaration_and_carries_every_attacker()
    {
        var board = Wave23Watcher(new TriggeredAbilityBoard(), new AttacksWithCreatures(new EachOpponent())).ActivePlayer(1);
        var one = board.Put(TriggeredAbilityBoard.Creature("one"), controller: 1).Object.Id;
        var two = board.Put(TriggeredAbilityBoard.Creature("two"), controller: 1).Object.Id;

        // CR 508.1: attackers are declared once, so a two-creature attack fires this once — firing per
        // creature is CR 508.2, which is what Attacks does.
        var fired = Assert.Single(Wave23Batch(board, Wave23Attack(1, (one, 0), (two, 0))));

        Assert.Equal(new PlayerId(1), fired.SubjectPlayer);
        Assert.Equal(2, fired.Subjects.Count);
        Assert.Equal(one, fired.Subjects[0]);
        Assert.Equal(two, fired.Subjects[1]);
    }

    [Fact]
    [RuleRef("508.1")]
    public void Attacking_with_creatures_reads_who_declared_and_not_who_was_attacked()
    {
        var board = Wave23Watcher(new TriggeredAbilityBoard(), new AttacksWithCreatures(new EachOpponent())).ActivePlayer(0);
        var mine = board.Put(TriggeredAbilityBoard.Creature("mine")).Object.Id;

        // The watcher's controller attacking is not an opponent attacking, so nothing fires.
        Assert.Empty(Wave23Batch(board, Wave23Attack(0, (mine, 1))));
    }

    private static AttackersDeclared Wave23Attack(int by, params (ObjectId Attacker, int Defender)[] attackers)
    {
        var declared = ImmutableDictionary<ObjectId, CombatTarget>.Empty;
        foreach (var (attacker, defender) in attackers)
            declared = declared.SetItem(attacker, new CombatTarget(new PlayerId(defender), null));
        return new AttackersDeclared(new PlayerId(by), declared);
    }

    [Fact]
    [RuleRef("603.10")]
    public void A_firing_on_damage_carries_how_much_damage_the_event_dealt()
    {
        var board = new TriggeredAbilityBoard();
        var archer = board.Put(TriggeredAbilityBoard.Creature("archer")).Object.Id;
        Wave23Watcher(board, new DealsDamage(ObjectFilter.Creature, ToPlayer: new PlayerFilter(Opponent: true)));

        // CR 603.10: "draw that many cards" reads the damage of the event that fired the ability, so
        // the firing carries it to the stack object the resolution reads.
        var outcome = board.Collect(new DamageDealt(archer, null, new PlayerId(1), 3, false, false));

        var created = Assert.Single(outcome.Events.OfType<ObjectCreated>());
        var chosen = created.Object.Stack?.Chosen;
        Assert.NotNull(chosen);
        Assert.Equal("3", chosen[TriggerSystem.TriggeringDamageAmount]);
    }

    [Fact]
    [RuleRef("603.10")]
    public void A_firing_on_anything_but_damage_carries_no_damage()
    {
        var board = new TriggeredAbilityBoard();
        Wave23Watcher(board, new EntersBattlefield(ObjectFilter.Creature));

        // Nothing is added to a firing that has no damage to report, so an ordinary trigger's stack
        // entry is what it always was.
        var outcome = board.Collect(board.Put(TriggeredAbilityBoard.Creature("beast")));

        var created = Assert.Single(outcome.Events.OfType<ObjectCreated>());
        Assert.NotNull(created.Object.Stack);
        Assert.Null(created.Object.Stack.Chosen);
    }
}
