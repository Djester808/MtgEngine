using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 30. What a once-per-turn permission has to be able to count, CR 116.2a: which effect
/// allowed each play, and the land plays, which are not casts and which nothing recorded before.
/// </summary>
public class StateRecordTestsWave30
{
    /// <summary>
    /// CR 116.2a: two permissions over one zone must not share a count, so the cast record says
    /// which one allowed it and never the zone alone.
    /// </summary>
    [Fact]
    public void Two_permissions_over_one_zone_are_told_apart_on_the_cast_record()
    {
        var one = new SpellCastRecord(new ObjectId(1), new PlayerId(0), Zone.Graveyard, Permission: 7);
        var other = new SpellCastRecord(new ObjectId(2), new PlayerId(0), Zone.Graveyard, Permission: 9);

        Assert.NotEqual(one.Permission, other.Permission);
        Assert.Equal(one.From, other.From);
    }

    /// <summary>An ordinary cast needed no permission, so the field is absent rather than a sentinel.</summary>
    [Fact]
    public void An_ordinary_cast_records_no_permission()
    {
        Assert.Null(new SpellCastRecord(new ObjectId(1), new PlayerId(0), Zone.Hand).Permission);
    }

    /// <summary>
    /// CR 305.1 with 116.2a: a land play is not a cast, so it is counted on its own record — the
    /// zone it came from and the permission it was played under.
    /// </summary>
    [Fact]
    public void A_land_played_under_a_permission_is_recorded_with_its_zone_and_permission()
    {
        var played = new LandPlayRecord(new ObjectId(3), new PlayerId(1), Zone.Graveyard, Permission: 7);

        Assert.Equal(Zone.Graveyard, played.From);
        Assert.Equal(7, played.Permission);
        Assert.Null(new LandPlayRecord(new ObjectId(4), new PlayerId(1), Zone.Hand).Permission);
    }

    /// <summary>A turn that has seen nothing has played no lands, and the record starts there.</summary>
    [Fact]
    public void A_fresh_turn_has_played_no_lands()
    {
        Assert.Empty(TurnHistory.Empty.LandsPlayed);
        Assert.Empty(TurnHistory.Empty.SpellsCast);
    }
}
