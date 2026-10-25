using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;

namespace MtgEngine.Rules.Tests;

public sealed class LibraryDisclosureLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shuffle_ends_identifiable_disclosure_before_a_later_choice(bool reveal)
    {
        var table = new InterpreterTable();
        for (var i = 0; i < 3; i++)
            table.Card(table.A, Zone.Library);
        var other = table.Card(table.B, Zone.Library);
        Effect disclosure = reveal ? new Reveal(new Top(new You(), new Fixed(1)))
            : new LookAt(new Top(new You(), new Fixed(1)), Who: new EachPlayer());
        var spell = table.OnStack(table.A, new Sequence([
            disclosure, new Shuffle(), new May(new GainLife(new Fixed(1))),
        ]));
        var initial = new LibraryInspectionChanged("unrelated",
            new State.LibraryInspection(table.B, table.B, [other])).Apply(table.State);
        var engine = Subsystems.Wave2();
        var started = engine.Interpreter.Resolve(initial, spell);
        var restored = GameReducer.Apply(initial, EventLogSerializer.Read(EventLogSerializer.Write(started.Events)));
        Assert.NotNull(restored.Pending);
        Assert.Equal("unrelated", Assert.Single(restored.LibraryInspections).Key);
        Assert.Empty(engine.Views.Project(restored, table.A).InspectedLibraryCards);
        Assert.Single(engine.Views.Project(restored, table.B).InspectedLibraryCards);
        var top = restored.In(State.ZoneKey.Of(Zone.Library, table.A))[0];
        var moved = engine.Zones.Move(restored, top, State.ZoneKey.Of(Zone.Hand, table.A));
        Assert.Null(Assert.Single(moved.Events.OfType<ObjectMoved>()).LibraryDeparture);
        Assert.NotNull(reveal ? restored.LastReveal : restored.LastLooks.GetValueOrDefault(table.A));
    }

    [Fact]
    public void Legacy_inspection_access_does_not_infer_public_reveal_provenance()
    {
        var table = new InterpreterTable();
        var card = table.Card(table.A, Zone.Library);
        var spell = table.OnStack(table.A, new Sequence([
            new Reveal(new Top(new You(), new Fixed(1))), new May(new GainLife(new Fixed(1))),
        ]));
        var engine = Subsystems.Wave2();
        var started = engine.Interpreter.Resolve(table.State, spell);
        var json = System.Text.Json.Nodes.JsonNode.Parse(EventLogSerializer.Write(started.Events))!.AsArray();
        var removed = 0;
        foreach (var item in json)
            if (item?["inspection"] is System.Text.Json.Nodes.JsonObject inspection && inspection.Remove("publicReveal"))
                removed++;
        Assert.Equal(2, removed);
        var restored = GameReducer.Apply(table.State, EventLogSerializer.Read(json.ToJsonString()));
        Assert.All(restored.LibraryInspections.Values, inspection => Assert.False(inspection.PublicReveal));
        Assert.Single(engine.Views.Project(restored, table.B).InspectedLibraryCards);
        var movement = engine.Zones.Move(restored, card, State.ZoneKey.Of(Zone.Hand, table.A));
        Assert.Null(Assert.Single(movement.Events.OfType<ObjectMoved>()).LibraryDeparture);
        var pending = restored.Pending!;
        var completed = engine.Choices.Answer(restored, new AnswerChoice(table.A, pending.Id, ["no"]));
        Assert.Empty(completed.State.LibraryInspections);
    }

    [Fact]
    public void Any_order_asks_before_changing_the_library_and_replays_the_selected_order()
    {
        var table = new InterpreterTable();
        for (var i = 0; i < 4; i++)
            table.Card(table.A, Zone.Library);
        var spell = table.OnStack(table.A, new Sequence([
            new LookAt(new Top(new You(), new Fixed(3)), RememberAs: "looked"),
            new Reorder(new Remembered("looked"), Placement.AnyOrder),
        ]));
        var engine = Subsystems.Wave2();
        var before = table.State.In(State.ZoneKey.Of(Zone.Library, table.A));
        var started = engine.Interpreter.Resolve(table.State, spell);
        var state = GameReducer.Apply(table.State, EventLogSerializer.Read(EventLogSerializer.Write(started.Events)));
        Assert.Equal("library-top-order", state.Pending?.Kind);
        Assert.Equal(before, state.In(State.ZoneKey.Of(Zone.Library, table.A)));
        Assert.Equal(3, engine.Views.Project(state, table.A).InspectedLibraryCards.Count);
        Assert.Empty(engine.Views.Project(state, table.B).InspectedLibraryCards);
        var pending = state.Pending!;
        var answered = engine.Choices.Answer(state, new AnswerChoice(table.A, pending.Id,
            pending.Options.Reverse().Select(option => option.Id).ToImmutableList()));
        state = GameReducer.Apply(state, EventLogSerializer.Read(EventLogSerializer.Write(answered.Events)));
        Assert.Equal(before.Take(3).Reverse().Concat(before.Skip(3)), state.In(State.ZoneKey.Of(Zone.Library, table.A)));
        Assert.Empty(state.LibraryInspections);
    }

    [Fact]
    public void Drawing_a_disclosed_card_removes_live_access_without_erasing_another_inspection()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        var other = table.Card(table.B, Zone.Library);
        var spell = table.OnStack(table.A, new Sequence([
            new LookAt(new Top(new You(), new Fixed(1))), new Draw(new Fixed(1)),
            new May(new GainLife(new Fixed(1))),
        ]));
        var state = new LibraryInspectionChanged("unrelated",
            new State.LibraryInspection(table.B, table.B, [other])).Apply(table.State);
        var engine = Subsystems.Wave2();
        var started = engine.Interpreter.Resolve(state, spell);
        Assert.Empty(engine.Views.Project(started.State, table.A).InspectedLibraryCards);
        Assert.Single(engine.Views.Project(started.State, table.B).InspectedLibraryCards);
        var pending = started.State.Pending!;
        var completed = engine.Choices.Answer(started.State, new AnswerChoice(table.A, pending.Id, ["no"]));
        Assert.Equal("unrelated", Assert.Single(completed.State.LibraryInspections).Key);
        Assert.Single(engine.Views.Project(completed.State, table.A).LastLook!.Cards);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Library_disclosure_is_active_through_a_choice_and_expires_when_resolution_finishes(bool reveal, bool lookAll)
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Library);
        Effect disclosure = reveal
            ? new Reveal(new Top(new You(), new Fixed(1)))
            : new LookAt(new Top(new You(), new Fixed(1)), Who: lookAll ? new EachPlayer() : new You());
        var spell = table.OnStack(table.A, new Sequence([disclosure, new May(new GainLife(new Fixed(1)))]));
        var engine = Subsystems.Wave2();
        var started = engine.Interpreter.Resolve(table.State, spell);
        var restored = GameReducer.Apply(table.State, EventLogSerializer.Read(EventLogSerializer.Write(started.Events)));
        Assert.Equal("may", restored.Pending?.Kind);
        Assert.Single(engine.Views.Project(restored, table.A).InspectedLibraryCards);
        Assert.Equal(reveal || lookAll ? 1 : 0, engine.Views.Project(restored, table.B).InspectedLibraryCards.Count);
        Assert.NotEmpty(restored.LibraryInspections);
        Assert.All(restored.LibraryInspections.Values, inspection => Assert.Equal(reveal, inspection.PublicReveal));
        var pending = restored.Pending!;
        var completed = engine.Choices.Answer(restored, new AnswerChoice(table.A, pending.Id, ["no"]));
        restored = GameReducer.Apply(restored, EventLogSerializer.Read(EventLogSerializer.Write(completed.Events)));
        Assert.Empty(restored.LibraryInspections);
        Assert.Empty(engine.Views.Project(restored, table.A).InspectedLibraryCards);
        Assert.Empty(engine.Views.Project(restored, table.B).InspectedLibraryCards);
        if (reveal)
            Assert.Single(engine.Views.Project(restored, table.B).LastReveal!.Cards);
        else
        {
            Assert.Single(engine.Views.Project(restored, table.A).LastLook!.Cards);
            Assert.Equal(lookAll, engine.Views.Project(restored, table.B).LastLook is not null);
        }
    }
}
