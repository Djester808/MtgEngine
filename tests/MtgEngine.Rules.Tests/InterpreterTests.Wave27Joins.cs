using MtgEngine.Cards;

namespace MtgEngine.Rules.Tests;

/// <summary>The wave-27 join E3p asked for: an "unless" pays its cost through the casting system, in full or not at all (CR 118.6 with 601.2h).</summary>
public sealed partial class InterpreterTests
{
    private static Unless UnlessPaid() =>
        new(new GainLife(new Fixed(3), new EachOpponent()), new PayLife(new Fixed(1)), new You());

    [Fact]
    [RuleRef("118.6")]
    public void An_unless_whose_cost_cannot_be_paid_is_not_asked_and_the_unpaid_half_happens()
    {
        var table = new InterpreterTable();
        table.Subject.Casting = new InterpreterTestsCasting();
        var life = (table.Life(table.A), table.Life(table.B));

        table.Resolve(table.OnStack(table.A, UnlessPaid()));

        // CR 118.6: a player who cannot pay is not offered the payment; the effect happens.
        Assert.Null(table.State.Pending);
        Assert.Equal(life.Item1, table.Life(table.A));
        Assert.Equal(life.Item2 + 3, table.Life(table.B));
    }

    [Fact]
    [RuleRef("601.2h")]
    public void An_unless_paid_in_full_stops_the_effect_and_the_payment_is_made()
    {
        var table = new InterpreterTable();
        table.Subject.Casting = new InterpreterTestsCasting { PaymentRefusal = null };
        var life = (table.Life(table.A), table.Life(table.B));

        table.Resolve(table.OnStack(table.A, UnlessPaid()));
        Assert.Equal("unless", table.Pending.Kind);
        table.Answer("yes");

        // CR 601.2h: the cost is paid through the casting system (the fake marks it as one life) and
        // the "unless" half never runs.
        Assert.Null(table.State.Pending);
        Assert.Equal(life.Item1 - 1, table.Life(table.A));
        Assert.Equal(life.Item2, table.Life(table.B));
    }
}
