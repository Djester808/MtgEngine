using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The half of a player's limits (CR 613.10) that damage reaches. A player whose life total can't
/// change loses no life to damage: CR 119.2 makes damage to a player a life loss, and CR 119.8
/// with 101.2 says that loss can't happen — so the event is dropped where it is still only
/// proposed, before any candidate is gathered.
/// <para>
/// The gain half and "can't be dealt damage" are in <c>PlayerLimitReplacementTests</c>; this file
/// is the damage-to-a-frozen-life-total case that had no test and no arm before wave 10. Every
/// definition is invented: no card name reaches the engine or its tests.
/// </para>
/// </summary>
public sealed partial class ReplacementEffectTests
{
    /// <summary>
    /// CR 119.2 with 119.8 and 101.2. Without the arm this asserts, the damage would be dealt and
    /// <c>DamageDealt</c>'s own fold would take the life — the limit would be honoured for a
    /// <see cref="LifeChanged"/> and stepped over for the commonest way a life total moves.
    /// </summary>
    [Fact]
    [RuleRef("119.2")]
    public void Damage_to_a_player_whose_life_total_cant_change_takes_no_life()
    {
        var board = new Board();
        board.Put(Frozen("still ledger"));
        var source = board.Put(Board.Creature(3, 3, "limit burner"), owner: 1);

        var frozen = System().Replace(board.State, DamageToPlayer(source, 0, 3), new PlayerId(0));

        Assert.Empty(frozen.Events);
        Assert.Equal(20, frozen.State.Player(new PlayerId(0)).Life);
    }

    /// <summary>
    /// The same board, the other seat: a limit is on one player, not on the table. This is what
    /// tells a dropped event from a replacement system that has stopped dealing damage at all.
    /// </summary>
    [Fact]
    [RuleRef("119.2")]
    public void Damage_to_the_unrestricted_player_on_the_same_board_still_lands()
    {
        var board = new Board();
        board.Put(Frozen("still ledger"));
        var source = board.Put(Board.Creature(3, 3, "limit burner"), owner: 1);

        var other = System().Replace(board.State, DamageToPlayer(source, 1, 3), new PlayerId(1));

        Assert.Equal(3, Assert.IsType<DamageDealt>(Assert.Single(other.Events)).Amount);
        Assert.Equal(17, other.State.Player(new PlayerId(1)).Life);
    }

    /// <summary>
    /// A player's limit is about the player: damage to a creature they control is untouched by it
    /// (CR 119.1 is a life total, and an object has none). Without this the arm could be written
    /// on the damage event rather than on its player half and nothing would notice.
    /// </summary>
    [Fact]
    [RuleRef("120.3")]
    public void A_frozen_life_total_does_not_shield_that_players_creatures()
    {
        var board = new Board();
        board.Put(Frozen("still ledger"));
        var creature = board.Put(Board.Creature(2, 4, "limit bystander"));
        var source = board.Put(Board.Creature(3, 3, "limit burner"), owner: 1);

        var outcome = System().Replace(board.State, Damage(source, creature, 3), new PlayerId(0));

        Assert.Equal(3, Assert.IsType<DamageDealt>(Assert.Single(outcome.Events)).Amount);
        Assert.Equal(3, outcome.State.Objects[creature].Damage);
    }

    /// <summary>
    /// The gain half on the same reading, so both directions of CR 119.7 and 119.8 are asserted
    /// against the same limit: a life total that can't change does not go up either.
    /// </summary>
    [Fact]
    [RuleRef("119.7")]
    public void A_gain_to_a_player_whose_life_total_cant_change_is_dropped()
    {
        var board = new Board();
        board.Put(Frozen("still ledger"));

        var outcome = System().Replace(board.State, new LifeChanged(new PlayerId(0), 4, "test"), new PlayerId(0));

        Assert.Empty(outcome.Events);
        Assert.Equal(20, outcome.State.Player(new PlayerId(0)).Life);
    }

    /// <summary>A permanent whose one static ability freezes its controller's life total (CR 613.10).</summary>
    private static CardDefinition Frozen(string name) =>
        Board.Definition(
            name,
            CardType.Enchantment,
            abilities: [new StaticAbility(new PlayerCant(PlayerRestriction.LifeTotalChanges), Players: new You())]);

    private static DamageDealt DamageToPlayer(ObjectId source, int player, int amount) =>
        new(source, null, new PlayerId(player), amount, Combat: false, FromCommander: false);
}
