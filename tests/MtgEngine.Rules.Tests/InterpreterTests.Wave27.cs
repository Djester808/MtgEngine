using MtgEngine.Cards;
using MtgEngine.Rules.Interpreter;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 27: moving counters as one action and the number the controller chooses (CR 122.5), the
/// placement a countered spell is put into (CR 701.6a), and the two continuous effects the Apply
/// arm could not create because they name no object — unpreventable damage (CR 615.12) and a
/// library played with its top card revealed (CR 401.5).
/// </summary>
public sealed partial class InterpreterTests
{
    private static readonly ObjectFilter Wave27Creatures = new(Types: CardType.Creature);
    private static readonly ObjectFilter Wave27Artifacts = new(Types: CardType.Artifact);

    [Fact]
    [RuleRef("122.5")]
    public void Moving_all_counters_takes_them_off_one_object_and_puts_them_on_the_other()
    {
        var table = new InterpreterTable();
        var source = table.Permanent(table.A);
        table.Run(new PutCounters(new Each(Wave27Creatures), "+1/+1", new Fixed(3)));
        var destination = table.Permanent(table.A, CardType.Artifact, power: null, toughness: null);

        table.Run(new MoveCounters(new Each(Wave27Creatures), new Each(Wave27Artifacts), "+1/+1", All: true));

        Assert.False(table.Object(source).Counters.ContainsKey("+1/+1"));
        Assert.Equal(3, table.Object(destination).Counters["+1/+1"]);
    }

    [Fact]
    [RuleRef("122.5")]
    public void Moving_any_number_of_counters_asks_the_controller_how_many()
    {
        var table = new InterpreterTable();
        var source = table.Permanent(table.A);
        table.Run(new PutCounters(new Each(Wave27Creatures), "+1/+1", new Fixed(3)));
        var destination = table.Permanent(table.A, CardType.Artifact, power: null, toughness: null);

        var move = table.OnStack(table.A, new MoveCounters(new Each(Wave27Creatures), new Each(Wave27Artifacts), "+1/+1", AnyNumber: true));
        table.Resolve(move);

        // CR 122.5 with 601.2b: "any number" is announced by the controller as the effect resolves,
        // bounded by what is there — before this wave nothing read the record at all and every
        // counter stayed where it was.
        Assert.Equal(table.A, table.Pending.Player);
        table.Answer("2");

        Assert.Equal(1, table.Object(source).Counters["+1/+1"]);
        Assert.Equal(2, table.Object(destination).Counters["+1/+1"]);
    }

    [Fact]
    [RuleRef("122.5")]
    public void A_move_with_nothing_to_move_onto_takes_no_counter_off()
    {
        var table = new InterpreterTable();
        var source = table.Permanent(table.A);
        table.Run(new PutCounters(new Each(Wave27Creatures), "+1/+1", new Fixed(3)));

        // CR 122.5: if either half of the move is impossible nothing moves, so a move that names no
        // destination is not a removal — the counters are still on the object they were on.
        table.Run(new MoveCounters(new Each(Wave27Creatures), new Each(Wave27Artifacts), "+1/+1", All: true));

        Assert.Equal(3, table.Object(source).Counters["+1/+1"]);
    }

    [Fact]
    [RuleRef("701.6a")]
    public void A_countered_spell_is_put_where_the_effect_says_and_not_into_a_graveyard()
    {
        var table = new InterpreterTable();
        var below = table.Card(table.B, Zone.Library);
        table.OnStack(table.B, new Cards.Draw(new Fixed(1)));

        // "Counter target spell. Its controller puts it on top of their library instead": one
        // action, so the spell never reaches the graveyard and the placement is the record's.
        table.Run(new Cards.Counter(new Each(new ObjectFilter(Spell: true)), Zone.Library, Placement.Top));

        Assert.Empty(table.In(Zone.Graveyard, table.B));
        var library = table.In(Zone.Library, table.B);

        // CR 400.7: the spell is a new object in its new zone, so it is the card on top that names
        // it and not the id it had on the stack.
        Assert.Equal(2, library.Count);
        Assert.NotEqual(below, library[0]);
        Assert.Equal(below, library[1]);
    }

    [Fact]
    [RuleRef("615.12")]
    public void Damage_that_cannot_be_prevented_is_created_against_every_player()
    {
        var table = new InterpreterTable();

        // CR 615.12: the effect names no object and no player — it is about damage however it is
        // dealt — so an Apply that reaches no object must still create it, which is what the arm
        // did before this wave when the selector found nothing.
        var events = table.Run(new Cards.Apply(
            new DamageCantBePrevented(),
            new Each(Wave27Creatures),
            new UntilEndOfTurn()));

        var created = Assert.Single(events.OfType<ContinuousEffectCreated>()).Effect;
        Assert.Equal([table.A, table.B], created.AppliesToPlayers);
        Assert.Empty(created.AppliesTo);
    }

    [Fact]
    [RuleRef("401.5")]
    public void A_library_played_with_its_top_card_revealed_is_created_against_that_player()
    {
        var table = new InterpreterTable();

        // CR 401.5: the effect is about a library, so it is created against the player whose
        // library it is; who may see the card is the view's half (seam-requests/E9v.md ask 2).
        var events = table.Run(new Cards.Apply(
            new PlayWithTopCardRevealed(new EachOpponent()),
            new Each(Wave27Creatures),
            new Permanently()));

        var created = Assert.Single(events.OfType<ContinuousEffectCreated>()).Effect;
        Assert.Equal(table.B, Assert.Single(created.AppliesToPlayers));
        Assert.Empty(created.AppliesTo);
    }
}
