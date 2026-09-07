using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Two questions asked from outside an ordinary resolution, played through
/// <see cref="Game.Apply"/> on <see cref="Subsystems.Wave2"/>: a value chosen while a permanent
/// enters (CR 614.1c with 101.4), and a resolution that asks a second question after the first
/// was answered (CR 608.2 with 701.23a). Both roads were found by playing real card files; both
/// stalled the game rather than finishing, so both are asserted here through the front door.
/// </summary>
public sealed partial class InterpreterTests
{
    private static PlayerId E0 => InterpreterTestsEnteringTable.P0;

    private static PlayerId E1 => InterpreterTestsEnteringTable.P1;

    /* ---------------------------------------------------------------------------------------- *
     * CR 614.1c with 101.4 — "as this enters, choose a creature type".
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    [RuleRef("614.1c")]
    public void A_value_chosen_as_a_permanent_enters_is_asked_of_its_controller()
    {
        var table = new InterpreterTestsEnteringTable(HallDeck(), HallDeck());
        table.PassTo(1, Step.PrecombatMain);
        table.Do(new PlayLand(E0, table.InHand(E0, InterpreterTestsEnteringTable.HallName)));

        // CR 614.1c: the choice is made as the permanent enters, so the game stops on it before
        // anything else happens, and it is the permanent's controller who is asked.
        var pending = table.State.Pending;
        Assert.NotNull(pending);
        Assert.Equal(Choices.ChoiceKinds.Value, pending.Kind);
        Assert.Equal(E0, pending.Player);

        // CR 614.12 is not held to yet, and this is the seam: the choice is made *as* the object
        // arrives, so the permanent should already be on the battlefield with the question open.
        // The replacement system treats a With it has no verb for as CR 614.1a's "instead" and
        // drops the entering event, so the land never arrives at all. Asserted as it stands until
        // the replacement half lands (seam-requests/E9l.md).
        Assert.Empty(table.Battlefield(E0, InterpreterTestsEnteringTable.HallName));
        Assert.False(table.State.Objects.ContainsKey(pending.Context.Source!.Value));
    }

    [Fact]
    [RuleRef("101.4")]
    public void The_answer_to_a_choice_made_while_entering_cannot_find_the_effect_that_asked()
    {
        var table = new InterpreterTestsEnteringTable(HallDeck(), HallDeck());
        table.PassTo(1, Step.PrecombatMain);
        table.Do(new PlayLand(E0, table.InHand(E0, InterpreterTestsEnteringTable.HallName)));

        var pending = table.State.Pending!;
        var refused = table.Refuse(new AnswerChoice(pending.Player, pending.Id, ["Goblin"]));

        // CR 101.4: the walk is found again by the source the question carried, and the object that
        // asked is the one that was entering — which the dropped event means does not exist. There
        // is nothing the interpreter can look up: the source is not in the state, so neither its
        // abilities nor the ability index are reachable from here. The refusal is honest (the game
        // stays on the question rather than the front door throwing), and it stays until the
        // entering event survives its own replacement (seam-requests/E9l.md).
        Assert.Equal("101.4", refused.Rule);
        Assert.Empty(table.Battlefield(E0, InterpreterTestsEnteringTable.HallName));
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 608.2 with 701.23a — a resolution that asks twice.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    [RuleRef("608.2")]
    public void A_resolution_answered_yes_and_then_answered_again_finishes_and_leaves_the_stack()
    {
        var table = InterpreterTestsEnteringTable.WithASpellOnTheStack();
        var spell = Assert.Single(table.State.In(ZoneKey.Shared(Zone.Stack)));

        // CR 608.2: the resolution exiles the land and then offers its controller a search. Saying
        // yes asks the second question, and answering that one has to finish the resolution.
        var may = table.PassUntilQuestion();
        Assert.Equal(Choices.ChoiceKinds.May, may.Kind);
        table.Do(new AnswerChoice(may.Player, may.Id, ["yes"]));

        var search = table.State.Pending;
        Assert.NotNull(search);
        Assert.Equal("search", search.Kind);
        table.Do(new AnswerChoice(search.Player, search.Id, [search.Options[0].Id]));

        // CR 608.2m: the resolution is over, so the spell is not on the stack any more. Before the
        // fix the walk asked "you may" a second time, because the answer to the first question did
        // not travel with the second, and the spell stayed on the stack for ever.
        Assert.Null(table.State.Pending);
        Assert.DoesNotContain(spell, table.State.In(ZoneKey.Shared(Zone.Stack)));

        // CR 701.23a: a basic land was found and put onto the battlefield tapped. It is the wrong
        // player's: "its controller" is a Remembered read of the key the Target selector declared,
        // nothing recalls a target under its key, and the exiled object is a new one anyway
        // (CR 400.7), so the search fell back to the spell's controller. Asserted as it stands;
        // the reading of "its controller" is named in seam-requests/E9l.md.
        var found = table.Battlefield(E0, InterpreterTestsEnteringTable.PlainName);
        Assert.Equal(2, found.Count);
        Assert.Contains(found, id => table.State.Objects[id].Tapped);
        Assert.Empty(table.Battlefield(E1, InterpreterTestsEnteringTable.WoodName));
    }

    [Fact]
    [RuleRef("608.2")]
    public void A_resolution_answered_no_finishes_and_leaves_the_stack()
    {
        var table = InterpreterTestsEnteringTable.WithASpellOnTheStack();
        var spell = Assert.Single(table.State.In(ZoneKey.Shared(Zone.Stack)));

        var may = table.PassUntilQuestion();
        table.Do(new AnswerChoice(may.Player, may.Id, ["no"]));

        // CR 608.2: declining the "may" performs nothing, and the rest of the resolution — there is
        // none — still finishes, so the spell leaves the stack and no land was found.
        Assert.Null(table.State.Pending);
        Assert.DoesNotContain(spell, table.State.In(ZoneKey.Shared(Zone.Stack)));
        Assert.Empty(table.Battlefield(E1, InterpreterTestsEnteringTable.WoodName));
    }

    private static ImmutableList<string> HallDeck() =>
        [.. Enumerable.Repeat(InterpreterTestsEnteringTable.HallId, 30)];
}
