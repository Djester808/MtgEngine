using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;

namespace MtgEngine.Rules.Tests;

public sealed class LibrarySearchTriggerPauseTests
{
    [Fact]
    public void A_permanent_found_by_search_does_not_trigger_from_the_search_before_it_entered()
    {
        var table = new InterpreterTable();
        table.Put(table.A, ZoneKey.Of(Zone.Library, table.A), InterpreterTable.Definition(CardType.Enchantment,
            new TriggeredAbility(new Searches(new PlayerFilter(IsYou: true)), new GainLife(new Fixed(1)))));
        table.OnStack(table.A, new Search(ObjectFilter.Any, Zone.Battlefield));
        var game = new Game(Subsystems.Wave2(), new EmptyDefinitions());
        var state = GameReducer.Apply(table.State,
            [new TurnBegan(1, table.A, false), new StepBegan(Step.PrecombatMain), new PriorityGranted(table.A)]);
        foreach (var player in new[] { table.A, table.B })
        {
            var passed = Assert.IsType<Applied>(game.Apply(state, new PassPriority(player)));
            state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(passed.Events)));
        }
        var pending = Assert.IsType<PendingChoice>(state.Pending);
        var result = Assert.IsType<Applied>(game.Apply(state, new AnswerChoice(pending.Player, pending.Id,
            [Assert.Single(pending.Options).Id])));
        Assert.Single(result.State.In(ZoneKey.Shared(Zone.Battlefield)));
        Assert.Empty(result.Events.OfType<TriggerFired>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Search_trigger_survives_the_search_choice_and_fires_once_after_resolution(bool anotherChoice, bool sourceLeaves = false)
    {
        var table = new InterpreterTable();
        var watcher = table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new TriggeredAbility(new Searches(new PlayerFilter(IsYou: true)), new GainLife(new Fixed(1)))));
        table.Card(table.A, Zone.Library);
        var search = new Search(ObjectFilter.Creature, Zone.Hand);
        ImmutableList<Effect> steps = [search];
        if (sourceLeaves)
            steps = steps.Add(new Exile(new Each(ObjectFilter.Any with { In = Zone.Battlefield })));
        if (anotherChoice)
            steps = steps.Add(new May(new GainLife(new Fixed(1))));
        table.OnStack(table.A, new Sequence(steps));
        var game = new Game(Subsystems.Wave2(), new EmptyDefinitions());
        var state = GameReducer.Apply(table.State,
            [new TurnBegan(1, table.A, false), new StepBegan(Step.PrecombatMain), new PriorityGranted(table.A)]);
        foreach (var player in new[] { table.A, table.B })
        {
            var passed = Assert.IsType<Applied>(game.Apply(state, new PassPriority(player)));
            state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(passed.Events)));
            Assert.Empty(passed.Events.OfType<TriggerFired>());
        }
        var pending = Assert.IsType<PendingChoice>(state.Pending);
        Assert.Equal("search", pending.Kind);
        Assert.Single(pending.Context.DeferredTriggers);
        var cause = Assert.IsType<LibrarySearched>(pending.Context.DeferredTriggers[0].Cause);
        Assert.Equal(table.A, cause.Searcher);
        Assert.NotNull(pending.Context.DeferredTriggers[0].CauseIndex);
        Assert.Empty(game.Engine.Views.Project(state, table.A).Pending!.Context.DeferredTriggers);
        Assert.Empty(game.Engine.Views.Project(state, table.B).Pending!.Context.DeferredTriggers);
        var result = Assert.IsType<Applied>(game.Apply(state, new AnswerChoice(pending.Player, pending.Id, [])));
        if (anotherChoice)
        {
            Assert.Empty(result.Events.OfType<TriggerFired>());
            state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(result.Events)));
            pending = Assert.IsType<PendingChoice>(state.Pending);
            Assert.Equal("may", pending.Kind);
            Assert.Single(pending.Context.DeferredTriggers);
            result = Assert.IsType<Applied>(game.Apply(state, new AnswerChoice(pending.Player, pending.Id, ["no"])));
        }
        Assert.Single(result.Events.OfType<TriggerFired>());
        if (sourceLeaves)
            Assert.False(result.State.Objects.ContainsKey(watcher));
        Assert.Equal("TriggeredAbility", Assert.Single(game.Engine.Views.Project(result.State, table.A).Stack).StackKind);
        state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(result.Events)));
        Assert.Empty(game.Settle(state).Events.OfType<TriggerFired>());
        var life = state.Players.Single(p => p.Id == table.A).Life;
        foreach (var player in new[] { table.A, table.B })
        {
            var passed = Assert.IsType<Applied>(game.Apply(state, new PassPriority(player)));
            state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(passed.Events)));
            Assert.Empty(passed.Events.OfType<TriggerFired>());
        }
        Assert.Empty(game.Engine.Views.Project(state, table.A).Stack);
        Assert.Equal(life + 1, state.Players.Single(p => p.Id == table.A).Life);
    }

    private sealed class EmptyDefinitions : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => null;
        public CardDefinition? ByOracleId(string oracleId) => null;
    }
}
