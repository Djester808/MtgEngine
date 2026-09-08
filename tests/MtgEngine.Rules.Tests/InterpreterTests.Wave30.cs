using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 30. The keys a paid "instead" has already applied survive the interpreter's halt (CR
/// 614.5), and a card-name choice with no printed options is offered the names the state holds
/// (CR 201.3).
/// </summary>
public class InterpreterTestsWave30
{
    /// <summary>The id every replacement effect in this file is skipped by, CR 614.5.</summary>
    private static string Key(ObjectId source, int index) => $"o{source.Value}:a{index}";

    private static CardDefinition E9yReplacer(Effect with) => new(
        "00000000-0000-0000-0000-00000000e930",
        "e9y replacer",
        Layout.Normal,
        [
            new Face(
                "e9y replacer",
                FaceRole.Main,
                new TypeLine(CardType.Artifact),
                Abilities: [new ReplacementAbility(new WouldEnterBattlefield(new ObjectFilter()), with, Where: Zone.Stack)]),
        ]);

    private static CardDefinition E9yNamed(string name, CardType types) => new(
        "00000000-0000-0000-0000-00000000e931",
        name,
        Layout.Normal,
        [new Face(name, FaceRole.Main, new TypeLine(types))]);

    /// <summary>
    /// CR 614.5: the walk is a replacement's own With, so the event it makes is offered with that
    /// effect's key and the effect can never replace what it itself produced.
    /// </summary>
    [Fact]
    public void An_instead_offers_its_own_key_with_the_event_it_makes()
    {
        var table = new InterpreterTable();
        var source = table.Put(table.A, ZoneKey.Shared(Zone.Stack), E9yReplacer(new GainLife(new Fixed(2))));

        table.RunFrom(new GainLife(new Fixed(2)), table.Context(source), 0);

        Assert.Single(table.Replacements.Used);
        Assert.Contains(Key(source, 0), table.Replacements.Used[0]);
    }

    /// <summary>
    /// CR 614.5 is about replacement effects only: a triggered ability's body replaced nothing, so
    /// the walk seeds nothing and every effect is still free to apply.
    /// </summary>
    [Fact]
    public void A_body_that_is_not_a_replacement_seeds_nothing()
    {
        var table = new InterpreterTable();
        var definition = new CardDefinition(
            "00000000-0000-0000-0000-00000000e932",
            "e9y trigger",
            Layout.Normal,
            [
                new Face(
                    "e9y trigger",
                    FaceRole.Main,
                    new TypeLine(CardType.Artifact),
                    Abilities: [new TriggeredAbility(new EntersBattlefield(new ObjectFilter()), new GainLife(new Fixed(1)))]),
            ]);
        var source = table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), definition);

        table.RunFrom(new GainLife(new Fixed(1)), table.Context(source), 0);

        Assert.Single(table.Replacements.Used);
        Assert.Empty(table.Replacements.Used[0]);
    }

    /// <summary>
    /// CR 614.5 with 101.4: the payment question stops the walk, and the key rides on the question
    /// so the resumed walk's re-proposal is not a fresh offer. Without this the effect is offered
    /// its own replacement again on every resumption and asks once per way of paying.
    /// </summary>
    [Fact]
    public void The_key_survives_the_halt_and_rides_on_the_re_proposal()
    {
        var table = new InterpreterTable();
        var with = new Sequence([new May(new GainLife(new Fixed(1))), new GainLife(new Fixed(2))]);
        var source = table.Put(table.A, ZoneKey.Shared(Zone.Stack), E9yReplacer(with));

        table.RunFrom(with, table.Context(source), 0);

        Assert.Empty(table.Replacements.Used);
        Assert.Contains("used:" + Key(source, 0), table.Pending.Context.Extra.Keys);

        table.Answer("no");

        Assert.Single(table.Replacements.Used);
        Assert.Contains(Key(source, 0), table.Replacements.Used[0]);
    }

    /// <summary>
    /// CR 201.3: a "choose a card name" the card printed no options for is answered from the names
    /// the game holds, one entry per name, not refused for having nothing to choose from.
    /// </summary>
    [Fact]
    public void A_card_name_choice_offers_the_names_the_state_holds()
    {
        var table = new InterpreterTable();
        table.Put(table.A, ZoneKey.Of(Zone.Library, table.A), E9yNamed("e9y alpha", CardType.Land));
        table.Put(table.B, ZoneKey.Shared(Zone.Battlefield), E9yNamed("e9y beta", CardType.Creature));

        table.Run(new ChooseValue(ChooseWhat.CardName, "named"));

        Assert.Equal(["e9y alpha", "e9y beta"], table.Pending.Options.Select(o => o.Id));
    }

    /// <summary>
    /// CR 201.2 with 205.2a: the type narrows the <em>name</em> — the check is on the named card's
    /// type line, so only the land card's name is offered and the creature on the board is not.
    /// </summary>
    [Fact]
    public void A_typed_card_name_choice_offers_only_names_of_that_type()
    {
        var table = new InterpreterTable();
        table.Put(table.A, ZoneKey.Of(Zone.Library, table.A), E9yNamed("e9y alpha", CardType.Land));
        table.Put(table.B, ZoneKey.Shared(Zone.Battlefield), E9yNamed("e9y beta", CardType.Creature));

        table.Run(new ChooseValue(ChooseWhat.CardName, "named", OfType: CardType.Land));

        Assert.Equal(["e9y alpha"], table.Pending.Options.Select(o => o.Id));
    }
}
