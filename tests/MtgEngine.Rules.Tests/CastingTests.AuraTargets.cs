using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using SpellCastEvent = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>CR 601.2c with 702.5b: the object half of an Aura's legality is answered as the spell is announced, not on resolution.</summary>
public sealed class CastingAuraTargetTests
{
    private readonly CastingSystem casting = new(
        new CastingFakeZones(),
        new CastingBacklogQuery(),
        new PrintedCharacteristics(),
        new RecordingInterpreter());

    [Fact]
    [RuleRef("601.2c")]
    public void An_aura_cast_at_an_object_its_enchant_ability_does_not_admit_is_refused()
    {
        var table = new CastingTable();
        var aura = table.Add(AuraResolutionCards.EnchantCreature(), CastingTable.A, CardZone.Hand);
        var land = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));

        var refused = Assert.Throws<RefusedException>(() => casting.Cast(table.State, new CastSpell(CastingTable.A, aura, Targets: [land])));

        Assert.Equal("601.2c", refused.Rule);
        Assert.Equal("601.2c", casting.Check(table.State, new CastSpell(CastingTable.A, aura, Targets: [land]))?.Rule);
    }

    [Fact]
    [RuleRef("601.2c")]
    public void An_aura_cast_at_a_card_that_is_not_on_the_battlefield_is_refused()
    {
        var table = new CastingTable();
        var aura = table.Add(AuraResolutionCards.EnchantCreature(), CastingTable.A, CardZone.Hand);
        var inHand = table.Add(TestFaces.Creature(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));

        var refused = Assert.Throws<RefusedException>(() => casting.Cast(table.State, new CastSpell(CastingTable.A, aura, Targets: [inHand])));

        Assert.Equal("601.2c", refused.Rule);
    }

    [Fact]
    [RuleRef("702.5b")]
    public void An_aura_cast_at_an_object_its_enchant_ability_admits_is_cast()
    {
        var table = new CastingTable();
        var aura = table.Add(AuraResolutionCards.EnchantCreature(), CastingTable.A, CardZone.Hand);
        var bear = table.Add(TestFaces.Creature(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, aura, Targets: [bear]));

        Assert.Single(cast.Events.OfType<SpellCastEvent>());
    }
}
