using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;

namespace MtgEngine.Rules.Tests;

public sealed class LibraryReplacementDisclosureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 1, true)]
    [InlineData(true, 1, true)]
    [InlineData(false, 2, true, true)]
    [InlineData(true, 2, true, true)]
    public void Draw_replacement_inspection_survives_replay_and_expires_after_its_choice(bool onStack, int count = 1, bool secondChoice = false, bool competing = false)
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Library);
        ImmutableList<Effect> steps = [new LookAt(new Top(new You(), new Fixed(2))), new May(new GainLife(new Fixed(1)))];
        if (secondChoice)
            steps = steps.Add(new May(new GainLife(new Fixed(1))));
        table.Put(table.A, State.ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new ReplacementAbility(new WouldDraw(new You()), new ReplaceWith(new Sequence(steps))) { Text = "Inspect instead" }));
        if (competing)
            table.Put(table.A, State.ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
                new ReplacementAbility(new WouldDraw(new You()), new ReplaceWith(new GainLife(new Fixed(10))))
                { Text = "Gain ten instead" }));
        var spell = table.OnStack(table.A, new Sequence([new Draw(new Fixed(count)), new GainLife(new Fixed(5))]));
        var engine = Subsystems.Wave2();
        var started = onStack ? engine.Interpreter.Resolve(table.State, spell) : engine.Zones.Draw(table.State, table.A, count);
        var state = GameReducer.Apply(table.State, EventLogSerializer.Read(EventLogSerializer.Write(started.Events)));
        var decisions = count * (secondChoice ? 2 : 1);
        for (var index = 0; index < decisions; index++)
        {
            if (competing && index % (secondChoice ? 2 : 1) == 0)
            {
                Assert.Equal("replacement-order", state.Pending?.Kind);
                var order = state.Pending!;
                var selected = engine.Choices.Answer(state, new AnswerChoice(table.A, order.Id,
                    [order.Options.Single(option => option.Label == "Inspect instead").Id]));
                state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(selected.Events)));
            }
            Assert.Equal("may", state.Pending?.Kind);
            Assert.Equal(2, engine.Views.Project(state, table.A).InspectedLibraryCards.Count);
            Assert.Empty(engine.Views.Project(state, table.B).InspectedLibraryCards);
            Assert.Null(engine.Views.Project(state, table.A).Pending!.Context.ReplacementContinuation);
            Assert.Null(engine.Views.Project(state, table.B).Pending!.Context.ReplacementContinuation);
            var pending = state.Pending!;
            var answered = engine.Choices.Answer(state, new AnswerChoice(table.A, pending.Id, ["yes"]));
            state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(answered.Events)));
        }
        Assert.Null(state.Pending);
        Assert.Empty(state.LibraryInspections);
        Assert.Equal(table.State.Player(table.A).Life + decisions + (onStack ? 5 : 0), state.Player(table.A).Life);
        Assert.Equal(2, state.In(State.ZoneKey.Of(Zone.Library, table.A)).Count);
        Assert.Empty(state.In(State.ZoneKey.Of(Zone.Hand, table.A)));
        Assert.Equal(0, state.Player(table.A).ObjectsDrawnThisTurn);
    }
}
