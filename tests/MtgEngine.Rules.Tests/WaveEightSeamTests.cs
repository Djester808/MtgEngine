using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The seams cut after wave 7, from the builders' requests: an Aura attached to a player
/// (E3e), a halted combat damage batch that can be resumed (E4b), and a lasting permission that
/// also waives the mana cost (E9f). Each is additive; the builders that read them are wave 8's.
/// </summary>
public sealed class WaveEightSeamTests
{
    private static readonly PlayerId P0 = new(0);

    [Fact]
    [RuleRef("303.4f")]
    public void An_object_change_attaches_an_aura_to_a_player_and_a_clear_detaches_it()
    {
        var board = new Board();
        var aura = board.Put(Board.Creature(0, 0, "seam aura"));

        var attached = new ObjectChanged(aura, AttachedToPlayer: P0).Apply(board.State);
        Assert.Equal(P0, attached.Objects[aura].AttachedToPlayer);
        Assert.Null(attached.Objects[aura].AttachedTo);

        var cleared = new ObjectChanged(aura, ClearAttachment: true).Apply(attached);
        Assert.Null(cleared.Objects[aura].AttachedToPlayer);
    }

    [Fact]
    [RuleRef("303.4f")]
    public void An_attachment_to_an_object_replaces_one_to_a_player()
    {
        var board = new Board();
        var aura = board.Put(Board.Creature(0, 0, "seam aura"));
        var bear = board.Put(Board.Creature(2, 2, "seam bear"));

        var onPlayer = new ObjectChanged(aura, AttachedToPlayer: P0).Apply(board.State);
        var onBear = new ObjectChanged(aura, AttachedTo: bear).Apply(onPlayer);

        Assert.Equal(bear, onBear.Objects[aura].AttachedTo);
        Assert.Null(onBear.Objects[aura].AttachedToPlayer);
    }

    [Fact]
    [RuleRef("118.9")]
    public void A_permission_that_waives_the_mana_cost_round_trips()
    {
        var permission = new MayPlayFromZone(new You(), Zone.Graveyard, WithoutPayingManaCost: true);
        var json = JsonSerializer.Serialize<ContinuousEffect>(permission, CardFileSerializer.Options);

        Assert.Contains("withoutPayingManaCost", json, StringComparison.Ordinal);
        var back = Assert.IsType<MayPlayFromZone>(JsonSerializer.Deserialize<ContinuousEffect>(json, CardFileSerializer.Options));
        Assert.True(back.WithoutPayingManaCost);
        Assert.False(new MayPlayFromZone(new You(), Zone.Graveyard).WithoutPayingManaCost);
    }
}
