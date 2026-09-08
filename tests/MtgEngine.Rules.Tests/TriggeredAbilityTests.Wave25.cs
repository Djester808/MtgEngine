using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The events waves 23 and 24 added and the matcher had no arm for: a library being searched
/// (CR 701.23f) and a player becoming the monarch (CR 725.1), plus the draw-step reading now that
/// the state keeps a per-step draw count (CR 121.1 with CR 504.1) instead of the matcher walking
/// the batch for a step boundary.
/// </summary>
public sealed partial class TriggeredAbilityTests
{
    private static readonly PlayerId Seat0 = new(0);
    private static readonly PlayerId Seat1 = new(1);

    /// <summary>A watcher of seat 0's that fires whenever an opponent searches a library.</summary>
    private static TriggeredAbilityBoard Wave25SearchWatcher() =>
        Wave23Watcher(new TriggeredAbilityBoard(), new Searches(new PlayerFilter(Opponent: true)));

    [Fact]
    [RuleRef("701.23f")]
    public void A_search_of_a_library_fires_the_watcher_for_the_player_who_searched()
    {
        var board = Wave25SearchWatcher();

        // CR 701.23f: the search itself is the event, written when it begins and whatever it finds.
        var fired = Wave23Batch(board, new LibrarySearched(Seat1, Seat1));

        var one = Assert.Single(fired);
        Assert.Equal(Seat1, one.SubjectPlayer);
        Assert.Empty(one.Subjects);
    }

    [Fact]
    [RuleRef("701.23f")]
    public void A_search_of_someone_elses_library_still_fires_for_the_searcher()
    {
        var board = Wave25SearchWatcher();

        // Seat 1 searches seat 0's library. The trigger reads Searcher, so requiring the owner and
        // the searcher to be the same player would silently stop this card firing (E1e § 3).
        var fired = Wave23Batch(board, new LibrarySearched(Seat0, Seat1));

        Assert.Equal(Seat1, Assert.Single(fired).SubjectPlayer);
    }

    [Fact]
    [RuleRef("701.23f")]
    public void A_search_the_filter_does_not_admit_fires_nothing()
    {
        var board = Wave25SearchWatcher();

        // The watcher's controller searches an opponent's library: "an opponent searches" is about
        // the searcher, who is seat 0 here, so an opponent filter admits nobody and nothing fires.
        var fired = Wave23Batch(board, new LibrarySearched(Seat1, Seat0));

        Assert.Empty(fired);
    }

    [Fact]
    [RuleRef("725.1")]
    public void Becoming_the_monarch_fires_for_the_player_who_became_it()
    {
        var board = Wave23Watcher(new TriggeredAbilityBoard(), new BecomesMonarch(new You()));

        // CR 725.1: the designation exists once an effect instructs a player to take it, and the
        // event names that player.
        var fired = Wave23Batch(board, new MonarchChanged(Seat0));

        var one = Assert.Single(fired);
        Assert.Equal(Seat0, one.SubjectPlayer);
        Assert.Empty(one.Subjects);
    }

    [Fact]
    [RuleRef("725.3")]
    public void Losing_the_monarchy_to_someone_else_fires_nothing_for_the_player_who_lost_it()
    {
        var board = Wave23Watcher(new TriggeredAbilityBoard(), new BecomesMonarch(new You()));
        board.Fold(new MonarchChanged(Seat0));

        // CR 725.3: as seat 1 becomes the monarch seat 0 ceases to be it. "Whenever you become the
        // monarch" is about becoming it, so the player who lost it fires nothing.
        var fired = Wave23Batch(board, new MonarchChanged(Seat1));

        Assert.Empty(fired);
    }

    [Fact]
    [RuleRef("725.1")]
    public void A_watcher_of_every_player_fires_once_for_the_one_the_event_names()
    {
        var board = Wave23Watcher(new TriggeredAbilityBoard(), new BecomesMonarch(new EachPlayer()));

        // A reference admitting both seats still fires once: the event is one player becoming it.
        var fired = Wave23Batch(board, new MonarchChanged(Seat1));

        Assert.Equal(Seat1, Assert.Single(fired).SubjectPlayer);
    }

    [Fact]
    [RuleRef("504.1")]
    public void A_draw_step_whose_own_draw_was_skipped_spares_the_next_draw_taken_in_it()
    {
        var board = Wave23DrawWatcher(new TriggeredAbilityBoard()).ActivePlayer(1);
        var drawn = Wave23Card(board, "later");
        board.Fold(new StepBegan(Step.Draw));

        // CR 504.1 with CR 614.5: the turn-based draw was replaced away, so this draw — in a batch
        // of its own, carrying no StepBegan for a walk of the batch to find — is the first card
        // drawn in that draw step and is the one CR 121.1 spares.
        var fired = Wave23Batch(board, new ObjectDrawn(Seat1, drawn));

        Assert.Empty(fired);
    }

    [Fact]
    [RuleRef("121.1")]
    public void A_draw_in_the_upkeep_does_not_spend_the_draw_steps_spared_draw()
    {
        var board = Wave23DrawWatcher(new TriggeredAbilityBoard()).ActivePlayer(1);
        var early = Wave23Card(board, "upkeep");
        var turnBased = Wave23Card(board, "draw step");

        // The exception counts draws per step and not per turn, so the upkeep draw fires and the
        // draw step's own first draw is still spared with the turn's count already at two.
        var upkeep = Wave23Batch(board, new StepBegan(Step.Upkeep), new ObjectDrawn(Seat1, early));
        var drawStep = Wave23Batch(board, new StepBegan(Step.Draw), new ObjectDrawn(Seat1, turnBased));

        Assert.Equal(early, Assert.Single(Assert.Single(upkeep).Subjects));
        Assert.Empty(drawStep);
    }
}
