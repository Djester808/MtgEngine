using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using SpellCast = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A spell whose printed cost carries a Phyrexian symbol, CR 107.4f: it is cast for one mana of
/// the symbol's colour or for 2 life, the payer chooses per symbol as the spell is announced
/// (CR 601.2h), and a player with less than 2 life cannot choose the life (CR 119.4).
/// </summary>
public sealed partial class CastingTests
{
    [Fact]
    public void A_phyrexian_cost_is_paid_with_its_color_when_the_pool_has_it()
    {
        // CR 107.4f: the mana half. The caster's life is untouched.
        var table = new CastingTable();
        var card = table.Add(TestFaces.Instant("{G/P}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, 0));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card));
        Assert.Contains(cast.Events, e => e is SpellCast);
        Assert.DoesNotContain(cast.Events, e => e is LifeChanged);
        Assert.Equal(20, cast.State.Player(CastingTable.A).Life);
        Assert.Equal(0, cast.State.Player(CastingTable.A).Mana.Total);
    }

    [Fact]
    public void A_phyrexian_cost_is_paid_with_two_life_when_the_pool_is_empty()
    {
        // CR 107.4f: a spell whose whole cost is Phyrexian is cast for no mana at all.
        var table = new CastingTable();
        var card = table.Add(TestFaces.Instant("{G/P}"), CastingTable.A, CardZone.Hand);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card));
        var paid = Assert.Single(cast.Events.OfType<LifeChanged>());
        Assert.Equal(-2, paid.Delta);
        Assert.Equal(18, cast.State.Player(CastingTable.A).Life);
        Assert.Contains(cast.Events, e => e is SpellCast);
    }

    [Fact]
    public void A_phyrexian_cost_the_caster_can_pay_neither_way_is_refused()
    {
        // CR 119.4: a player at 1 life cannot pay 2, and CR 601.2h forbids a partial payment, so
        // the cast is refused naming the symbol's own rule rather than half made.
        var table = new CastingTable();
        var card = table.Add(TestFaces.Instant("{G/P}"), CastingTable.A, CardZone.Hand);
        table.Apply([new LifeChanged(CastingTable.A, -19, "test")]);

        Assert.Equal(1, table.State.Player(CastingTable.A).Life);
        Assert.Equal("107.4f", casting.Check(table.State, new CastSpell(CastingTable.A, card))?.Rule);
    }

    [Fact]
    public void A_caster_who_announces_the_life_keeps_the_mana()
    {
        // CR 601.2h: the choice is announced with the command, on the same CostChoices channel
        // every other cost choice travels, so no payment is ever suspended to ask for it.
        var table = new CastingTable();
        var card = table.Add(TestFaces.Instant("{G/P}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, 0));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card, CostChoices: ["life"]));
        Assert.Equal(-2, Assert.Single(cast.Events.OfType<LifeChanged>()).Delta);
        Assert.Equal(1, cast.State.Player(CastingTable.A).Mana.Total);
    }
}
