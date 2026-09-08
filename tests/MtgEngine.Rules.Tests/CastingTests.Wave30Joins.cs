using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The wave-30 join E1h asked for: a permanent spell resolves through the zone system's own road,
/// so a spell CR 608.3e sent to its owner's graveyard is neither summoning sick nor attached — only
/// a permanent that entered gets the marks (CR 302.6, 303.4f). Every fixture here is invented.
/// </summary>
public sealed partial class CastingTests
{
    /// <summary>A zone system whose resolution road refuses every entry the way CR 608.3e says: the card goes to its owner's graveyard.</summary>
    private sealed class CastingRefusingZones : IZoneSystem
    {
        private readonly CastingFakeZones _plain = new();

        public string Name => "fake zones refusing the battlefield";

        public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where = Placement.Default, bool tapped = false, PlayerId? controller = null) =>
            _plain.Move(state, what, to, where, tapped, controller);

        [RuleRef("608.3e")]
        public Outcome ResolveOntoBattlefield(GameState state, ObjectId what, PlayerId controller, bool faceDown = false) =>
            _plain.Move(state, what, ZoneKey.Of(CardZone.Graveyard, state.Objects[what].Owner));

        public Outcome Create(GameSetup setup, IDefinitionStore store) => throw new NotSupportedException();

        public Outcome Draw(GameState state, PlayerId player, int count) => throw new NotSupportedException();

        public Outcome Shuffle(GameState state, PlayerId player) => throw new NotSupportedException();

        public Outcome CreateToken(GameState state, TokenSpec token, PlayerId controller, bool tapped, bool attacking) => throw new NotSupportedException();
    }

    [Fact]
    [RuleRef("608.3e")]
    public void A_permanent_spell_the_zone_system_sends_to_the_graveyard_is_not_marked_summoning_sick()
    {
        var refusing = new CastingSystem(new CastingRefusingZones(), new FixedAmounts(), new PrintedCharacteristics(), interpreter);
        var table = new CastingTable();
        var card = table.Add(TestFaces.Creature("{1}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));
        table.Apply(refusing.Cast(table.State, new CastSpell(CastingTable.A, card)).Events);

        var resolved = refusing.ResolveTop(table.State);

        // CR 608.3e: the card is in its owner's graveyard, the spell is gone from the stack, and
        // no summoning-sick or attachment mark was written against an object that never entered.
        Assert.Contains(resolved.Events, e => e is StackObjectResolved);
        Assert.True(StackZone.IsEmpty(resolved.State));
        Assert.Single(resolved.State.Objects.Values, o => o.Zone == ZoneKey.Of(CardZone.Graveyard, CastingTable.A));
        Assert.DoesNotContain(resolved.State.Objects.Values, o => o.Zone == ZoneKey.Of(CardZone.Battlefield, CastingTable.A));
        Assert.DoesNotContain(resolved.Events, e => e is ObjectChanged { SummoningSick: true });
    }

    [Fact]
    [RuleRef("302.6")]
    public void A_permanent_spell_that_entered_is_still_marked_summoning_sick_through_the_resolution_road()
    {
        var table = new CastingTable();
        var card = table.Add(TestFaces.Creature("{1}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));
        table.Apply(casting.Cast(table.State, new CastSpell(CastingTable.A, card)).Events);

        var resolved = casting.ResolveTop(table.State);

        var permanent = resolved.State.Objects.Values.Single(o => o.Zone == ZoneKey.Of(CardZone.Battlefield, CastingTable.A));
        Assert.True(permanent.SummoningSick);
        Assert.Single(resolved.Events, e => e is ObjectChanged { SummoningSick: true });
    }
}
