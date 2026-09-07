using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// "Any target", CR 115.4: the one selector for the whole phrase. The target was chosen as the
/// spell went on the stack (CR 601.2c) and rides in one of the two slots the command already has,
/// so the query reads it back rather than making it: the object half from <c>Targets</c>, the
/// player half from <c>TargetPlayers</c>, and the CR 608.2b re-check accepting either. The land in
/// these tests is what tells the difference between this and an open filter.
/// </summary>
public sealed class QueryTargetTests
{
    private static readonly PlayerId Alice = new(0);
    private static readonly PlayerId Bob = new(1);

    [Fact]
    [RuleRef("115.4")]
    public void Any_target_resolves_to_the_creature_that_was_chosen_when_the_spell_was_cast()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(2, 2));

        var resolved = Query(board).Resolve(board.State, new AnyTarget(), Targeting(creature));

        Assert.Equal([creature], resolved);
    }

    [Fact]
    [RuleRef("115.4")]
    public void Any_target_resolving_to_a_player_names_no_object_and_the_player_is_read_from_the_context()
    {
        var board = new Board();
        _ = board.Put(Board.Creature(2, 2));

        var context = Targeting() with { TargetPlayers = [Bob] };
        Assert.Empty(Query(board).Resolve(board.State, new AnyTarget(), context));

        // The contract: nothing in Targets means the other half was chosen, and the caller reads it
        // from the context. One AnyTarget never fills both slots (CR 601.2c).
        Assert.Equal([Bob], context.TargetPlayers);
    }

    [Fact]
    [RuleRef("115.4")]
    public void A_land_is_not_a_legal_any_target_and_a_creature_planeswalker_and_battle_are()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(2, 2));
        var land = board.Put(Board.Definition("qt land", CardType.Land));
        var walker = board.Put(Board.Definition("qt walker", CardType.Planeswalker));
        var battle = board.Put(Board.Definition("qt battle", CardType.Battle));
        var query = Query(board);

        Assert.True(query.MatchesAnyTarget(board.State, creature, Targeting()));
        Assert.True(query.MatchesAnyTarget(board.State, walker, Targeting()));
        Assert.True(query.MatchesAnyTarget(board.State, battle, Targeting()));
        Assert.False(query.MatchesAnyTarget(board.State, land, Targeting()));

        // The filter is the same one a Target would have been given, so the refusal is the filter's
        // and not a special case: an open filter would have admitted the land.
        Assert.False(query.Matches(board.State, MtgEngine.Rules.Query.Query.AnyTargetFilter, land, Targeting()));
        Assert.True(query.Matches(board.State, new ObjectFilter(), land, Targeting()));
    }

    [Fact]
    [RuleRef("608.2b")]
    public void The_re_check_at_resolution_takes_an_object_or_a_player()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(2, 2));
        var spell = board.Put(Board.Creature(1, 1), Zone.Stack);

        Assert.True(Query(board).MatchesAnyTarget(board.State, creature, Targeting()));
        // A creature card on the stack is not a creature on the battlefield (CR 110.1), so the
        // re-check refuses it the way it refuses a land.
        Assert.False(Query(board).MatchesAnyTarget(board.State, spell, Targeting()));

        Assert.True(MtgEngine.Rules.Query.Query.MatchesAnyTarget(board.State, Alice));
        Assert.True(MtgEngine.Rules.Query.Query.MatchesAnyTarget(board.State, Bob));

        // CR 800.4a: a player who has lost is no longer a player, so they are no longer a target.
        board.Lost(Bob.Value);
        Assert.False(MtgEngine.Rules.Query.Query.MatchesAnyTarget(board.State, Bob));
        Assert.True(MtgEngine.Rules.Query.Query.MatchesAnyTarget(board.State, Alice));
    }

    [Fact]
    [RuleRef("115.4")]
    public void A_keyed_any_target_reads_back_what_the_earlier_step_remembered()
    {
        var board = new Board();
        var chosen = board.Put(Board.Creature(2, 2));
        var other = board.Put(Board.Creature(3, 3));

        var context = Targeting(other) with
        {
            Remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem("hit", [chosen]),
        };

        Assert.Equal([chosen], Query(board).Resolve(board.State, new AnyTarget(Key: "hit"), context));
        Assert.Equal([other], Query(board).Resolve(board.State, new AnyTarget(), context));
    }

    private static MtgEngine.Rules.Query.Query Query(Board board) => new(new FakeCharacteristics());

    private static EffectContext Targeting(params ObjectId[] targets) => new(
        null,
        Alice,
        [.. targets],
        [],
        null,
        ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
        ImmutableDictionary<string, string>.Empty,
        ImmutableDictionary<string, int>.Empty,
        [],
        null);
}
