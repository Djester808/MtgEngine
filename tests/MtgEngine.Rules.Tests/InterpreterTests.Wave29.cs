using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 29: the fields the vocabulary had written and no arm read — a discard that names what may
/// be discarded and who picks it (CR 701.9b), a token sized by an amount (CR 111.3) and the
/// per-permanent-type edict (CR 701.21 with 110.4).
/// </summary>
public sealed partial class InterpreterTests
{
    [Fact]
    [RuleRef("701.9a")]
    public void A_discard_that_names_a_kind_of_card_only_offers_that_kind()
    {
        var table = new InterpreterTable();
        var creature = table.Card(table.A, Zone.Hand);
        var land = Wave29Land(table, table.A);

        table.Run(new Cards.Discard(What: new ObjectFilter(Types: CardType.Land)));

        // CR 701.9a: the hand is narrowed before the count is, so the creature card is not on
        // offer at all and the only answer the question admits is the land.
        var offered = table.Pending.Options.Select(o => o.Object).ToList();
        Assert.Equal([land], offered);
        Assert.DoesNotContain(creature, offered.OfType<ObjectId>());
    }

    [Fact]
    [RuleRef("701.9a")]
    public void A_discard_of_a_kind_the_hand_does_not_hold_discards_nothing_and_asks_nothing()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Hand);

        table.Run(new Cards.Discard(What: new ObjectFilter(Types: CardType.Land)));

        // CR 101.4: no answer could satisfy the question, so it is not asked; the creature card
        // stays in hand rather than being taken because it was the only thing there.
        Assert.Null(table.State.Pending);
        Assert.Single(table.In(Zone.Hand, table.A));
    }

    [Fact]
    [RuleRef("701.9b")]
    public void A_random_discard_takes_a_card_off_the_seeded_generator_without_asking()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Hand);
        table.Card(table.A, Zone.Hand);
        table.Card(table.A, Zone.Hand);

        var events = table.Run(new Cards.Discard(By: DiscardChooser.Random));

        // CR 701.9b: a random discard is not the player's choice, so nothing stops to ask; the
        // generator advance is logged so replaying the log discards the same card.
        Assert.Null(table.State.Pending);
        Assert.Equal(2, table.In(Zone.Hand, table.A).Count);
        Assert.Single(table.In(Zone.Graveyard, table.A));
        Assert.Contains(events, e => e is RngAdvanced);
    }

    [Fact]
    [RuleRef("701.9b")]
    public void The_same_seed_discards_the_same_card_at_random()
    {
        // CR 104: nothing in the rules says which card; this engine's rule is that a game replays
        // from its log, so the same seed must take the same card both times.
        Assert.Equal(Wave29RandomlyDiscarded(), Wave29RandomlyDiscarded());
    }

    [Fact]
    [RuleRef("701.9b")]
    public void Another_player_choosing_the_discard_asks_them_and_empties_the_discarding_hand()
    {
        var table = new InterpreterTable();
        var card = table.Card(table.B, Zone.Hand);

        table.Resolve(table.OnStack(table.A, new Cards.Discard(Who: new EachOpponent(), By: DiscardChooser.Controller)));

        // CR 701.9b: the effect's controller picks, so the question goes to A's seat — but the
        // card is B's and leaves B's hand.
        Assert.Equal(table.A, table.Pending.Player);
        table.Answer(card.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Empty(table.In(Zone.Hand, table.B));
        Assert.Single(table.In(Zone.Graveyard, table.B));
    }

    [Fact]
    [RuleRef("111.3")]
    public void A_token_whose_size_is_an_amount_is_created_at_that_size()
    {
        var table = new InterpreterTable();

        table.Run(new Cards.CreateToken(new TokenSpec(
            "wave twenty-nine token",
            Color.Colorless,
            CardType.Creature,
            PowerAmount: new Fixed(3),
            ToughnessAmount: new Fixed(4))));

        // CR 111.3: the value the ability defines is the token's own text, so it is on the face
        // the token was created with and not a setting applied afterwards.
        var token = table.State.Objects.Values.Single(o => o.Zone.Zone == Zone.Battlefield);
        Assert.Equal(3, token.Face.Power?.Number);
        Assert.Equal(4, token.Face.Toughness?.Number);
    }

    [Fact]
    [RuleRef("111.3")]
    public void A_token_with_printed_numbers_keeps_them_when_no_amount_is_given()
    {
        var table = new InterpreterTable();

        table.Run(new Cards.CreateToken(new TokenSpec(
            "wave twenty-nine token",
            Color.Colorless,
            CardType.Creature,
            Power: 1,
            Toughness: 1)));

        var token = table.State.Objects.Values.Single(o => o.Zone.Zone == Zone.Battlefield);
        Assert.Equal(1, token.Face.Power?.Number);
        Assert.Equal(1, token.Face.Toughness?.Number);
    }

    [Fact]
    [RuleRef("701.21")]
    public void Keeping_one_permanent_of_each_type_sacrifices_everything_else()
    {
        var table = new InterpreterTable();
        var creature = table.Permanent(table.B);
        var land = table.Permanent(table.B, CardType.Land, power: null, toughness: null);
        var second = table.Permanent(table.B);

        table.Resolve(table.OnStack(table.A, new Sacrifice(Who: new EachOpponent(), KeepOnePerPermanentType: true)));

        // CR 110.4: B controls two of the six permanent types, so at most two are kept.
        Assert.Equal(table.B, table.Pending.Player);
        Assert.Equal(2, table.Pending.Max);

        table.Answer(Wave29Id(creature), Wave29Id(land));

        // CR 701.21a: everything not kept is sacrificed, and only that.
        Assert.Equal(Zone.Battlefield, table.Object(creature).Zone.Zone);
        Assert.Equal(Zone.Battlefield, table.Object(land).Zone.Zone);
        Assert.False(table.State.Objects.ContainsKey(second));
        Assert.Single(table.In(Zone.Graveyard, table.B));
    }

    [Fact]
    [RuleRef("101.4")]
    public void Every_player_chooses_before_any_permanent_is_sacrificed()
    {
        var table = new InterpreterTable();
        var mine = table.Permanent(table.A);
        var spare = table.Permanent(table.A);
        var theirs = table.Permanent(table.B);

        table.Resolve(table.OnStack(table.A, new Sacrifice(Who: new EachPlayer(), KeepOnePerPermanentType: true)));

        // CR 101.4: the first seat's answer changes nothing on the board — the second seat chooses
        // from the same battlefield the first one did, and the sacrifices happen together.
        Assert.Equal(table.A, table.Pending.Player);
        table.Answer(Wave29Id(mine));
        Assert.Equal(Zone.Battlefield, table.Object(spare).Zone.Zone);
        Assert.Empty(table.In(Zone.Graveyard, table.A));

        Assert.Equal(table.B, table.Pending.Player);
        table.Answer(Wave29Id(theirs));

        Assert.Equal(Zone.Battlefield, table.Object(mine).Zone.Zone);
        Assert.False(table.State.Objects.ContainsKey(spare));
        Assert.Single(table.In(Zone.Graveyard, table.A));
        Assert.Equal(Zone.Battlefield, table.Object(theirs).Zone.Zone);
    }

    /// <summary>A land card in a hand, which the plain characteristics read off the definition.</summary>
    private static ObjectId Wave29Land(InterpreterTable table, PlayerId owner) =>
        table.Put(owner, ZoneKey.Of(Zone.Hand, owner), InterpreterTable.Definition(CardType.Land));

    private static string Wave29Id(ObjectId id) =>
        id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Which card a random discard took, for the seed the table always starts from.</summary>
    private static ObjectId Wave29RandomlyDiscarded()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Hand);
        table.Card(table.A, Zone.Hand);
        table.Card(table.A, Zone.Hand);
        table.Run(new Cards.Discard(By: DiscardChooser.Random));
        return table.In(Zone.Graveyard, table.A).Single();
    }
}
