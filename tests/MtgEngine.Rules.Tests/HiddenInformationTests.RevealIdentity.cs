using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Library;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

public sealed partial class HiddenInformationTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Reordering_reidentifies_only_the_revealed_card(bool revealed, bool sameOrder)
    {
        var game = new Game(Subsystems.Wave2(), new InterpreterNoCards());
        var state = game.Settle(revealed ? Revealing(Start(openingHand: 0), Me) : Start(openingHand: 0)).State;
        var zone = ZoneKey.Of(MtgEngine.Cards.Zone.Library, Me);
        var before = state.In(zone);
        var order = sameOrder ? before : before.Reverse().ToImmutableList();
        var changed = new ZoneReordered(zone, order, Shuffle: sameOrder);
        var after = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write([changed])));
        if (revealed)
        {
            Assert.DoesNotContain(before[0], after.Objects.Keys);
            Assert.All(before.Skip(1), id => Assert.Contains(id, after.Objects.Keys));
            Assert.Equal(before.Count, after.In(zone).Count);
            Assert.Equal(state.Objects[before[0]].Definition, after.Objects[after.In(zone)[order.IndexOf(before[0])]].Definition);
        }
        else
            Assert.Equal(order, after.In(zone));
        var settled = game.Settle(after).State;
        Assert.Equal(after.In(zone), settled.In(zone));
        var unchanged = new ZoneReordered(zone, settled.In(zone)).Apply(settled);
        Assert.Equal(settled.In(zone), unchanged.In(zone));
    }

    [Fact]
    public void Revealing_the_same_top_card_after_an_interval_hidden_creates_a_new_object()
    {
        var game = new Game(Subsystems.Wave2(), new InterpreterNoCards());
        var state = game.Settle(Revealing(Start(openingHand: 0), Me)).State;
        var first = Project(state, Them).Players[0].RevealedTopCard!;
        var original = state.Objects[first.Id];
        original = original with { CardInstanceId = new CardInstanceId(900) };
        state = state with { Objects = state.Objects.SetItem(first.Id, original) };
        state = game.Settle(new ActiveEffectChanged(1, null).Apply(state)).State;
        Assert.Null(Project(state, Them).Players[0].RevealedTopCard);
        var before = Revealing(state, Me);
        var result = game.Settle(before);
        Assert.Single(result.Events.OfType<LibraryCardReidentified>());
        Assert.DoesNotContain(result.Events, e => e is ObjectMoved);
        state = GameReducer.Apply(before, EventLogSerializer.Read(EventLogSerializer.Write(result.Events)));
        var again = Project(state, Them).Players[0].RevealedTopCard!;
        Assert.Equal(first.OracleId, again.OracleId);
        Assert.NotEqual(first.Id, again.Id);
        Assert.Equal(original.CardInstanceId, state.Objects[again.Id].CardInstanceId);
        Assert.Equal(original.Zone, state.Objects[again.Id].Zone);
        Assert.DoesNotContain(first.Id, state.Objects.Keys);
        Assert.Empty(game.Settle(state).Events);
    }
}
