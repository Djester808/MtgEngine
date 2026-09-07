using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// What a card chose and what a card was told to do, CR 608.2d and 608.2g. A choice a resolution
/// makes outlives that resolution when the thing that chose is a permanent, and the interpreter's
/// own casts go through the door CR 608.2g opens rather than the one a player uses.
/// </summary>
public sealed partial class InterpreterTests
{
    /* ---------------------------------------------------------------------------------------- *
     * CR 608.2d — the value a permanent chose stays on the permanent.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void A_colour_chosen_as_an_ability_resolves_is_kept_on_the_permanent_that_chose_it()
    {
        var table = new InterpreterTable();
        var permanent = table.Permanent(table.A, CardType.Enchantment, null, null);
        table.Resolve(table.OnStack(table.A, Choose, Of(permanent)));

        Assert.Equal("value", table.Pending.Kind);
        var events = table.Answer("Green");

        // CR 608.2d: "as this enters, choose a color" is answered once and asked about for as long
        // as the permanent is on the battlefield, so the object keeps it rather than the resolution.
        Assert.Contains(events, e => e is ValueChosen { Key: "color", Value: "Green" });
        Assert.Equal("Green", table.Object(permanent).Chosen["color"]);
    }

    [Fact]
    public void A_later_resolution_of_the_same_permanent_reads_the_colour_without_asking_again()
    {
        var table = new InterpreterTable();
        var permanent = table.Permanent(table.A, CardType.Enchantment, null, null);
        table.Resolve(table.OnStack(table.A, Choose, Of(permanent)));
        table.Answer("Green");

        // A second ability of the same object: the choice is already in the context, so the walk
        // runs straight through the ChooseValue node and "one mana of the chosen color" is green.
        var events = table.Resolve(table.OnStack(table.A, new Sequence([Choose, OfChosenColor]), Of(permanent)));

        Assert.Null(table.State.Pending);
        Assert.Equal(1, table.State.Player(table.A).Mana.Green);

        // Reading a stored choice back is not choosing again, so nothing is written a second time.
        Assert.DoesNotContain(events, e => e is ValueChosen);
    }

    [Fact]
    public void The_stack_entrys_choice_wins_over_the_one_the_object_is_keeping()
    {
        var table = new InterpreterTable();
        var permanent = table.Permanent(table.A, CardType.Enchantment, null, null);
        table.Resolve(table.OnStack(table.A, Choose, Of(permanent)));
        table.Answer("Green");

        // CR 603.3d: what the firing itself chose travels with its stack object, and it is the
        // later word of the two — "of that color" is the colour this ability triggered on, not the
        // one the permanent chose when it entered.
        var entry = Of(permanent) with
        {
            Chosen = ImmutableDictionary<string, string>.Empty.SetItem("color", "Red"),
        };
        table.Resolve(table.OnStack(table.A, OfChosenColor, entry));

        var pool = table.State.Player(table.A).Mana;
        Assert.Equal(1, pool.Red);
        Assert.Equal(0, pool.Green);
    }

    [Fact]
    public void A_spell_keeps_nothing_because_the_object_that_chose_leaves_the_stack()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, Choose);
        table.Resolve(spell);
        var events = table.Answer("Green");

        // CR 400.7: the object that chose is not a permanent, so there is nothing for a later
        // ability of it to read and no choice is written down.
        Assert.DoesNotContain(events, e => e is ValueChosen);
        Assert.Empty(table.Object(spell).Chosen);
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 608.2g — a cast the resolution instructed.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void The_may_cast_arm_announces_through_the_instructed_cast_and_not_the_ordinary_one()
    {
        var table = new InterpreterTable();
        var casting = new InterpreterTestsCasting();
        table.Subject.Casting = casting;
        var card = table.Card(table.A, Zone.Graveyard);

        table.Resolve(table.OnStack(table.A, new MayCast(new Remembered("it")), Remembering(card)));
        table.Answer("yes");

        // CR 608.2g: the cast happens during this resolution and CR 117.1a's timing does not apply
        // to it, so it cannot go through the door a player's own announcement uses.
        Assert.Equal(card, Assert.Single(casting.Instructed).Card);
        Assert.Empty(casting.Cast);
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 106.6 — what the mana an instruction added remembers.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void A_keyed_add_mana_writes_one_block_per_single_mana_under_that_key()
    {
        var table = new InterpreterTable();
        var permanent = table.Permanent(table.A, CardType.Enchantment, null, null);
        table.Resolve(table.OnStack(table.A, new AddMana(new ManaProduction("{G}{G}"), Key: "spent"), Of(permanent)));

        // CR 106.6a: a separate delayed ability is created for each mana produced, so the two mana
        // have to be told apart — one block each, both naming the permanent that produced them.
        var blocks = table.State.Player(table.A).Mana.Provenance;
        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, block => Assert.Equal(1, block.Amount));
        Assert.All(blocks, block => Assert.Equal(permanent, block.Source));
        Assert.Equal(["spent#0", "spent#1"], blocks.Select(b => b.Key ?? string.Empty).Order().ToList());
    }

    [Fact]
    public void An_unkeyed_add_mana_writes_one_plain_block_naming_the_source()
    {
        var table = new InterpreterTable();
        var permanent = table.Permanent(table.A, CardType.Enchantment, null, null);
        table.Resolve(table.OnStack(table.A, new AddMana(new ManaProduction("{G}{G}")), Of(permanent)));

        // CR 106.6: mana with nothing watching it still remembers where it came from, because
        // "spend only mana produced by ..." (CR 106.6b) reads the source and not a key.
        var block = Assert.Single(table.State.Player(table.A).Mana.Provenance);
        Assert.Equal(2, block.Amount);
        Assert.Equal(permanent, block.Source);
        Assert.Null(block.Key);
    }

    /// <summary>"As this enters, choose a color", CR 608.2d.</summary>
    private static readonly ChooseValue Choose =
        new(ChooseWhat.Color, "color", Options: ["White", "Blue", "Black", "Red", "Green"]);

    /// <summary>"Add one mana of the chosen color", CR 106.1.</summary>
    private static readonly AddMana OfChosenColor =
        new(new ManaProduction(ChosenColor: true, ChosenColorKey: "color"));

    /// <summary>An ability of that permanent on the stack, CR 603.3: the source is what chose.</summary>
    private static StackEntry Of(ObjectId source) => new(source, 0, [], [], [], null, false);
}
