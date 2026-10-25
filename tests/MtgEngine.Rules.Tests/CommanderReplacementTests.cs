using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.Replacement;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

public sealed class CommanderReplacementTests
{
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(1, true, false)]
    [InlineData(2, false, true)]
    [InlineData(1, false, true)]
    [InlineData(1, true, true)]
    public void Answering_a_commander_move_continues_the_remaining_objects_and_following_effect(int firstCommander, bool bothCommanders, bool shuffle)
    {
        var owner = new PlayerId(0);
        var layers = new LayerSystem();
        var query = new Query.Query(layers);
        var zones = new ZoneSystem();
        var replacements = new ReplacementSystem(query, layers, new Stubs.Interpreter());
        zones.Replacements = replacements;
        var interpreter = new EffectInterpreter(query, zones, replacements, layers);
        var creature = new CardDefinition("leader", "Leader", Layout.Normal,
            [new Face("Leader", FaceRole.Main, new TypeLine(CardType.Creature))]);
        var selected = new Each(new ObjectFilter(In: Zone.Battlefield, Types: CardType.Creature));
        Effect moving = shuffle ? new Shuffle(IntoLibrary: selected) : new Move(selected, Zone.Hand);
        var body = new Sequence([moving, new GainLife(new Fixed(3))]);
        var spell = new CardDefinition("spell", "Recall", Layout.Normal,
            [new Face("Recall", FaceRole.Main, new TypeLine(CardType.Instant), Abilities: [new SpellAbility(body)])]);
        var state = GameReducer.Replay(
        [
            new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 1, 40, "commander"),
            new ObjectCreated(new GameObject(new ObjectId(1), creature, 0, shuffle && firstCommander == 2 ? new PlayerId(1) : owner, owner, ZoneKey.Shared(Zone.Battlefield), 1) { IsCommander = firstCommander == 1 || bothCommanders }, -1),
            new ObjectCreated(new GameObject(new ObjectId(2), creature, 0, owner, owner, ZoneKey.Shared(Zone.Battlefield), 2) { IsCommander = firstCommander == 2 || bothCommanders }, -1),
            new ObjectCreated(new GameObject(new ObjectId(3), spell, 0, owner, owner, ZoneKey.Shared(Zone.Stack), 3)
            { Stack = new StackEntry(null, null, [], [], [], null, false) }, -1),
        ]);
        var halted = interpreter.Resolve(state, new ObjectId(3));
        var initial = state;
        var events = halted.Events;
        var replayedPause = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
        Assert.Empty(halted.Events.OfType<ZoneReordered>());
        Assert.Equal(initial.Rng, replayedPause.Rng);
        var pending = Assert.IsType<PendingChoice>(replayedPause.Pending);
        Assert.Equal(40, halted.State.Player(owner).Life);
        Assert.Equal(firstCommander == 1 ? 2 : 1, halted.State.Objects.Values.Count(card => card.Zone.Zone == Zone.Battlefield));
        var option = Assert.Single(pending.Options, o => o.Id == "return");
        var answer = new ChoiceAnswered(pending.Id, owner, [option.Id]);
        events = events.Add(answer);
        var answered = answer.Apply(replayedPause);
        var resumed = interpreter.Resume(answered, new Resumption(pending.Kind, owner, [option], pending.Context));
        events = events.AddRange(resumed.Events);
        if (bothCommanders)
        {
            var replayedAgain = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
            var again = Assert.IsType<PendingChoice>(replayedAgain.Pending);
            Assert.Empty(events.OfType<ZoneReordered>());
            Assert.Equal(initial.Rng, replayedAgain.Rng);
            Assert.Equal(40, resumed.State.Player(owner).Life);
            var nextOption = Assert.Single(again.Options, o => o.Id == "return");
            var nextAnswer = new ChoiceAnswered(again.Id, owner, [nextOption.Id]);
            events = events.Add(nextAnswer);
            resumed = interpreter.Resume(nextAnswer.Apply(replayedAgain),
                new Resumption(again.Kind, owner, [nextOption], again.Context));
            events = events.AddRange(resumed.Events);
        }
        Assert.Null(resumed.State.Pending);
        Assert.Equal(43, resumed.State.Player(owner).Life);
        Assert.DoesNotContain(resumed.State.Objects.Values, card => card.Zone.Zone == Zone.Battlefield);
        Assert.Equal(bothCommanders ? 2 : 1, resumed.State.Objects.Values.Count(card => card.Zone.Zone == Zone.Command));
        Assert.Equal(bothCommanders ? 0 : 1, resumed.State.Objects.Values.Count(card => card.Zone.Zone == (shuffle ? Zone.Library : Zone.Hand)));
        if (shuffle)
        {
            Assert.Equal(firstCommander == 2 ? 2 : 1, resumed.Events.OfType<ZoneReordered>().Count(e => e.Shuffle));
            var lastMove = events.OfType<ObjectMoved>().Max(move => events.IndexOf(move));
            Assert.All(events.OfType<ZoneReordered>(), reordered => Assert.True(events.IndexOf(reordered) > lastMove));
            var replayed = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(events)));
            Assert.Null(replayed.Pending);
            Assert.Equal(resumed.State.Rng, replayed.Rng);
            foreach (var player in new[] { owner, new PlayerId(1) })
                Assert.Equal(resumed.State.In(ZoneKey.Of(Zone.Library, player)), replayed.In(ZoneKey.Of(Zone.Library, player)));
        }
    }

    [Theory]
    [InlineData(Zone.Hand, true)]
    [InlineData(Zone.Hand, false)]
    [InlineData(Zone.Library, true)]
    [InlineData(Zone.Library, false)]
    public void Commander_owner_replaces_or_accepts_the_destination_before_the_move(Zone destination, bool returns)
    {
        var owner = new PlayerId(0);
        var layers = new LayerSystem();
        var replacements = new ReplacementSystem(new Query.Query(layers), layers, new Stubs.Interpreter());
        var zones = new ZoneSystem { Replacements = replacements };
        var definition = new CardDefinition("leader", "Leader", Layout.Normal,
            [new Face("Leader", FaceRole.Main, new TypeLine(CardType.Creature))]);
        GameEvent[] initial =
        [
            new GameStarted([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], 1, 40, "commander"),
            new ObjectCreated(new GameObject(new ObjectId(1), definition, 0, owner, new PlayerId(1), ZoneKey.Shared(Zone.Battlefield), 1)
            { IsCommander = true }, 0),
        ];
        var proposed = zones.Move(GameReducer.Replay(initial), new ObjectId(1), ZoneKey.Of(destination, owner));
        Assert.Equal(Zone.Battlefield, proposed.State.Objects[new ObjectId(1)].Zone.Zone);
        Assert.DoesNotContain(proposed.Events, e => e is ObjectMoved);
        var pending = Assert.IsType<PendingChoice>(proposed.State.Pending);
        Assert.Equal(owner, pending.Player);
        var option = Assert.Single(pending.Options, o => o.Id == (returns ? "return" : "keep"));
        var answered = new ChoiceAnswered(pending.Id, owner, [option.Id]);
        var result = replacements.Resume(answered.Apply(proposed.State),
            new Resumption(ChoiceKinds.ReplacementOrder, owner, [option], pending.Context));
        Assert.Null(result.State.Pending);
        var card = Assert.Single(result.State.Objects.Values);
        Assert.Equal(returns ? Zone.Command : destination, card.Zone.Zone);
        Assert.True(card.IsCommander);
        Assert.Single(result.Events.OfType<ObjectMoved>());
        var replayed = GameReducer.Replay(initial.Concat(proposed.Events).Append(answered).Concat(result.Events));
        Assert.Equal(card.Zone, Assert.Single(replayed.Objects.Values).Zone);
        Assert.Null(replayed.Pending);
    }
}
