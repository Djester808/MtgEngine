using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using SpellCast = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

public sealed class CommanderCastHistoryTests
{
    [Fact]
    public void Replay_preserves_counts_across_turns_and_new_object_ids()
    {
        var first = Card(1, 0, "leader");
        var returned = Card(2, 0, "leader");
        GameEvent[] log =
        [
            new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 42, 40, "commander"),
            new ObjectCreated(first, 0),
            new SpellCast(first.Id, first.Owner, Zone.Command),
            new TurnBegan(2, new PlayerId(1), false),
            new ObjectCreated(returned, 0),
            new SpellCast(returned.Id, returned.Owner, Zone.Command),
        ];
        var state = GameReducer.Replay(log);
        Assert.Equal(2, state.CommandZoneCasts(returned, returned.Owner));
        Assert.Single(state.History.SpellsCast);
        Assert.Equal(state.CommanderCasts, GameReducer.Replay(log).CommanderCasts);
        Assert.Equal(0, state.CommandZoneCasts(Card(3, 1, "leader"), new PlayerId(1)));
        Assert.Equal(0, state.CommandZoneCasts(Card(4, 0, "partner"), new PlayerId(0)));
        Assert.Equal(0, state.CommandZoneCasts(returned, new PlayerId(1)));
    }

    [Theory]
    [InlineData(Zone.Hand, true, 0)]
    [InlineData(Zone.Graveyard, true, 0)]
    [InlineData(Zone.Command, false, 0)]
    [InlineData(Zone.Command, true, 1)]
    public void Only_actual_commander_casts_from_command_count(Zone from, bool commander, int expected)
    {
        var card = Card(1, 0, "leader") with { IsCommander = commander };
        var state = GameReducer.Replay(
        [
            new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 42, 40, "commander"),
            new ObjectCreated(card, 0),
            new SpellCast(card.Id, card.Owner, from),
        ]);
        Assert.Equal(expected, state.CommandZoneCasts(card, card.Owner));
    }

    private static GameObject Card(int id, int owner, string oracleId) => new(
        new ObjectId(id),
        new CardDefinition(oracleId, "Leader", Layout.Normal,
            [new Face("Leader", FaceRole.Main, new TypeLine(CardType.Creature, Supertype.Legendary, []))]),
        0, new PlayerId(owner), new PlayerId(owner), ZoneKey.Shared(Zone.Stack), id)
    { IsCommander = true };
}
