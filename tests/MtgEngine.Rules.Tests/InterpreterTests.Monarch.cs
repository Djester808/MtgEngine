using MtgEngine.Cards;

namespace MtgEngine.Rules.Tests;

/// <summary>CR 724: the monarch is a designation one player holds; "you become the monarch" moves it.</summary>
public sealed partial class InterpreterTests
{
    [Fact]
    [RuleRef("724.1")]
    public void Becoming_the_monarch_writes_the_designation_and_takes_it_from_the_last_holder()
    {
        var table = new InterpreterTable();
        table.Resolve(table.OnStack(table.A, new BecomeMonarch()));
        Assert.Equal(table.A, table.State.Monarch);

        // CR 724.1: there is only ever one monarch, so the next "you become the monarch" moves it.
        table.Resolve(table.OnStack(table.B, new BecomeMonarch()));
        Assert.Equal(table.B, table.State.Monarch);
    }
}
