using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A non-targeted choice from among a set, CR 700.2, and the numbers "among" reads, CR 608.2. A
/// <see cref="Choose"/> with <c>Among</c> offers exactly the objects the selector names wherever
/// they are — revealed cards still in the library — narrowed by its filter and never by the
/// filter's zone; a set with nothing in it asks nothing. <see cref="TotalAmong"/> and
/// <see cref="GreatestAmong"/> read their inner amount once per candidate with that candidate as
/// <see cref="Self"/>, never the source and never the triggering object.
/// </summary>
public sealed partial class InterpreterTests
{
    [Fact]
    [RuleRef("700.2")]
    public void A_keyed_choice_inside_for_each_is_asked_once_per_iteration()
    {
        var table = new InterpreterTable();
        var first = table.Permanent(table.A, power: 1, toughness: 5);
        var second = table.Permanent(table.A, power: 1, toughness: 5);
        table.Permanent(table.B, power: 1, toughness: 5);
        table.Permanent(table.B, power: 1, toughness: 5);
        var spell = table.OnStack(table.A, new ForEach(
            new Each(new ObjectFilter(Types: CardType.Creature, ControlledBy: new EachOpponent())),
            new DealDamage(new Fixed(1), To: new Choose(new ObjectFilter(Types: CardType.Creature, ControlledBy: new You()), new You(), Key: "pick"))));

        table.Resolve(spell);
        table.Answer(first.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // CR 700.2: the second iteration makes its own choice rather than reusing the first.
        Assert.NotNull(table.State.Pending);
        table.Answer(second.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Null(table.State.Pending);
        Assert.Equal(1, table.Object(first).Damage);
        Assert.Equal(1, table.Object(second).Damage);
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 700.2 — "from among them": the candidates are the remembered set, wherever it is.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    [RuleRef("700.2")]
    public void A_choice_among_remembered_library_cards_offers_exactly_those_and_the_rest_are_still_remembered()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Library);
        var library = table.In(Zone.Library, table.A);
        var revealed = library.Take(2).ToList();

        // "Reveal the top two cards of your library. Put any number of creature cards from among
        // them onto the battlefield, then the rest into your graveyard."
        var spell = table.OnStack(table.A, new Sequence(
        [
            new Reveal(new Top(new You(), new Fixed(2)), RememberAs: "revealed"),
            new Cards.Move(
                new Choose(new ObjectFilter(Types: CardType.Creature), new You(), UpTo: true, Key: "chosen", Among: new Remembered("revealed")),
                Zone.Battlefield),
            new Cards.Move(new Remembered("revealed"), Zone.Graveyard),
        ]));
        table.Resolve(spell);

        // CR 700.2 with 601.2b: the question is the revealed cards and nothing else in the library,
        // and "any number" is none up to all of them.
        Assert.Equal(revealed, table.Pending.Options.Select(o => o.Object).OfType<ObjectId>());
        Assert.Equal(table.A, table.Pending.Player);
        Assert.Equal(0, table.Pending.Min);
        Assert.Equal(2, table.Pending.Max);

        var events = table.Answer(table.Pending.Options[0].Id);

        // CR 608.2: the chosen card is what the move reads, the rest is what the next step reads
        // — the reveal's memory survived the question — and the third card was never touched.
        Assert.Single(table.State.In(ZoneKey.Shared(Zone.Battlefield)));
        Assert.Single(table.In(Zone.Graveyard, table.A));
        Assert.Equal([library[2]], table.In(Zone.Library, table.A));
        Assert.Contains(events, e => e is StackObjectResolved);
    }

    [Fact]
    [RuleRef("700.2")]
    public void The_filter_narrows_the_candidates_and_its_zone_is_not_read()
    {
        var table = new InterpreterTable();
        var creature = table.Card(table.A, Zone.Library);
        table.Put(table.A, ZoneKey.Of(Zone.Library, table.A), InterpreterTable.Definition(CardType.Land));
        table.Card(table.A, Zone.Library);

        // The filter says creature cards, and says the battlefield — the zone L022 refuses beside
        // Among, written here to prove the interpreter never reads it: the candidates are where
        // they are.
        var filter = new ObjectFilter(In: Zone.Battlefield, Types: CardType.Creature);
        table.Resolve(table.OnStack(table.A, new Sequence(
        [
            new Reveal(new Top(new You(), new Fixed(2)), RememberAs: "revealed"),
            new Cards.Move(new Choose(filter, new You(), new Fixed(1), Key: "chosen", Among: new Remembered("revealed")), Zone.Hand),
        ])));

        // CR 700.2: the land among the revealed two is not offered; "choose one" is exactly one.
        Assert.Equal([creature], table.Pending.Options.Select(o => o.Object).OfType<ObjectId>());
        Assert.Equal(1, table.Pending.Min);
        Assert.Equal(1, table.Pending.Max);

        table.Answer(table.Pending.Options[0].Id);
        Assert.Single(table.In(Zone.Hand, table.A));
    }

    [Fact]
    [RuleRef("700.2")]
    public void Nothing_is_offered_when_the_set_chosen_from_is_empty()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        var spell = table.OnStack(table.A, new Sequence(
        [
            new Cards.Move(
                new Choose(new ObjectFilter(Types: CardType.Creature), new You(), UpTo: true, Key: "chosen", Among: new Remembered("revealed")),
                Zone.Battlefield),
            new GainLife(new Fixed(1)),
        ]));

        var events = table.Resolve(spell);

        // CR 101.4: a question with no options could never be answered, so none is asked; the
        // choice is of nothing, the move moves nothing, and the resolution goes on to its end.
        Assert.Null(table.State.Pending);
        Assert.Empty(table.State.In(ZoneKey.Shared(Zone.Battlefield)));
        Assert.Equal(21, table.Life(table.A));
        Assert.Contains(events, e => e is StackObjectResolved);
    }

