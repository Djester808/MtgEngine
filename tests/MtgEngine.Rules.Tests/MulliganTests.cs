using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Opening hands, CR 103.4–103.5. The rulebook's own statements are the acceptance tests: the
/// starting player declares first and each other player in turn order (CR 103.5), a mulligan
/// shuffles the hand back and draws a new one of the same size (CR 103.5), a kept hand costs one
/// card per mulligan taken (CR 103.5), the first mulligan is free in a multiplayer game
/// (CR 103.5c), and none of it happens during turn 1, which begins only after the last keep
/// (CR 103.8).
/// </summary>
public sealed class MulliganTests
{
    [Fact]
    public void Creating_a_game_stops_at_the_starting_players_declaration_before_turn_one()
    {
        var table = new MulliganTable();

        // CR 103.5: the starting player declares first, and CR 101.4: the game waits for it.
        Assert.Equal(ChoiceKinds.Mulligan, table.Pending.Kind);
        Assert.Equal(MulliganTable.P0, table.Pending.Player);
        Assert.Equal([ChoiceKinds.Keep, ChoiceKinds.Mulligan], table.Pending.Options.Select(o => o.Id));
        Assert.Equal(1, table.Pending.Min);
        Assert.Equal(1, table.Pending.Max);

        // CR 103.5 happens before CR 103.8's first turn: nothing of turn 1 has happened.
        Assert.Equal(0, table.State.Turn.Number);
        Assert.Empty(table.Log.OfType<TurnBegan>());
        Assert.Empty(table.Log.OfType<StepBegan>());
    }

    [Fact]
    public void Mulligans_are_offered_only_when_the_setup_asks_for_them()
    {
        var table = new MulliganTable(offerMulligans: false);

        Assert.Null(table.State.Pending);
        Assert.Equal(1, table.State.Turn.Number);
    }

    [Fact]
    public void The_second_player_declares_once_the_first_has_kept()
    {
        var table = new MulliganTable();

        table.Keep();

        // CR 103.5: "then each other player in turn order does the same".
        Assert.Equal(ChoiceKinds.Mulligan, table.Pending.Kind);
        Assert.Equal(MulliganTable.P1, table.Pending.Player);
        Assert.Equal(0, table.State.Turn.Number);
    }

    [Fact]
    public void Turn_one_begins_when_every_player_has_kept()
    {
        var table = new MulliganTable();

        table.Keep();
        var events = table.Keep();

        // CR 103.8: the starting player takes the first turn, and it starts here and nowhere else.
        var began = Assert.Single(events.OfType<TurnBegan>());
        Assert.Equal(1, began.Number);
        Assert.Equal(MulliganTable.P0, began.ActivePlayer);
        Assert.Null(table.State.Pending);
        Assert.Equal(1, table.State.Turn.Number);
        Assert.Equal(Step.Upkeep, table.State.Turn.Step);
    }

    [Fact]
    public void A_mulligan_shuffles_the_hand_back_draws_a_new_one_and_asks_again()
    {
        var table = new MulliganTable();
        var first = table.Hand(MulliganTable.P0);

        var events = table.Mulligan();

        // CR 103.5: shuffle the hand back into the library, then draw a new hand of the same size.
        Assert.Contains(events.OfType<ZoneReordered>(), z => z.Shuffle);
        var second = table.Hand(MulliganTable.P0);
        Assert.Equal(first.Count, second.Count);
        Assert.Empty(second.Intersect(first));

        // CR 103.5: "then, if any player took a mulligan, the process is repeated".
        Assert.Equal(ChoiceKinds.Mulligan, table.Pending.Kind);
        Assert.Equal(MulliganTable.P0, table.Pending.Player);
        Assert.Equal("1", table.Pending.Context.Extra["mulligans"]);
        Assert.Equal(0, table.State.Turn.Number);
    }

    [Fact]
    public void Keeping_after_one_mulligan_puts_one_card_on_the_bottom_before_the_next_player_declares()
    {
        var table = new MulliganTable();
        table.Mulligan();

        table.Keep();

        // CR 103.5: one card per mulligan taken goes on the bottom of the library.
        Assert.Equal(ChoiceKinds.MulliganBottom, table.Pending.Kind);
        Assert.Equal(1, table.Pending.Min);
        Assert.Equal(1, table.Pending.Max);

        var bottomed = table.Pending.Options[0];
        var library = ZoneKey.Of(Zone.Library, MulliganTable.P0);
        table.Answer(bottomed.Id);

        Assert.Equal(6, table.Hand(MulliganTable.P0).Count);
        Assert.Equal(bottomed.Label, table.State.Objects[table.State.In(library)[^1]].Face.Name);

        // Only then does the next player declare; turn 1 still has not begun.
        Assert.Equal(MulliganTable.P1, table.Pending.Player);
        Assert.Equal(0, table.State.Turn.Number);
    }

    [Fact]
    public void The_first_mulligan_costs_no_card_in_a_multiplayer_game()
    {
        var table = new MulliganTable(players: 4);

        table.Mulligan();
        table.Keep();

        // CR 103.5c: the first mulligan in a multiplayer game puts no card on the bottom, so
        // there is nothing to ask and the next player declares straight away.
        Assert.Equal(ChoiceKinds.Mulligan, table.Pending.Kind);
        Assert.Equal(MulliganTable.P1, table.Pending.Player);
        Assert.Equal(7, table.Hand(MulliganTable.P0).Count);
    }

    [Fact]
    public void The_first_mulligan_costs_a_card_in_a_two_player_game()
    {
        var table = new MulliganTable(players: 2);

        table.Mulligan();
        table.Keep();

        // CR 103.5c exempts multiplayer and Brawl only; a duel pays for its first mulligan.
        Assert.Equal(ChoiceKinds.MulliganBottom, table.Pending.Kind);
        Assert.Equal(1, table.Pending.Min);
    }

    [Fact]
    public void Every_player_at_a_four_player_table_declares_before_turn_one()
    {
        var table = new MulliganTable(players: 4);

        var declared = new List<PlayerId>();
        for (var guard = 0; guard < 10 && table.State.Pending is not null; guard++)
        {
            declared.Add(table.Pending.Player);
            table.Keep();
        }

        // CR 103.5: the starting player, then each other player in turn order, CR 103.8.
        Assert.Equal([new PlayerId(0), new PlayerId(1), new PlayerId(2), new PlayerId(3)], declared);
        Assert.Equal(1, table.State.Turn.Number);
    }
}
