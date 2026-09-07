using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Three readings the wave-19 box settled: what a choice among cards outside the battlefield is
/// offered (CR 110.4a), where a spell that shuffles itself away ends up (CR 608.2m), and how many
/// modes the resolution's question may take when the announcement worked the ceiling out (CR
/// 700.2 with 601.2b).
/// </summary>
public sealed partial class InterpreterTests
{
    [Fact]
    [RuleRef("110.4a")]
    public void A_choice_among_revealed_library_cards_offers_the_permanent_card()
    {
        // "Reveal the top two cards of your library. Put a permanent card from among them onto the
        // battlefield." The choice is made among cards still in the library, so its filter's
        // "permanent" is asking which of them is a permanent card, not which is on the battlefield.
        var table = new InterpreterTable();
        var creature = table.Put(table.A, ZoneKey.Of(Zone.Library, table.A), InterpreterTable.Definition(CardType.Creature));
        table.Put(table.A, ZoneKey.Of(Zone.Library, table.A), InterpreterTable.Definition(CardType.Instant));
        var spell = table.OnStack(table.A, new Sequence(
        [
            new Reveal(new Top(new You(), new Fixed(2)), RememberAs: "revealed"),
            new Move(new Choose(new ObjectFilter(Permanent: true), new You(), Among: new Remembered("revealed")), Zone.Battlefield),
        ]));

        table.Resolve(spell);

        // One option, and it is the creature card: the instant is not a permanent card.
        var option = Assert.Single(table.Pending.Options);
        Assert.Equal(creature, option.Object);

        table.Answer(option.Id);

        Assert.Contains(table.State.Objects.Values, o => o.Zone.Zone == Zone.Battlefield);
        Assert.Single(table.In(Zone.Library, table.A));
    }

    [Fact]
    [RuleRef("608.2m")]
    public void A_spell_that_shuffles_itself_into_its_library_never_reaches_the_graveyard()
    {
        // CR 608.2m puts a resolved spell into its owner's graveyard only if nothing moved it
        // first; the shuffle moved it, and the step after the shuffle still runs.
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        var spell = table.OnStack(table.A, new Sequence(
        [
            new Shuffle(IntoLibrary: new Self()),
            new GainLife(new Fixed(3)),
        ]));

        var events = table.Resolve(spell);

        Assert.False(table.State.Objects.ContainsKey(spell));
        Assert.Equal(2, table.In(Zone.Library, table.A).Count);
        Assert.Empty(table.In(Zone.Graveyard, table.A));
        Assert.Equal(23, table.Life(table.A));
        Assert.Contains(events, e => e is ZoneReordered { Shuffle: true });
        Assert.Contains(events, e => e is StackObjectResolved);
    }

    [Fact]
    [RuleRef("608.2m")]
    public void A_plain_shuffle_still_shuffles_the_library_the_effect_names()
    {
        // Nothing named to shuffle in: the arm is the one it always was, and "you" shuffle.
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Library);
        var spell = table.OnStack(table.A, new Shuffle());

        var events = table.Resolve(spell);

        Assert.Equal(2, table.In(Zone.Library, table.A).Count);
        Assert.Contains(events, e => e is ZoneReordered { Shuffle: true });
        Assert.True(table.State.Objects.ContainsKey(spell));
    }

    [Fact]
    [RuleRef("700.2")]
    public void The_mode_question_takes_the_ceiling_the_announcement_came_to()
    {
        // CR 700.2 with 601.2b: the conditional ceiling was read once, as the spell was put on the
        // stack, and the number it came to rode there. The resolution's question offers that
        // number and never re-reads the condition.
        var table = new InterpreterTable();
        var spell = table.OnStack(
            table.A,
            Modal(),
            new StackEntry(null, null, [], [], [], null, false,
                Chosen: ImmutableDictionary<string, string>.Empty.Add(ModeAnnouncement.CeilingKey, "2")));

        table.Resolve(spell);

        Assert.Equal(2, table.Pending.Max);
        Assert.Equal(1, table.Pending.Min);

        table.Answer("0", "2");

        Assert.Equal(25, table.Life(table.A));
    }

    [Fact]
    [RuleRef("700.2")]
    public void A_modal_spell_that_announced_no_ceiling_offers_the_records_own_max()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, Modal());

        table.Resolve(spell);

        Assert.Equal(1, table.Pending.Max);
        Assert.Equal(1, table.Pending.Min);
    }

    [Fact]
    [RuleRef("700.2")]
    public void Modes_already_announced_run_as_announced_without_the_ceiling_being_read_again()
    {
        // The stack entry carries two modes although the record's Max is one: the announcement
        // decided, and CR 700.2 does not let the resolution take that decision a second time.
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, Modal(), new StackEntry(null, null, [], [], [0, 1], null, false));

        table.Resolve(spell);

        Assert.Null(table.State.Pending);
        Assert.Equal(23, table.Life(table.A));
    }

    /// <summary>Three modes worth 1, 2 and 4 life, so which of them ran is read off the life total.</summary>
    private static ChooseMode Modal() => new(
    [
        new Mode("gain 1 life", new GainLife(new Fixed(1))),
        new Mode("gain 2 life", new GainLife(new Fixed(2))),
        new Mode("gain 4 life", new GainLife(new Fixed(4))),
    ]);
}
