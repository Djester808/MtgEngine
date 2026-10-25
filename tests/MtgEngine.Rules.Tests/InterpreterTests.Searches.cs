using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A search whose destination depends on the card found, CR 701.23a: "search your library for a
/// creature or land card, reveal it, put it into your hand. If it's a land card, put it onto the
/// battlefield tapped instead" is one search of one card (V10), and its <see cref="Search.Instead"/>
/// list is read at resolution against the card found, where it lies, the first entry that admits
/// the card winning. Every card here is invented; the library holds one land and one creature.
/// </summary>
public sealed partial class InterpreterTests
{
    private const string FoundKey = "found";

    /* ---------------------------------------------------------------------------------------- *
     * CR 701.23a — the entry that admits the card found gives its destination.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    [RuleRef("701.23a")]
    public void A_land_the_search_found_goes_to_the_battlefield_tapped_as_its_instead_entry_says()
    {
        var table = new InterpreterTable();
        var (land, creature) = Stocked(table);
        table.Resolve(table.OnStack(table.A, LandInstead()));

        Assert.Equal("search", table.Pending.Kind);
        table.Answer(Option(table, land));

        // CR 701.23a: the land is the card the entry admits, so it went where the entry says — the
        // battlefield, tapped — and not to the hand the search itself names; the creature was not
        // found and is still in the library.
        var arrived = Assert.Single(table.State.In(ZoneKey.Shared(Zone.Battlefield)));
        Assert.Equal(CardType.Land, table.Object(arrived).Face.Types.Types);
        Assert.True(table.Object(arrived).Tapped);
        Assert.Empty(table.In(Zone.Hand, table.A));
        Assert.Contains(creature, table.In(Zone.Library, table.A));
    }

    [Fact]
    [RuleRef("701.23a")]
    public void A_creature_the_search_found_goes_to_the_hand_the_search_itself_names()
    {
        var table = new InterpreterTable();
        var (land, creature) = Stocked(table);
        table.Resolve(table.OnStack(table.A, LandInstead()));
        table.Answer(Option(table, creature));

        // CR 701.23a: no entry admits a creature, so it takes the search's own destination, untapped
        // as anything in a hand is, and nothing arrived on the battlefield.
        var held = Assert.Single(table.In(Zone.Hand, table.A));
        Assert.Equal(CardType.Creature, table.Object(held).Face.Types.Types);
        Assert.False(table.Object(held).Tapped);
        Assert.Empty(table.State.In(ZoneKey.Shared(Zone.Battlefield)));
        Assert.Contains(land, table.In(Zone.Library, table.A));
    }

    [Fact]
    [RuleRef("701.23a")]
    public void An_instead_entry_is_read_against_the_card_where_it_lies_whatever_zone_it_named()
    {
        var table = new InterpreterTable();
        var (land, _) = Stocked(table);
        var search = LandInstead() with
        {
            Instead = [new SearchDestination(new ObjectFilter(In: Zone.Battlefield, Types: CardType.Land), Zone.Battlefield, Tapped: true)],
        };
        table.Resolve(table.OnStack(table.A, search));
        table.Answer(Option(table, land));

        // V10 with CR 701.23a: the entry's filter carries no zone of its own — it describes the
        // card found, which is in the library when it is read. A zone the record wrote (the
        // battlefield a filter defaults to) would otherwise admit nothing and the land would end
        // in hand: the trap Choose.Among closed in V9.
        var arrived = Assert.Single(table.State.In(ZoneKey.Shared(Zone.Battlefield)));
        Assert.True(table.Object(arrived).Tapped);
        Assert.Empty(table.In(Zone.Hand, table.A));
    }

    [Fact]
    [RuleRef("701.23a")]
    public void The_first_instead_entry_that_admits_the_card_wins_over_a_later_one()
    {
        var search = LandInstead() with
        {
            Instead =
            [
                new SearchDestination(new ObjectFilter(Types: CardType.Land), Zone.Battlefield, Tapped: true),
                new SearchDestination(new ObjectFilter(AnyTypes: CardType.Land | CardType.Creature), Zone.Exile),
            ],
        };

        // CR 701.23a: both entries admit the land; the first one printed wins, so it is on the
        // battlefield and not in exile.
        var lands = new InterpreterTable();
        var (land, _) = Stocked(lands);
        lands.Resolve(lands.OnStack(lands.A, search));
        lands.Answer(Option(lands, land));
        var arrived = Assert.Single(lands.State.In(ZoneKey.Shared(Zone.Battlefield)));
        Assert.True(lands.Object(arrived).Tapped);
        Assert.Empty(lands.State.In(ZoneSystem.KeyFor(Zone.Exile, lands.A)));

        // Only the second entry admits the creature, so it is the second entry's destination.
        var creatures = new InterpreterTable();
        var (_, creature) = Stocked(creatures);
        creatures.Resolve(creatures.OnStack(creatures.A, search));
        creatures.Answer(Option(creatures, creature));
        var exiled = Assert.Single(creatures.State.In(ZoneSystem.KeyFor(Zone.Exile, creatures.A)));
        Assert.Equal(CardType.Creature, creatures.Object(exiled).Face.Types.Types);
        Assert.Empty(creatures.In(Zone.Hand, creatures.A));
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 701.23a — several cards found are each read on their own; the found card is remembered.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    [RuleRef("701.23a")]
    public void Each_of_several_cards_found_is_read_against_the_entries_on_its_own()
    {
        var table = new InterpreterTable();
        var (land, creature) = Stocked(table);
        table.Resolve(table.OnStack(table.A, LandInstead() with { UpTo = new Fixed(2) }));
        table.Answer(Option(table, land), Option(table, creature));

        // CR 701.23a: one search found both; the land took its entry and the creature the plain
        // destination, and the library the search emptied was shuffled with nothing in it.
        var arrived = Assert.Single(table.State.In(ZoneKey.Shared(Zone.Battlefield)));
        Assert.Equal(CardType.Land, table.Object(arrived).Face.Types.Types);
        Assert.True(table.Object(arrived).Tapped);
        var held = Assert.Single(table.In(Zone.Hand, table.A));
        Assert.Equal(CardType.Creature, table.Object(held).Face.Types.Types);
        Assert.Empty(table.In(Zone.Library, table.A));
    }

    [Fact]
    [RuleRef("701.23a")]
    public void The_card_an_instead_entry_sent_to_the_battlefield_is_remembered_under_the_searchs_key()
    {
        var table = new InterpreterTable();
        var (land, _) = Stocked(table);
        table.Resolve(table.OnStack(table.A, new Sequence(
        [
            LandInstead() with { RememberAs = FoundKey },
            new Exile(new Remembered(FoundKey)),
        ])));
        var events = table.Answer(Option(table, land));

        // CR 701.23a with 400.7: the object that arrived on the battlefield is the one remembered,
        // as a plain search remembers what it found, so the step after the search exiles it from
        // there — the land went to the battlefield first, that object is what left for exile, the
        // battlefield is empty again and the hand never held it.
        var arrived = Assert.Single(events.OfType<ObjectMoved>(), e => e.To.Zone.Zone == Zone.Battlefield);
        Assert.Contains(events, e => e is ObjectMoved { To.Zone.Zone: Zone.Exile } left && left.From == arrived.To.Id);
        Assert.Empty(table.State.In(ZoneKey.Shared(Zone.Battlefield)));
        var exiled = Assert.Single(table.State.In(ZoneSystem.KeyFor(Zone.Exile, table.A)));
        Assert.Equal(CardType.Land, table.Object(exiled).Face.Types.Types);
        Assert.Empty(table.In(Zone.Hand, table.A));
    }

    /// <summary>A library of one land card and one creature card, both player A's.</summary>
    private static (ObjectId Land, ObjectId Creature) Stocked(InterpreterTable table) => (
        table.Put(table.A, ZoneKey.Of(Zone.Library, table.A), InterpreterTable.Definition(CardType.Land)),
        table.Card(table.A, Zone.Library));

    /// <summary>
    /// "Search your library for a creature or land card, reveal it, put it into your hand. If it's
    /// a land card, put it onto the battlefield tapped instead", spelled as V10 says: one Search
    /// with one Instead entry, never an If around it.
    /// </summary>
    private static Search LandInstead() => new(
        new ObjectFilter(AnyTypes: CardType.Creature | CardType.Land),
        Zone.Hand,
        Reveal: true,
        Instead: [new SearchDestination(new ObjectFilter(Types: CardType.Land), Zone.Battlefield, Tapped: true)]);

    /// <summary>The id the open search question gave this card, which is what an answer names.</summary>
    private static string Option(InterpreterTable table, ObjectId id) =>
        table.Pending.Options.Single(o => o.Object == id).Id;

    /* ---------------------------------------------------------------------------------------- *
     * CR 701.23b — how much of the answer is compulsory, judged zone by zone.
     * ---------------------------------------------------------------------------------------- */

