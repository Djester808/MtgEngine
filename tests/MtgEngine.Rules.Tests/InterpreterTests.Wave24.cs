using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 24: the amass keyword action (CR 701.47), which had no arm at all and refused every card
/// that printed it, and the secret simultaneous number choice (CR 700.2 with 101.4).
/// </summary>
public sealed partial class InterpreterTests
{
    /* ------------------------------------------------------------------------------------------ *
     * CR 701.47 — amass.
     * ------------------------------------------------------------------------------------------ */

    [Fact]
    [RuleRef("701.47")]
    public void Amass_creates_a_zero_zero_black_army_of_the_named_type_when_you_control_none()
    {
        // CR 701.47a: with no Army creature to add to, the keyword action creates one, and the
        // token's characteristics are the rule's — 0/0, black, that type and Army.
        var table = new InterpreterTable();
        var events = table.Run(new Amass("Orc", new Fixed(2)));

        var created = Assert.Single(events.OfType<ObjectCreated>());
        var face = created.Object.Face;
        Assert.Equal(CardType.Creature, face.Types.Types);
        Assert.Contains("Army", face.Types.SubtypeList);
        Assert.Contains("Orc", face.Types.SubtypeList);
        Assert.Equal(Color.Black, face.ColorIndicator);
        Assert.Equal(0, face.Power?.Number);
        Assert.Equal(0, face.Toughness?.Number);

        // CR 111.4: a token the rules create with no name of its own is named for its subtypes.
        Assert.Equal("Orc Army", face.Name);

        // ... and the counters go on it, because it is the Army the player now controls.
        Assert.Equal(2, table.Object(created.Object.Id).Counters[Counters.PlusOnePlusOne]);
    }

    [Fact]
    [RuleRef("701.47")]
    public void Amass_adds_the_counters_to_the_army_you_already_control_and_creates_no_token()
    {
        // CR 701.47a: the token is created only when the player controls no Army creature.
        var table = new InterpreterTable();
        var army = table.Permanent(table.A);
        Army(table, army, table.A);

        var events = table.Run(new Amass("Zombie", new Fixed(3)));

        Assert.Empty(events.OfType<ObjectCreated>());
        Assert.Equal(3, table.Object(army).Counters[Counters.PlusOnePlusOne]);
    }

