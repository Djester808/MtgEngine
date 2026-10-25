using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;

namespace MtgEngine.Rules.Tests;

public sealed class LibraryDisclosureIdentityTests
{
    [Theory]
    [InlineData(false, Zone.Exile)]
    [InlineData(true, Zone.Exile)]
    [InlineData(false, Zone.Hand)]
    [InlineData(true, Zone.Hand)]
    public void Reordering_a_revealed_top_preserves_the_current_disclosure_until_the_next_choice_finishes(bool publicReveal, Zone destination)
    {
        var table = new InterpreterTable();
        table.Put(table.A, State.ZoneKey.Shared(Zone.Battlefield), InterpreterTable.Definition(CardType.Enchantment,
            new StaticAbility(new PlayWithTopCardRevealed(new You()))));
        for (var i = 0; i < 4; i++)
            table.Card(table.A, Zone.Library);
        Effect disclosure = publicReveal ? new Reveal(new Top(new You(), new Fixed(3)), RememberAs: "looked")
            : new LookAt(new Top(new You(), new Fixed(3)), RememberAs: "looked");
        var spell = table.OnStack(table.A, new Sequence([
            disclosure,
            new Reorder(new Remembered("looked"), Placement.AnyOrder),
            new May(new GainLife(new Fixed(1))),
            new Move(new Remembered("looked"), destination),
        ]));
        var engine = Subsystems.Wave2();
        var started = engine.Interpreter.Resolve(table.State, spell);
        var pending = started.State.Pending!;
        var oldTop = started.State.In(State.ZoneKey.Of(Zone.Library, table.A))[0];
        var result = engine.Choices.Answer(started.State, new AnswerChoice(table.A, pending.Id,
            pending.Options.Reverse().Select(option => option.Id).ToImmutableList()));
        var restored = GameReducer.Apply(started.State, EventLogSerializer.Read(EventLogSerializer.Write(result.Events)));
        Assert.Equal("may", restored.Pending?.Kind);
        Assert.False(restored.Objects.ContainsKey(oldTop));
        Assert.Equal(3, engine.Views.Project(restored, table.A).InspectedLibraryCards.Count);
        Assert.Equal(publicReveal ? 3 : 0, engine.Views.Project(restored, table.B).InspectedLibraryCards.Count);
        Assert.All(restored.LibraryInspections.Values, inspection =>
        {
            Assert.Equal(publicReveal, inspection.PublicReveal);
            Assert.DoesNotContain(oldTop, inspection.Cards);
        });
        pending = restored.Pending!;
        result = engine.Choices.Answer(restored, new AnswerChoice(table.A, pending.Id, ["no"]));
        Assert.Empty(result.State.LibraryInspections);
        var movements = result.Events.OfType<ObjectMoved>().Where(move => move.LeftFrom?.Zone == Zone.Library).ToList();
        Assert.Equal(3, movements.Count);
        if (destination == Zone.Hand)
        {
            // The continuous reveal independently exposes the current top, even in
            // the private-Look control. Other inspected cards need explicit Reveal.
            var currentTop = restored.In(State.ZoneKey.Of(Zone.Library, table.A))[0];
            Assert.All(movements, move => Assert.Equal(publicReveal || move.From == currentTop, move.LibraryDeparture is not null));
        }
        else
            Assert.All(movements, move => Assert.Null(move.LibraryDeparture));
        Assert.Equal(3, result.State.In(destination == Zone.Hand
            ? State.ZoneKey.Of(destination, table.A) : State.ZoneKey.Shared(destination)).Count);
        Assert.Single(result.State.In(State.ZoneKey.Of(Zone.Library, table.A)));
    }
}