    /// <summary>
    /// CR 101.4: a question whose minimum is higher than its options is a question nobody can
    /// answer — the choice system refuses every answer below the minimum — and the game never moves
    /// again. A mandatory search of a zone holding nothing that matches is exactly that shape.
    /// </summary>
    [Fact]
    [RuleRef("101.4")]
    public void A_mandatory_search_that_can_find_nothing_can_still_be_answered()
    {
        var table = new InterpreterTable();
        table.Resolve(table.OnStack(table.A, new Search(new ObjectFilter(Types: CardType.Creature), Zone.Hand, From: Zone.Graveyard)));

        Assert.Empty(table.Pending.Options);
        Assert.Equal(0, table.Pending.Min);
    }

    /// <summary>CR 701.23b: a card matching in a PUBLIC zone has to be found.</summary>
    [Fact]
    [RuleRef("701.23b")]
    public void A_search_of_a_public_zone_that_can_find_something_must_find_it()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Graveyard);
        table.Resolve(table.OnStack(table.A, new Search(new ObjectFilter(Types: CardType.Creature), Zone.Hand, From: Zone.Graveyard)));

        Assert.Single(table.Pending.Options);
        Assert.Equal(1, table.Pending.Min);
    }

    /// <summary>CR 701.23b: a hidden zone never has to give the card up, however much it holds.</summary>
    [Fact]
    [RuleRef("701.23b")]
    public void A_search_of_a_hidden_zone_need_not_find_anything()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        table.Resolve(table.OnStack(table.A, new Search(new ObjectFilter(Types: CardType.Creature), Zone.Hand)));

        Assert.Single(table.Pending.Options);
        Assert.Equal(0, table.Pending.Min);
    }

    /// <summary>
    /// CR 701.23b read over the whole search rather than zone by zone: one public zone among the
    /// zones made the search compulsory, so a search of a graveyard and a library with a match in
    /// neither asked for one card out of no options and stopped the game (CR 101.4).
    /// </summary>
    [Fact]
    [RuleRef("701.23b")]
    public void A_search_of_a_hidden_and_a_public_zone_asks_only_for_what_the_public_zone_holds()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        var search = new Search(
            new ObjectFilter(Types: CardType.Creature),
            Zone.Hand,
            Zones: [Zone.Library, Zone.Graveyard]);

        table.Resolve(table.OnStack(table.A, search));

        // The graveyard holds nothing that matches, so nothing is compulsory; the library's card
        // is offered and may be left where it is.
        Assert.Single(table.Pending.Options);
        Assert.Equal(0, table.Pending.Min);
    }

    /// <summary>CR 701.23b: the same search, with the graveyard now holding one. That one must be found.</summary>
    [Fact]
    [RuleRef("701.23b")]
    public void The_public_half_of_a_mixed_search_is_still_compulsory()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Graveyard);
        var search = new Search(
            new ObjectFilter(Types: CardType.Creature),
            Zone.Hand,
            Zones: [Zone.Library, Zone.Graveyard]);

        table.Resolve(table.OnStack(table.A, search));

        Assert.Equal(2, table.Pending.Options.Count);
        Assert.Equal(1, table.Pending.Min);
    }

    /// <summary>CR 701.23b: "up to" asks for nothing at all, in any zone.</summary>
    [Fact]
    [RuleRef("701.23b")]
    public void An_up_to_search_of_a_public_zone_need_not_find_anything()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Graveyard);
        var search = new Search(
            new ObjectFilter(Types: CardType.Creature),
            Zone.Hand,
            new Fixed(2),
            From: Zone.Graveyard);

        table.Resolve(table.OnStack(table.A, search));

        Assert.Equal(0, table.Pending.Min);
        Assert.Equal(2, table.Pending.Max);
    }

    /// <summary>
    /// CR 701.23b with 101.4: three cards in a graveyard and a search for one. The minimum is what
    /// the search allows, never what the zone holds.
    /// </summary>
    [Fact]
    [RuleRef("101.4")]
    public void A_compulsory_search_never_asks_for_more_than_it_allows()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Graveyard);
        table.Card(table.A, Zone.Graveyard);
        table.Card(table.A, Zone.Graveyard);
        table.Resolve(table.OnStack(table.A, new Search(new ObjectFilter(Types: CardType.Creature), Zone.Hand, From: Zone.Graveyard)));

        Assert.Equal(3, table.Pending.Options.Count);
        Assert.Equal(1, table.Pending.Min);
        Assert.Equal(1, table.Pending.Max);
    }
}