    [Fact]
    [RuleRef("701.47")]
    public void Amass_asks_which_army_takes_the_counters_when_you_control_more_than_one()
    {
        // CR 701.47a: the counters go on one Army the player chooses, never on each of them.
        var table = new InterpreterTable();
        var first = table.Permanent(table.A);
        var second = table.Permanent(table.A);
        Army(table, first, table.A);
        Army(table, second, table.A);

        // On the stack, because the answer has to find the effect that asked again (CR 101.4).
        table.Resolve(table.OnStack(table.A, new Amass("Orc", new Fixed(1))));

        Assert.Equal(table.A, table.Pending.Player);
        Assert.Equal(2, table.Pending.Options.Count);
        table.Answer(second.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(1, table.Object(second).Counters[Counters.PlusOnePlusOne]);
        Assert.False(table.Object(first).Counters.ContainsKey(Counters.PlusOnePlusOne));
    }

    [Fact]
    [RuleRef("701.47")]
    public void Amass_of_zero_still_creates_the_army_and_puts_no_counters_on_it()
    {
        // CR 701.47a: the creation is not conditional on the number, and 0 counters is no event.
        var table = new InterpreterTable();
        var events = table.Run(new Amass("Orc", new Fixed(0)));

        var created = Assert.Single(events.OfType<ObjectCreated>());
        Assert.Empty(events.OfType<CountersChanged>());
        Assert.Empty(table.Object(created.Object.Id).Counters);
    }

    [Fact]
    [RuleRef("205.3m")]
    public void Amass_reads_the_printed_plural_as_the_creature_type_and_refuses_a_word_that_is_none()
    {
        // Printed amass text is plural ("amass Orcs 1") and CR 205.3m names types in the singular.
        var plural = new InterpreterTable();
        var events = plural.Run(new Amass("Orcs", new Fixed(1)));
        Assert.Contains("Orc", Assert.Single(events.OfType<ObjectCreated>()).Object.Face.Types.SubtypeList);

        // A word that is no creature type either way would make a token no filter could find.
        var nonsense = new InterpreterTable();
        var refused = Assert.Throws<RefusedException>(() => nonsense.Run(new Amass("Vorpal", new Fixed(1))));
        Assert.Equal("205.3m", refused.Rule);
    }

    /// <summary>Make that permanent an Army creature the layers report, CR 205.3m.</summary>
    private static void Army(InterpreterTable table, ObjectId id, PlayerId controller) =>
        table.Layers.Set(id, new Characteristics(
            "engine test object",
            CardType.Creature,
            Supertype.None,
            ["Army"],
            Color.Black,
            0,
            0,
            null,
            null,
            null,
            [],
            controller));

    /* ------------------------------------------------------------------------------------------ *
     * CR 700.2 with 101.4 — the secret simultaneous choice.
     * ------------------------------------------------------------------------------------------ */

    [Fact]
    [RuleRef("700.2")]
    public void Every_player_is_asked_for_a_secret_number_in_turn_order_and_none_is_read_until_the_last()
    {
        // CR 101.4: the numbers are chosen simultaneously, so nothing the effect does afterwards
        // may read one before every player has answered. The walk halts on each question, which is
        // what makes that true: the step after the choice has not run when the first answer lands.
        var table = new InterpreterTable();
        var secret = new ChooseNumberSecretly("wheel", new EachPlayer(), Max: new Fixed(4));
        var reads = new GainLife(new ChosenNumber(EffectInterpreter.SecretNumberKey("wheel", table.A)));
        table.Resolve(table.OnStack(table.A, new Sequence([secret, reads])));

        // Turn order, CR 101.4: the active player answers first.
        Assert.Equal(table.State.Turn.ActivePlayer, table.Pending.Player);
        Assert.Equal(table.A, table.Pending.Player);
        Assert.Equal(5, table.Pending.Options.Count);

        var before = table.Life(table.A);
        table.Answer("3");

        // The second seat is asked, and nothing has read the first number yet.
        Assert.Equal(table.B, table.Pending.Player);
        Assert.Equal(before, table.Life(table.A));

        table.Answer("1");
        Assert.Null(table.State.Pending);
        Assert.Equal(before + 3, table.Life(table.A));
    }

    [Fact]
    [RuleRef("700.2")]
    public void Each_secret_number_is_remembered_under_its_own_players_key()
    {
        // CR 700.2: the numbers are stored per player, which is what the highest, the lowest and
        // "each player who chose" read back. Both are readable once the last answer is in.
        var table = new InterpreterTable();
        var secret = new ChooseNumberSecretly("wheel", new EachPlayer(), Min: new Fixed(1), Max: new Fixed(6));
        var reads = new Sequence(
        [
            new GainLife(new ChosenNumber(EffectInterpreter.SecretNumberKey("wheel", table.A))),
            new GainLife(new ChosenNumber(EffectInterpreter.SecretNumberKey("wheel", table.B)), new EachOpponent()),
        ]);

        var life = (table.Life(table.A), table.Life(table.B));
        table.Resolve(table.OnStack(table.A, new Sequence([secret, reads])));

        // The bounds are the printed ones: 1 to 6, six options and no zero.
        Assert.Equal(6, table.Pending.Options.Count);
        Assert.DoesNotContain(table.Pending.Options, o => o.Id == "0");

        table.Answer("6");
        table.Answer("2");
        Assert.Equal(life.Item1 + 6, table.Life(table.A));
        Assert.Equal(life.Item2 + 2, table.Life(table.B));
    }

    [Fact]
    [RuleRef("601.2b")]
    public void A_secret_choice_with_no_printed_maximum_is_asked_through_the_numeric_channel()
    {
        // CR 601.2b with 101.4: "a number 0 or greater" has no option list to answer from, so the
        // question carries its range instead and the answer is checked against it (E4h ask 2).
        var table = new InterpreterTable();
        table.Resolve(table.OnStack(table.A, new ChooseNumberSecretly("wheel", new EachPlayer())));

        Assert.Empty(table.Pending.Options);
        Assert.Equal(new NumberRange(0, null), ChoiceNumbers.RangeOf(table.Pending));
        table.Answer("1000000");
        Assert.Equal(table.B, table.Pending.Player);
        table.Answer("3");
        Assert.Null(table.State.Pending);
    }

    [Fact]
    [RuleRef("109.5")]
    public void A_secret_choice_that_names_nobody_asks_the_controller_alone()
    {
        // CR 109.5: an omitted player reference is "you", here as everywhere else; a card that
        // means every player prints so.
        var table = new InterpreterTable();
        table.Resolve(table.OnStack(table.A, new ChooseNumberSecretly("wheel", Max: new Fixed(2))));

        Assert.Equal(table.A, table.Pending.Player);
        table.Answer("2");
        Assert.Null(table.State.Pending);
    }
}
