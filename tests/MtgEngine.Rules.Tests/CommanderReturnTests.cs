using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.Sba;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

public sealed class CommanderReturnTests
{
    [Fact]
    public void Four_simultaneously_dying_commanders_offer_each_owner_a_choice_in_turn_order()
    {
        var definition = new CardDefinition("leader", "Leader", Layout.Normal,
            [new Face("Leader", FaceRole.Main, new TypeLine(CardType.Creature), Power: new PrintedStat("2"), Toughness: new PrintedStat("2"))]);
        var log = ImmutableList.Create<GameEvent>(new GameStarted(
            [new PlayerStart("A", [], []), new PlayerStart("B", [], []), new PlayerStart("C", [], []), new PlayerStart("D", [], [])],
            1, 40, "commander", StartingPlayer: 2));
        for (var seat = 0; seat < 4; seat++)
            log = log.Add(new ObjectCreated(new GameObject(new ObjectId(seat + 1), definition, 0,
                new PlayerId(seat), new PlayerId((seat + 1) % 4), ZoneKey.Shared(Zone.Battlefield), seat + 1)
            { IsCommander = true, Damage = 2 }, -1));
        var zones = new ZoneSystem();
        var layers = new LayerSystem();
        var sba = new StateBasedActions(zones, layers, new Query.Query(layers));
        var choices = new ChoiceSystem(zones, new Stubs.Turn());
        var outcome = sba.Check(GameReducer.Replay(log));
        log = log.AddRange(outcome.Events);
        var state = outcome.State;
        Assert.Equal(4, state.Objects.Values.Count(card => card.Zone.Zone == Zone.Graveyard));
        foreach (var seat in new[] { 2, 3, 0, 1 })
        {
            var pending = Assert.IsType<PendingChoice>(state.Pending);
            Assert.Equal(new PlayerId(seat), pending.Player);
            Assert.Equal(ChoiceKinds.CommanderReturn, pending.Kind);
            var answer = choices.Answer(state, new AnswerChoice(pending.Player, pending.Id, seat % 2 == 0 ? ["return"] : []));
            var next = sba.Check(answer.State);
            log = log.AddRange(answer.Events).AddRange(next.Events);
            state = next.State;
        }

        Assert.Null(state.Pending);
        Assert.Empty(state.CommanderReturnCandidates);
        Assert.Equal(2, state.Objects.Values.Count(card => card.Zone.Zone == Zone.Command));
        Assert.Equal(2, state.Objects.Values.Count(card => card.Zone.Zone == Zone.Graveyard));
        var replayed = GameReducer.Replay(log);
        Assert.Null(replayed.Pending);
        Assert.Empty(replayed.CommanderReturnCandidates);
        Assert.Equal(state.Objects.Keys.OrderBy(id => id.Value), replayed.Objects.Keys.OrderBy(id => id.Value));
        var vanished = new ObjectCeasedToExist(Assert.Single(outcome.State.CommanderReturnCandidates.Take(1))).Apply(outcome.State);
        Assert.All(vanished.CommanderReturnCandidates, id => Assert.True(vanished.Objects.ContainsKey(id)));
    }

    [Theory]
    [InlineData(Zone.Graveyard, true)]
    [InlineData(Zone.Graveyard, false)]
    [InlineData(Zone.Exile, true)]
    [InlineData(Zone.Exile, false)]
    public void Owner_can_return_or_keep_a_commander_without_repeating_the_question(Zone destination, bool returns)
    {
        var owner = new PlayerId(0);
        var definition = new CardDefinition("leader", "Leader", Layout.Normal,
            [new Face("Leader", FaceRole.Main, new TypeLine(CardType.Creature), Power: new PrintedStat("2"), Toughness: new PrintedStat("2"))]);
        GameEvent[] start =
        [
            new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 1, 40, "commander"),
            new ObjectCreated(new GameObject(new ObjectId(1), definition, 0, owner, new PlayerId(1), ZoneKey.Shared(Zone.Battlefield), 1)
            { IsCommander = true }, 0),
        ];
        var zones = new ZoneSystem();
        var layers = new LayerSystem();
        var sba = new StateBasedActions(zones, layers, new Query.Query(layers));
        var moved = zones.Move(GameReducer.Replay(start), new ObjectId(1), ZoneKey.Of(destination, owner));
        var checkedState = sba.Check(moved.State);
        var choice = Assert.IsType<PendingChoice>(checkedState.State.Pending);
        Assert.Equal(owner, choice.Player); // Ownership, even when another player controlled it.
        Assert.Equal(ChoiceKinds.CommanderReturn, choice.Kind);
        var choices = new ChoiceSystem(zones, new Stubs.Turn());
        Assert.Throws<RefusedException>(() => choices.Answer(checkedState.State,
            new AnswerChoice(new PlayerId(1), choice.Id, ["return"])));
        var answered = choices.Answer(checkedState.State, new AnswerChoice(owner, choice.Id, returns ? ["return"] : []));
        Assert.Null(answered.State.Pending);
        Assert.Equal(returns ? Zone.Command : destination, Assert.Single(answered.State.Objects.Values).Zone.Zone);
        Assert.Empty(sba.Check(answered.State).Events);
        var replay = GameReducer.Replay(start.Concat(moved.Events).Concat(checkedState.Events).Concat(answered.Events));
        Assert.Null(replay.Pending);
        Assert.Empty(replay.CommanderReturnCandidates);
        Assert.Equal(returns ? Zone.Command : destination, Assert.Single(replay.Objects.Values).Zone.Zone);
        if (!returns)
        {
            // A later zone change grants a fresh decision, rather than permanently suppressing it.
            var card = Assert.Single(answered.State.Objects.Values);
            var next = zones.Move(answered.State, card.Id, ZoneKey.Of(destination == Zone.Exile ? Zone.Graveyard : Zone.Exile, owner));
            Assert.NotNull(sba.Check(next.State).State.Pending);
        }
    }
}
