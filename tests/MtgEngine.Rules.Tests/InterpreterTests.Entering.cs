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

        // CR 614.12: the choice is made *as* the object arrives, so the permanent is already on the
        // battlefield with the question open, and the question's source is that permanent.
        var land = Assert.Single(table.Battlefield(E0, InterpreterTestsEnteringTable.HallName));
        Assert.Equal(land, pending.Context.Source);
    }

    [Fact]
    [RuleRef("101.4")]
    public void The_answer_to_a_choice_made_while_entering_finds_the_effect_that_asked_and_keeps_the_answer()
    {
        var table = new InterpreterTestsEnteringTable(HallDeck(), HallDeck());
        table.PassTo(1, Step.PrecombatMain);
        table.Do(new PlayLand(E0, table.InHand(E0, InterpreterTestsEnteringTable.HallName)));

        var pending = table.State.Pending!;
        table.Do(new AnswerChoice(pending.Player, pending.Id, ["Goblin"]));

        // CR 101.4 with 608.2d: the walk is found again by the source the question carried and the
        // index of the ability that asked, both of which the entering permanent now has (CR 614.12);
        // the answer is kept on the permanent for the abilities that read it.
        var land = Assert.Single(table.Battlefield(E0, InterpreterTestsEnteringTable.HallName));
        Assert.Null(table.State.Pending);
        Assert.Equal("Goblin", table.State.Objects[land].Chosen[InterpreterTestsEnteringTable.TypeKey]);
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

        // CR 701.23a with 608.2h and 400.7: "its controller" is the controller the exiled land had
        // (the target is remembered under the key its selector declared, and a departed object's
        // controller is last-known information), so the search was the other player's: their basic
        // land is on their battlefield, tapped, and the caster found nothing.
        var found = table.Battlefield(E1, InterpreterTestsEnteringTable.WoodName);
        var land = Assert.Single(found);
        Assert.True(table.State.Objects[land].Tapped);
        Assert.Single(table.Battlefield(E0, InterpreterTestsEnteringTable.PlainName));
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