    [Fact]
    [RuleRef("700.2")]
    public void A_choice_without_among_is_made_from_the_filter_in_its_own_zone()
    {
        var table = new InterpreterTable();
        var mine = table.Permanent(table.A);
        table.Permanent(table.B);
        table.Card(table.A, Zone.Library);

        // "Choose a creature you control": no key, no Among — the battlefield is the filter's zone.
        table.Resolve(table.OnStack(table.A, new Cards.Tap(new Choose(new ObjectFilter(ControlledBy: new You()), new You()))));

        Assert.Equal([mine], table.Pending.Options.Select(o => o.Object).OfType<ObjectId>());
        table.Answer(table.Pending.Options[0].Id);
        Assert.True(table.Object(mine).Tapped);
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 608.2 — "the total power among": the inner amount is read as each candidate.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    [RuleRef("608.2")]
    public void Total_among_binds_each_candidate_as_self_and_not_the_source()
    {
        var table = new InterpreterTable();
        var source = table.Permanent(table.A, power: 1, toughness: 1);
        table.Permanent(table.A, power: 2, toughness: 2);
        table.Permanent(table.A, power: 3, toughness: 3);
        table.Permanent(table.B, power: 7, toughness: 7);
        var mine = new ObjectFilter(Types: CardType.Creature, ControlledBy: new You());

        // "You gain life equal to the total power of creatures you control": 1 + 2 + 3. Bound to
        // the source it would be the source's 1 three times over.
        table.Resolve(table.OnStack(table.A, new GainLife(new TotalAmong(mine, new PowerOf(new Self()))), Ability(source)));

        Assert.Equal(26, table.Life(table.A));
    }

    [Fact]
    [RuleRef("608.2")]
    public void Greatest_among_binds_each_candidate_as_self_and_leaves_triggering_alone()
    {
        var table = new InterpreterTable();
        var source = table.Permanent(table.A, power: 1, toughness: 1);
        table.Permanent(table.A, power: 4, toughness: 4);
        table.Permanent(table.A, power: 2, toughness: 2);
        var died = table.Permanent(table.B, power: 5, toughness: 5);
        var mine = new ObjectFilter(Types: CardType.Creature, ControlledBy: new You());

        // "the greatest power among creatures you control" is 4, whatever the source or the
        // trigger's object has; "the triggering creature's power" inside the same reading is
        // still the trigger's object, once per candidate: 5 + 5 + 5.
        table.Resolve(table.OnStack(table.A, new Sequence(
        [
            new GainLife(new GreatestAmong(mine, new PowerOf(new Self()))),
            new GainLife(new TotalAmong(mine, new PowerOf(new Triggering()))),
        ]), Ability(source) with { Triggering = [died] }));

        Assert.Equal(20 + 4 + 15, table.Life(table.A));
    }

    /// <summary>An ability of that permanent on the stack, CR 603.3: the source the amount's "this" would be.</summary>
    private static StackEntry Ability(ObjectId source) => new(source, 0, [], [], [], null, false);
}
