using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// What a resolution does about paying, CR 118.12, and what it does when the translator could not
/// say the card at all. Both end the same way when they cannot go on: a refusal that names a rule
/// and a reason, which a runner reports as a failure, rather than an exception out of the engine's
/// front door.
/// </summary>
public sealed partial class InterpreterTests
{
    /* ---------------------------------------------------------------------------------------- *
     * The marker the translator leaves behind.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void An_unsupported_effect_is_refused_by_name_with_the_reason_it_carries()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, new Unsupported("the second half of that sentence has no record"));

        var refused = Assert.Throws<RefusedException>(() => table.Resolve(spell));

        // CR 608.2: the instruction cannot be performed, and a definition that carries the marker
        // must never reach a table. The reason the translator wrote travels with the refusal, so
        // the failure names the sentence rather than only the type.
        Assert.Equal("608.2", refused.Rule);
        Assert.Contains("the second half of that sentence has no record", refused.Message, StringComparison.Ordinal);
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 118.12 — "you may pay [cost]. If you do, [effect]."
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void A_may_pay_answered_yes_pays_the_cost_and_runs_the_then_half()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, Offer(new PayLife(new Fixed(2))));

        table.Resolve(spell);
        Assert.Equal("may-pay", table.Pending.Kind);
        Assert.Equal(table.A, table.Pending.Player);
        table.Answer("yes");

        // CR 118.12: the action is a cost, paid as the ability resolves, and the "if you do" half
        // then happens. Two life out for the cost, five back for the effect.
        Assert.Equal(23, table.Life(table.A));
    }

    [Fact]
    public void A_may_pay_answered_no_runs_the_else_half_and_pays_nothing()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, Offer(new PayLife(new Fixed(2))));

        table.Resolve(spell);
        var events = table.Answer("no");

        // CR 118.12: a player who declines has not paid, so the "if you don't" half is what
        // happens and no life leaves for the cost.
        Assert.Equal(19, table.Life(table.A));
        Assert.DoesNotContain(events, e => e is LifeChanged { Reason: "cost" });
    }

    [Fact]
    public void A_may_pay_the_player_cannot_pay_runs_the_else_half()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, Offer(new PayLife(new Fixed(50))));

        table.Resolve(spell);
        table.Answer("yes");

        // CR 118.12 with CR 119.4: a player who says yes and then cannot pay has not paid — the
        // rule's own example is the sacrifice whose permanent has already gone — so the "if you
        // don't" half is what happens, and the resolution carries on rather than raising.
        Assert.Equal(19, table.Life(table.A));
    }

    [Fact]
    public void The_may_pay_question_is_answered_by_its_resumption_alone()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, Offer(new PayLife(new Fixed(2))));
        table.Resolve(spell);
        var pending = table.Pending;

        // CR 101.4: no continuation is captured anywhere. The answer, the stack object and the
        // path the question carries are everything needed, so a game rebuilt from its log resumes
        // the same way it did the first time.
        var resumed = table.Subject.Resume(
            table.State,
            new Resumption(pending.Kind, pending.Player, [new ChoiceOption("yes", "Yes")], pending.Context));

        Assert.Equal(23, resumed.State.Player(table.A).Life);
        Assert.Contains(resumed.Events, e => e is StackObjectResolved);
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 601.2c — the casting system says what the announcement still has to name.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void The_cast_cost_question_offers_exactly_what_the_casting_system_asked_for()
    {
        var table = new InterpreterTable();
        var casting = new InterpreterTestsCasting();
        table.Subject.Casting = casting;
        var wanted = table.Permanent(table.A);
        table.Permanent(table.A);
        table.Card(table.B, Zone.Hand);
        casting.Choices.Add(new CostChoice("additional cost", [wanted], 1));
        var card = table.Card(table.A, Zone.Graveyard);

        table.Resolve(table.OnStack(table.A, new MayCast(new Remembered("it")), Remembering(card)));
        table.Answer("yes");

        // CR 601.2c: what a cost consumes, where it looks and whose objects it may take is the
        // casting system's knowledge. The question offers that list and nothing the interpreter
        // worked out for itself from the printed face.
        Assert.Equal("cast-cost", table.Pending.Kind);
        Assert.Equal([wanted], table.Pending.Options.Select(o => o.Object));
        Assert.Contains("additional cost", table.Pending.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_costs_that_each_name_objects_are_asked_for_one_at_a_time()
    {
        var table = new InterpreterTable();
        var casting = new InterpreterTestsCasting();
        table.Subject.Casting = casting;
        var sacrificed = table.Permanent(table.A);
        var discarded = table.Card(table.A, Zone.Hand);
        casting.Choices.Add(new CostChoice("sacrifice", [sacrificed], 1));
        casting.Choices.Add(new CostChoice("discard", [discarded], 1));
        var card = table.Card(table.A, Zone.Graveyard);

        table.Resolve(table.OnStack(table.A, new MayCast(new Remembered("it")), Remembering(card)));
        table.Answer("yes");
        table.Answer(sacrificed.Value.ToString(CultureInfo.InvariantCulture));

        // CR 601.2c: CostObjects is one flat list read in the order the costs are written, so a
        // face carrying two object-naming costs is asked twice and what the first answer named is
        // carried on the mark — the board the second is chosen from has already moved on.
        Assert.Equal([discarded], table.Pending.Options.Select(o => o.Object));
        table.Answer(discarded.Value.ToString(CultureInfo.InvariantCulture));

        Assert.Equal([sacrificed, discarded], Assert.Single(casting.Instructed).CostObjects);
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 106.6a — a delayed ability for each mana produced.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void A_keyed_add_mana_creates_one_delayed_trigger_for_each_mana_it_produced()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, new Sequence(
        [
            new AddMana(new ManaProduction("{G}{G}"), Key: "spent"),
            new DelayedTrigger(new ManaSpent("spent"), new GainLife(new Fixed(1))),
        ]));

        table.Resolve(spell);

        // CR 106.6a: "if a replacement effect increases the amount of mana produced, a separate
        // delayed ability is created for each mana produced" — so the two mana of this instruction
        // are two abilities, each watching its own mana and not both firing on one spend.
        Assert.Equal(2, table.State.DelayedTriggers.Count);
        var watched = table.State.DelayedTriggers
            .Select(t => Assert.IsType<ManaSpent>(t.When).ManaKey)
            .Order()
            .ToList();
        Assert.Equal(2, watched.Distinct().Count());

        // Mana is not an object, so what links the two halves is the key each mana carries in the
        // pool: one block per single mana, written by the AddMana that made it.
        var pool = table.State.Player(table.A).Mana;
        Assert.Equal(2, pool.Green);
        Assert.Equal(watched, pool.Provenance.Select(p => p.Key ?? string.Empty).Order().ToList());
        Assert.All(pool.Provenance, block => Assert.Equal(1, block.Amount));
    }

    [Fact]
    public void An_unkeyed_delayed_trigger_is_created_once_however_much_mana_is_in_the_pool()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, new Sequence(
        [
            new AddMana(new ManaProduction("{G}{G}")),
            new DelayedTrigger(new ManaSpent("spent"), new GainLife(new Fixed(1))),
        ]));

        table.Resolve(spell);

        // CR 106.6: a key this resolution did not produce is left as the card wrote it — the mana
        // it watches can come from a linked ability on another permanent, so the trigger is one.
        var delayed = Assert.Single(table.State.DelayedTriggers);
        Assert.Equal("spent", Assert.IsType<ManaSpent>(delayed.When).ManaKey);
    }

    /// <summary>"You may pay [cost]. If you do, gain 5 life; if you don't, lose 1", CR 118.12.</summary>
    private static MayPay Offer(Cost cost) =>
        new(cost, new GainLife(new Fixed(5)), new LoseLife(new Fixed(1)));
}
