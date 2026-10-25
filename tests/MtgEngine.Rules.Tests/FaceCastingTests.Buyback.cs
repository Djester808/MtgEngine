using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 702.26a: a buyback spell returns to its owner's hand "as it resolves" — and CR 608.2b says
/// a spell whose every target is illegal does not resolve at all; CR 608.2m then puts it into
/// its owner's graveyard like any other countered spell.
///
/// The destination was read off <c>OptionalCostsPaid</c> alone, on a road that runs for a spell
/// that did NOT resolve, so a fizzled buyback spell was handed back to its caster. Flashback,
/// beside it, is correctly unconditional: CR 702.34a exiles the card "any time it would leave
/// the stack".
/// </summary>
public sealed partial class FaceCastingTests
{
    [Fact]
    [RuleRef("702.26a")]
    public void A_buyback_spell_that_resolves_goes_back_to_its_owners_hand()
    {
        var table = new CastingTable();
        var card = table.Add(FaceCastingFaces.Buyback(), CastingTable.A, CardZone.Hand);
        var victim = table.Add(FaceCastingFaces.Trinket(), CastingTable.B, CardZone.Battlefield);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 1, 0, 3));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card, Targets: [victim], OptionalCosts: [KeywordKind.Buyback]));
        var resolved = casting.ResolveTop(cast.State);

        Assert.Contains(resolved.State.Objects.Values, o => o.Face.Name == "Test Returning Bolt" && o.Zone.Zone == CardZone.Hand);
    }

    /// <summary>The failure this file exists for.</summary>
    [Fact]
    [RuleRef("608.2b")]
    public void A_buyback_spell_whose_target_has_gone_is_countered_into_the_graveyard()
    {
        var table = new CastingTable();
        var card = table.Add(FaceCastingFaces.Buyback(), CastingTable.A, CardZone.Hand);
        var victim = table.Add(FaceCastingFaces.Trinket(), CastingTable.B, CardZone.Battlefield);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 1, 0, 3));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card, Targets: [victim], OptionalCosts: [KeywordKind.Buyback]));

        // The only target leaves before the spell resolves, so CR 608.2b counters it.
        var gone = new Events.ObjectCeasedToExist(victim).Apply(cast.State);
        var resolved = casting.ResolveTop(gone);

        Assert.Contains(resolved.State.Objects.Values, o => o.Face.Name == "Test Returning Bolt" && o.Zone.Zone == CardZone.Graveyard);
        Assert.DoesNotContain(resolved.State.Objects.Values, o => o.Face.Name == "Test Returning Bolt" && o.Zone.Zone == CardZone.Hand);
    }
}
