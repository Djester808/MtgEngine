using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 116.2a's limit counts the plays a PARTICULAR permission allowed.
///
/// The count used to be "a cast this player made this turn out of this permission's zone",
/// which the code itself flagged as approximate. A land play is not a cast, and
/// <c>TurnHistory.LandsPlayed</c> had no writer at all, so a land played under a once-per-turn
/// permission left no trace and the permission granted a second land the same turn. Both
/// records now carry the id of the permission that allowed the play.
/// </summary>
public sealed partial class CastingTests
{
    /// <summary>
    /// "Once during each of your turns, you may play a land from your graveyard": zone-wide, so
    /// it names no card — <c>AppliesTo</c> empty is a permission for everything in that zone.
    /// </summary>
    private static ContinuousEffectCreated LandsFromGraveyard(GameState state, bool oncePerTurn) =>
        new(new ActiveEffect(
            1,
            new MayPlayFromZone(new You(), CardZone.Graveyard, OncePerTurn: oncePerTurn),
            null,
            CastingTable.A,
            [],
            [CastingTable.A],
            new UntilEndOfTurn(),
            state.NextTimestamp,
            FromStaticAbility: true));

    [Fact]
    [RuleRef("116.2a")]
    public void A_land_played_under_a_once_per_turn_permission_spends_it()
    {
        var table = new CastingTable();
        var first = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Graveyard);
        var second = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Graveyard);
        table.Apply([LandsFromGraveyard(table.State, oncePerTurn: true)]);

        table.Apply(casting.PlayLand(table.State, new PlayLand(CastingTable.A, first)).Events);

        // The turn remembers which permission allowed it...
        var record = Assert.Single(table.State.History.LandsPlayed);
        Assert.Equal(CastingTable.A, record.By);
        Assert.Equal(CardZone.Graveyard, record.From);
        Assert.NotNull(record.Permission);

        // ...so the permission is spent, and with no permission left the graveyard is not a zone
        // a land may be played from (CR 305.1).
        Assert.Equal("305.1", casting.Check(table.State, new PlayLand(CastingTable.A, second))?.Rule);
    }

    /// <summary>
    /// CR 305.2: "you may play an additional land on each of your turns". Exploration and its kin
    /// write ExtraLandDrops, which had no reader in the whole solution - the ceiling was a
    /// hardcoded one - so the enchantment resolved onto the battlefield and did nothing.
    /// </summary>
    [Fact]
    [RuleRef("305.2")]
    public void An_extra_land_drop_effect_lets_a_second_land_be_played()
    {
        var table = new CastingTable();
        var first = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Hand);
        var second = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Hand);

        table.Apply(casting.PlayLand(table.State, new PlayLand(CastingTable.A, first)).Events);
        Assert.Equal("305.2", casting.Check(table.State, new PlayLand(CastingTable.A, second))?.Rule);

        // The ceiling the casting system reads comes through ICharacteristics; that the LAYER
        // fills it from an ExtraLandDrops effect is LayerPlayerLimitsTests' half.
        characteristics.Limits[CastingTable.A] = new PlayerLimits(ExtraLandDrops: 1);

        Assert.Null(casting.Check(table.State, new PlayLand(CastingTable.A, second)));
    }

    /// <summary>
    /// CR 305.1: a land is played "during a main phase of their turn when they have priority and
    /// the stack is empty". The priority half was unchecked on this road only - Cast and Activate
    /// both check it - so the active player could play a land while an opponent held priority.
    /// </summary>
    [Fact]
    [RuleRef("305.1")]
    public void A_land_cannot_be_played_while_somebody_else_holds_priority()
    {
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Hand);

        // A is still the active player in their own main phase with an empty stack, but has
        // passed priority to B.
        table.Priority(CastingTable.B);

        Assert.Equal("305.1", casting.Check(table.State, new PlayLand(CastingTable.A, land))?.Rule);

        table.Priority(CastingTable.A);
        Assert.Null(casting.Check(table.State, new PlayLand(CastingTable.A, land)));
    }

    /// <summary>
    /// The CAST road, which is where the regression actually was: a flashback cast comes out of
    /// the graveyard on its own keyword (CR 702.34a) and needs no permission, so it must leave a
    /// once-per-turn graveyard permission untouched.
    /// </summary>
    [Fact]
    [RuleRef("116.2a")]
    public void A_flashback_cast_does_not_spend_a_graveyard_permission()
    {
        var table = new CastingTable();
        var flashback = table.Add(FaceCastingFaces.Flashback(cost: "{2}{R}", flashback: "{4}{R}"), CastingTable.A, CardZone.Graveyard);
        var other = table.Add(TestFaces.Instant("{1}"), CastingTable.A, CardZone.Graveyard);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 1, 0, 8));
        table.Apply([LandsFromGraveyard(table.State, oncePerTurn: true)]);

        table.Apply(casting.Cast(table.State, new CastSpell(CastingTable.A, flashback, AlternativeCost: KeywordKind.Flashback)).Events);

        // The flashback cast took the keyword road, so it names no permission...
        var record = Assert.Single(table.State.History.SpellsCast);
        Assert.Null(record.Permission);

        // ...and the permission is still there for the cast it was actually for.
        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, other)));
    }

    /// <summary>
    /// A cast that took another road out of the same zone must not spend the permission. This is
    /// the failure the first version of the fix introduced: it asked whether a permission EXISTED
    /// for the zone rather than whether one was used, so a flashback cast - which needs no
    /// permission at all (CR 702.34a) - stamped the permission and then refused the graveyard cast
    /// that permission was actually for.
    /// </summary>
    [Fact]
    [RuleRef("116.2a")]
    public void A_cast_that_needed_no_permission_does_not_spend_one()
    {
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Graveyard);
        table.Apply([LandsFromGraveyard(table.State, oncePerTurn: true)]);

        // A land played from HAND takes no permission, so the graveyard permission is untouched.
        var fromHand = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Hand);
        table.Apply(casting.PlayLand(table.State, new PlayLand(CastingTable.A, fromHand)).Events);

        var record = Assert.Single(table.State.History.LandsPlayed);
        Assert.Null(record.Permission);

        // The permission is still there to be used - only the one-land rule stands in the way.
        Assert.Equal("305.2", casting.Check(table.State, new PlayLand(CastingTable.A, land))?.Rule);
    }

    [Fact]
    [RuleRef("116.2a")]
    public void A_permission_without_the_limit_is_not_spent_by_a_land()
    {
        var table = new CastingTable();
        var first = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Graveyard);
        var second = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Graveyard);
        table.Apply([LandsFromGraveyard(table.State, oncePerTurn: false)]);

        table.Apply(casting.PlayLand(table.State, new PlayLand(CastingTable.A, first)).Events);

        // Only the one-land-a-turn rule stops the second one now — CR 305.2, not the permission.
        Assert.Equal("305.2", casting.Check(table.State, new PlayLand(CastingTable.A, second))?.Rule);
    }
}
