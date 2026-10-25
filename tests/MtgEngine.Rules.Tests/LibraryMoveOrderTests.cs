using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

public sealed class LibraryMoveOrderTests
{
    [Theory]
    [InlineData(Placement.Top)]
    [InlineData(Placement.Bottom)]
    public void Ordered_movement_resumes_after_competing_replacements_without_reasking_or_repeating_moves(Placement placement)
    {
        var table = new InterpreterTable();
        for (var i = 0; i < 3; i++)
            table.Put(table.A, ZoneKey.Of(Zone.Hand, table.A),
                InterpreterTable.Definition(i == 1 ? CardType.Creature : CardType.Land) with { OracleId = $"card-{i}" });
        foreach (var amount in new[] { 2, 3 })
            table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
                new ReplacementAbility(new WouldBePutIntoZone(ObjectFilter.Creature with { In = Zone.Hand }, Zone.Library),
                    new ReplaceWith(new GainLife(new Fixed(amount))))
                { Text = $"Gain {amount} instead" }));
        var spell = table.OnStack(table.A, new Sequence([
            new Move(new Each(ObjectFilter.Any with { In = Zone.Hand }), Zone.Library, placement), new GainLife(new Fixed(1))]));
        var engine = Subsystems.Wave2();
        var life = table.State.Player(table.A).Life;
        var events = new List<GameEvent>();
        var result = engine.Interpreter.Resolve(table.State, spell);
        events.AddRange(result.Events);
        var state = GameReducer.Apply(table.State, EventLogSerializer.Read(EventLogSerializer.Write(result.Events)));
        var pending = Assert.IsType<PendingChoice>(state.Pending);
        result = engine.Choices.Answer(state, new AnswerChoice(table.A, pending.Id,
            [.. pending.Options.Reverse().Select(option => option.Id)]));
        events.AddRange(result.Events);
        state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(result.Events)));
        pending = Assert.IsType<PendingChoice>(state.Pending);
        Assert.Equal("replacement-order", pending.Kind);
        Assert.Equal(life, state.Player(table.A).Life);
        Assert.Single(events.OfType<ObjectMoved>());
        result = engine.Choices.Answer(state, new AnswerChoice(table.A, pending.Id,
            [pending.Options.Single(option => option.Label == "Gain 2 instead").Id]));
        events.AddRange(result.Events);
        state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(result.Events)));
        Assert.Null(state.Pending);
        Assert.Equal(life + 3, state.Player(table.A).Life);
        Assert.Equal(2, events.OfType<ObjectMoved>().Count(e => e.To.Zone == ZoneKey.Of(Zone.Library, table.A)));
        Assert.Equal(2, events.OfType<ChoiceRequested>().Count());
        Assert.Single(events.OfType<StackObjectResolved>());
        Assert.Equal(new[] { "card-2", "card-0" },
            state.In(ZoneKey.Of(Zone.Library, table.A)).Select(id => state.Objects[id].Definition.OracleId));
    }

    [Theory]
    [InlineData(Placement.Top)]
    [InlineData(Placement.Bottom)]
    public void Four_owners_choose_privately_in_turn_order_before_any_cards_move(Placement placement)
    {
        var log = new List<GameEvent>
        {
            new GameStarted([.. Enumerable.Range(0, 4).Select(i => new PlayerStart($"Seat {i}", [], []))], 42, 40, "commander"),
            new TurnBegan(1, new PlayerId(2), false), new StepBegan(Step.PrecombatMain),
        };
        for (var seat = 0; seat < 4; seat++)
            for (var index = 0; index < 3; index++)
            {
                var id = seat * 3 + index + 1;
                log.Add(new ObjectCreated(new State.GameObject(new ObjectId(id),
                    InterpreterTable.Definition(CardType.Land) with { OracleId = $"{seat}-{index}" },
                    0, new PlayerId(seat), new PlayerId(seat), State.ZoneKey.Of(Zone.Library, new PlayerId(seat)), id), -1));
            }
        log.Add(new ObjectCreated(new State.GameObject(new ObjectId(13), InterpreterTable.Definition(CardType.Instant,
            new SpellAbility(new Move(new Top(new EachPlayer(), new Fixed(2)), Zone.Library, placement))),
            0, new PlayerId(0), new PlayerId(0), State.ZoneKey.Shared(Zone.Stack), 13)
        { Stack = new State.StackEntry(null, null, [], [], [], null, false) }, -1));
        var state = GameReducer.Replay(log);
        var engine = Subsystems.Wave2();
        var result = engine.Interpreter.Resolve(state, new ObjectId(13));
        state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(result.Events)));
        foreach (var seat in new[] { 2, 3, 0, 1 })
        {
            var pending = Assert.IsType<State.PendingChoice>(state.Pending);
            Assert.Equal(new PlayerId(seat), pending.Player);
            Assert.DoesNotContain(result.Events, e => e is ObjectMoved or ZoneReordered);
            for (var viewer = 0; viewer < 4; viewer++)
            {
                var cards = engine.Views.Project(state, new PlayerId(viewer)).ChoiceCards;
                Assert.Equal(viewer == seat ? 2 : 0, cards.Count);
                Assert.Equal(Enumerable.Range(0, 3).Select(i => $"{viewer}-{i}"),
                    state.In(State.ZoneKey.Of(Zone.Library, new PlayerId(viewer))).Select(id => state.Objects[id].Definition.OracleId));
            }
            result = engine.Choices.Answer(state, new AnswerChoice(pending.Player, pending.Id,
                [.. pending.Options.Reverse().Select(option => option.Id)]));
            state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(result.Events)));
        }
        Assert.Null(state.Pending);
        for (var seat = 0; seat < 4; seat++)
            Assert.Equal((placement == Placement.Top ? new[] { 1, 0, 2 } : new[] { 2, 1, 0 }).Select(i => $"{seat}-{i}"),
                state.In(State.ZoneKey.Of(Zone.Library, new PlayerId(seat))).Select(id => state.Objects[id].Definition.OracleId));
    }

    [Theory]
    [InlineData(Placement.Bottom, false)]
    [InlineData(Placement.Top, false)]
    [InlineData(Placement.Bottom, true)]
    [InlineData(Placement.Top, true)]
    public void Owner_orders_library_placement_before_cards_move(Placement placement, bool revealedTop)
    {
        var table = new InterpreterTable();
        if (revealedTop)
            table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
                new StaticAbility(new PlayWithTopCardRevealed(new You()))));
        for (var i = 0; i < 4; i++)
            table.Put(table.A, State.ZoneKey.Of(Zone.Library, table.A),
                InterpreterTable.Definition(CardType.Land) with { OracleId = $"land-{i}" });
        var spell = table.OnStack(table.A, new Move(new Top(new You(), new Fixed(2)), Zone.Library, placement));
        var engine = Subsystems.Wave2();
        var initial = table.State.In(State.ZoneKey.Of(Zone.Library, table.A));
        var result = engine.Interpreter.Resolve(table.State, spell);
        var state = GameReducer.Apply(table.State, EventLogSerializer.Read(EventLogSerializer.Write(result.Events)));
        Assert.NotNull(state.Pending);
        Assert.Equal(2, engine.Views.Project(state, table.A).ChoiceCards.Count);
        Assert.Empty(engine.Views.Project(state, table.B).ChoiceCards);
        Assert.Equal(initial, state.In(State.ZoneKey.Of(Zone.Library, table.A)));
        var pending = state.Pending!;
        Assert.Equal(table.A, pending.Player);
        result = engine.Choices.Answer(state, new AnswerChoice(table.A, pending.Id,
            pending.Options.Reverse().Select(option => option.Id).ToImmutableList()));
        state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(result.Events)));
        var expected = placement == Placement.Bottom ? new[] { "land-2", "land-3", "land-1", "land-0" }
            : ["land-1", "land-0", "land-2", "land-3"];
        Assert.Equal(expected, state.In(State.ZoneKey.Of(Zone.Library, table.A)).Select(id => state.Objects[id].Definition.OracleId));
        if (!revealedTop)
            Assert.Equal(initial.OrderBy(id => id.Value), state.In(State.ZoneKey.Of(Zone.Library, table.A)).OrderBy(id => id.Value));
        Assert.DoesNotContain(result.Events.OfType<ObjectMoved>(), e => e.To.Zone.Zone == Zone.Library);
        Assert.Null(state.Pending);
    }
}
