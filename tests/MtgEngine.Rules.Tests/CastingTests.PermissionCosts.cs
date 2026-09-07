using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using LandPlayed = MtgEngine.Rules.Events.LandPlayed;
using SpellCast = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The two costs a play permission can carry, CR 118.9: a waived mana cost that still spends the
/// turn's land play on a land (CR 702.75a with 305.2), and a life payment equal to the spell's
/// mana value in place of its mana cost (CR 601.2f), paid at CR 601.2h and refused by CR 119.4
/// when the life is not there. Every permission here is an invented fixture; no card file is read.
/// </summary>
public sealed partial class CastingTests
{
    [Fact]
    [RuleRef("305.2")]
    public void A_land_played_under_a_free_permission_uses_the_turns_land_play()
    {
        // CR 702.75a with 305.1: "you may play the exiled card without paying its mana cost" plays
        // a land as a land, and CR 305.2: the permission is to play, not an extra land play, so a
        // player who has played their land this turn cannot play the permitted one.
        var table = new CastingTable();
        var first = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Hand);
        var hidden = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Exile);
        table.Apply([CastingTestsPermissions.ToPlay(table.State, hidden, CardZone.Exile, free: true)]);

        Assert.Null(casting.Check(table.State, new PlayLand(CastingTable.A, hidden)));

        table.Apply(casting.PlayLand(table.State, new PlayLand(CastingTable.A, first)).Events);

        Assert.Equal("305.2", casting.Check(table.State, new PlayLand(CastingTable.A, hidden))?.Rule);
    }

    [Fact]
    [RuleRef("118.9")]
    public void A_spell_cast_under_a_life_permission_pays_its_mana_value_in_life_and_no_mana()
    {
        // CR 601.2f with 118.9: the permission's rider replaces the mana cost with a life payment
        // equal to the spell's mana value, paid at CR 601.2h; the pool is untouched and the entry
        // records how the spell was cast so "if you cast a spell this way" can be asked.
        var table = new CastingTable();
        var spell = table.Add(TestFaces.Sorcery("{2}"), CastingTable.A, CardZone.Graveyard);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));
        table.Apply([CastingTestsPermissions.ToPlay(table.State, spell, CardZone.Graveyard, payLife: true)]);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, spell));

        Assert.Equal(18, cast.State.Player(CastingTable.A).Life);
        Assert.Equal(2, cast.State.Player(CastingTable.A).Mana.Colorless);
        Assert.DoesNotContain(cast.Events, e => e is ManaChanged);
        var life = Assert.Single(cast.Events.OfType<LifeChanged>());
        Assert.Equal(-2, life.Delta);
        Assert.Equal("cost", life.Reason);
        var top = cast.State.Objects[StackZone.Top(cast.State)!.Value];
        Assert.Contains("life", top.Stack!.OptionalCostsPaid!);
        Assert.DoesNotContain("free", top.Stack!.OptionalCostsPaid!);
    }

    [Fact]
    [RuleRef("202.3e")]
    public void A_life_permission_counts_X_as_it_was_announced()
    {
        // CR 601.2b: X is chosen as the spell is announced, and CR 202.3e: on the stack the mana
        // value counts X as chosen, so the life paid is the printed part plus X.
        var table = new CastingTable();
        var spell = table.Add(TestFaces.Sorcery("{X}{1}"), CastingTable.A, CardZone.Graveyard);
        table.Apply([CastingTestsPermissions.ToPlay(table.State, spell, CardZone.Graveyard, payLife: true)]);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, spell, X: 3));

        Assert.Equal(16, cast.State.Player(CastingTable.A).Life);
    }

    [Fact]
    [RuleRef("119.4")]
    public void A_life_permission_is_refused_below_the_mana_value_and_paid_at_exactly_it()
    {
        // CR 119.4: a player may pay life only if their life total is at least that much. One
        // short is refused as the cost's own refusal and nothing moves; exactly enough is paid
        // down to nothing, which is a legal payment.
        var table = new CastingTable();
        var spell = table.Add(TestFaces.Sorcery("{2}"), CastingTable.A, CardZone.Graveyard);
        table.Apply([CastingTestsPermissions.ToPlay(table.State, spell, CardZone.Graveyard, payLife: true)]);
        table.Apply([new LifeChanged(CastingTable.A, -19, "test")]);

        Assert.Equal("119.4", casting.Check(table.State, new CastSpell(CastingTable.A, spell))?.Rule);
        Assert.Equal(CardZone.Graveyard, table.State.Objects[spell].Zone.Zone);

        table.Apply([new LifeChanged(CastingTable.A, 1, "test")]);
        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, spell));

        Assert.Equal(0, cast.State.Player(CastingTable.A).Life);
        Assert.Single(cast.Events.OfType<SpellCast>());
    }

    [Fact]
    [RuleRef("601.2f")]
    public void A_life_permission_still_pays_the_additional_cost_the_card_prints()
    {
        // CR 601.2f with 118.9c: an alternative cost replaces the mana cost and nothing else, so
        // the additional cost printed on the spell is paid beside the life the permission asks for.
        var table = new CastingTable();
        var spell = table.Add(CastingTestsPermissions.PayLifeSorcery(), CastingTable.A, CardZone.Graveyard);
        table.Apply([CastingTestsPermissions.ToPlay(table.State, spell, CardZone.Graveyard, payLife: true)]);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, spell));

        Assert.Equal(16, cast.State.Player(CastingTable.A).Life);
        Assert.Equal(2, cast.Events.OfType<LifeChanged>().Count());
    }

    [Fact]
    [RuleRef("118.9")]
    public void A_spell_cast_from_hand_pays_mana_while_a_life_permission_stands_elsewhere()
    {
        // The rider applies only to a cast the permission allowed ("if you cast a spell this
        // way"): the same player casting from hand is a different cast and pays as printed.
        var table = new CastingTable();
        var spell = table.Add(TestFaces.Sorcery("{2}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));
        table.Apply([CastingTestsPermissions.ToPlayFromTop(table.State, payLife: true)]);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, spell));

        Assert.Equal(20, cast.State.Player(CastingTable.A).Life);
        Assert.Equal(0, cast.State.Player(CastingTable.A).Mana.Total);
        Assert.DoesNotContain("life", cast.State.Objects[StackZone.Top(cast.State)!.Value].Stack!.OptionalCostsPaid!);
    }

    [Fact]
    [RuleRef("305.1")]
    public void A_land_played_under_a_life_permission_pays_nothing()
    {
        // CR 305.1: a land is played, not cast, so the rider on a spell's mana cost has nothing
        // to replace and the land play costs no life.
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Graveyard);
        table.Apply([CastingTestsPermissions.ToPlay(table.State, land, CardZone.Graveyard, payLife: true)]);

        var played = casting.PlayLand(table.State, new PlayLand(CastingTable.A, land));

        Assert.IsType<LandPlayed>(played.Events.Last());
        Assert.Equal(20, played.State.Player(CastingTable.A).Life);
        Assert.DoesNotContain(played.Events, e => e is LifeChanged);
    }

    [Fact]
    [RuleRef("601.3a")]
    public void A_top_of_library_permission_reaches_the_top_card_and_no_other()
    {
        // CR 601.3a: "from the top of your library" is a permission for one card, and the state
        // keeps every library top-first, so the card on top is castable and the one beneath it is
        // refused for the zone as if there were no permission at all.
        var table = new CastingTable();
        table.Add(TestFaces.Sorcery("{2}"), CastingTable.A, CardZone.Library);
        table.Add(TestFaces.Sorcery("{2}"), CastingTable.A, CardZone.Library);
        var library = table.State.In(ZoneKey.Of(CardZone.Library, CastingTable.A));
        table.Apply([CastingTestsPermissions.ToPlayFromTop(table.State, payLife: true)]);

        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, library[0])));
        Assert.Equal("601.3", casting.Check(table.State, new CastSpell(CastingTable.A, library[1]))?.Rule);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, library[0]));

        Assert.Equal(18, cast.State.Player(CastingTable.A).Life);
        Assert.Equal(CardZone.Library, Assert.IsType<SpellCast>(cast.Events.Last()).From);
    }
}
