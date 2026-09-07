using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using LandPlayed = MtgEngine.Rules.Events.LandPlayed;
using SpellCast = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The permission a card needs to be cast from anywhere but its owner's hand, CR 601.3 and
/// 601.3a, and what a cost may ask a caster for, CR 601.2b and 601.2c. Every refusal is checked
/// by its rule number rather than its wording.
/// </summary>
public sealed partial class CastingTests
{
    /// <summary>A casting system whose query can match a filter, which the permission's What and a cost's candidates both need.</summary>
    private readonly CastingSystem filtering = new(
        new CastingFakeZones(),
        new CastingBacklogQuery(),
        new PrintedCharacteristics(),
        new RecordingInterpreter());

    [Fact]
    public void A_card_in_the_graveyard_is_refused_without_a_permission_and_cast_with_one()
    {
        // CR 601.3: a card is cast from its owner's hand unless an effect says otherwise, and
        // CR 601.3a: an effect that says a player may cast it is that permission. The effect is
        // MayPlayFromZone, which nothing read until now.
        var table = new CastingTable();
        var spell = table.Add(TestFaces.Sorcery(), CastingTable.A, CardZone.Graveyard);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));
        var command = new CastSpell(CastingTable.A, spell);

        Assert.Equal("601.3", casting.Check(table.State, command)?.Rule);

        table.Apply([CastingTestsPermissions.ToPlay(table.State, spell, CardZone.Graveyard)]);
        var cast = casting.Cast(table.State, command);

        var moved = Assert.Single(cast.Events.OfType<ObjectMoved>(), m => m.From == spell);
        Assert.Equal(CardZone.Stack, cast.State.Objects[moved.To.Id].Zone.Zone);
        Assert.Equal(CardZone.Graveyard, Assert.IsType<SpellCast>(cast.Events.Last()).From);
    }

    [Fact]
    public void A_permission_that_pays_no_mana_cost_records_free_and_spends_nothing()
    {
        // CR 118.9: an effect that says a card may be cast without paying its mana cost is both
        // the permission of CR 601.3a and the announcement that no mana is paid, so the command
        // need not say so itself. The plan records "free" on the spell.
        var table = new CastingTable();
        var spell = table.Add(TestFaces.Sorcery(), CastingTable.A, CardZone.Graveyard);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));
        table.Apply([CastingTestsPermissions.ToPlay(table.State, spell, CardZone.Graveyard, free: true)]);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, spell));

        var top = cast.State.Objects[StackZone.Top(cast.State)!.Value];
        Assert.Contains("free", top.Stack!.OptionalCostsPaid!);
        Assert.Equal(2, cast.State.Player(CastingTable.A).Mana.Colorless);
        Assert.DoesNotContain(cast.Events, e => e is ManaChanged);
    }

    [Fact]
    public void A_permission_given_to_another_player_does_not_let_this_one_cast()
    {
        // CR 601.3a: the permission is one player's. An effect that applies to somebody else is
        // not a permission this caster has, so the zone check refuses exactly as with none.
        var table = new CastingTable();
        var spell = table.Add(TestFaces.Sorcery(), CastingTable.A, CardZone.Graveyard);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));
        table.Apply([CastingTestsPermissions.ToPlay(table.State, spell, CardZone.Graveyard, who: CastingTable.B)]);

        Assert.Equal("601.3", casting.Check(table.State, new CastSpell(CastingTable.A, spell))?.Rule);
    }

    [Fact]
    public void A_permission_for_another_zone_does_not_let_a_card_be_cast_from_this_one()
    {
        // CR 601.3a: the effect names the zone it allows. Exile is not the graveyard, so a card
        // in the graveyard is still refused by CR 601.3.
        var table = new CastingTable();
        var spell = table.Add(TestFaces.Sorcery(), CastingTable.A, CardZone.Graveyard);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));
        table.Apply([CastingTestsPermissions.ToPlay(table.State, spell, CardZone.Exile)]);

        Assert.Equal("601.3", casting.Check(table.State, new CastSpell(CastingTable.A, spell))?.Rule);
    }

    [Fact]
    public void A_land_is_played_from_a_zone_a_permission_names()
    {
        // CR 305.1 and 601.3a: a land is played from its owner's hand unless an effect says they
        // may play it from where it is — the same permission, read by the same code, so a land
        // and a spell cannot disagree about what it allows.
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land(), CastingTable.A, CardZone.Graveyard);

        Assert.Equal("305.1", casting.Check(table.State, new PlayLand(CastingTable.A, land))?.Rule);

        table.Apply([CastingTestsPermissions.ToPlay(table.State, land, CardZone.Graveyard)]);
        var played = casting.PlayLand(table.State, new PlayLand(CastingTable.A, land));

        var permanent = Assert.IsType<LandPlayed>(played.Events.Last());
        Assert.Equal(ZoneKey.Of(CardZone.Battlefield, CastingTable.A), played.State.Objects[permanent.Land].Zone);
        Assert.Equal(1, played.State.Player(CastingTable.A).LandsPlayedThisTurn);
    }

    [Fact]
    public void A_cost_that_names_objects_the_command_does_not_carry_is_refused_by_name()
    {
        // CR 601.2b and 601.2c: an additional cost is announced with the spell and the objects
        // that pay it are chosen then, so a command carrying none for a cost that needs a choice
        // is not a complete announcement. The casting system is the one place that knows this;
        // the interpreter's may-cast question hands the command over and relays the refusal
        // (seam-requests/E9f.md § 2), which is why the reason names the cost.
        var table = new CastingTable();
        var spell = table.Add(CastingBacklogCards.SacrificeSorcery(), CastingTable.A, CardZone.Hand);
        table.Add(CastingBacklogCards.Beast(), CastingTable.A, CardZone.Battlefield);
        table.Add(CastingBacklogCards.Beast(), CastingTable.A, CardZone.Battlefield);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));

        var refusal = filtering.Check(table.State, new CastSpell(CastingTable.A, spell));

        Assert.Equal("601.2c", refusal?.Rule);
        Assert.Contains("sacrifice", refusal!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cost_that_names_no_objects_is_accepted_with_none_on_the_command()
    {
        // CR 601.2b: an additional cost of paying life needs no object, so the same command that
        // cannot announce a sacrifice announces this one. Narrowing the may-cast refusal to the
        // costs that really need objects is the whole point of asking the casting system.
        var table = new CastingTable();
        var spell = table.Add(CastingTestsPermissions.PayLifeSorcery(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));

        Assert.Null(filtering.Check(table.State, new CastSpell(CastingTable.A, spell)));

        var cast = filtering.Cast(table.State, new CastSpell(CastingTable.A, spell));
        Assert.Equal(18, cast.State.Player(CastingTable.A).Life);
    }

    [Fact]
    public void A_free_cast_still_pays_the_additional_cost_the_card_prints()
    {
        // CR 601.2b: an additional cost is paid whether or not the mana cost is, so a spell cast
        // for free still sacrifices, and the object that pays it comes off the command (601.2c).
        // The free cast is the instructed one, CR 608.2g and 118.9: the flag is the resolving
        // effect's announcement, and a player's command alone can never carry it (601.3).
        var table = new CastingTable();
        var spell = table.Add(CastingBacklogCards.SacrificeSorcery(), CastingTable.A, CardZone.Hand);
        var kept = table.Add(CastingBacklogCards.Beast(), CastingTable.A, CardZone.Battlefield);
        var given = table.Add(CastingBacklogCards.Beast(), CastingTable.A, CardZone.Battlefield);

        var cast = filtering.CastInstructed(
            table.State,
            new CastSpell(CastingTable.A, spell, CostObjects: [given], WithoutPayingManaCost: true));

        var sacrificed = Assert.Single(cast.Events.OfType<ObjectLeftBy>(), e => e.Verb == "sacrificed");
        Assert.Equal(given, sacrificed.Object);
        Assert.Equal(CardZone.Battlefield, cast.State.Objects[kept].Zone.Zone);
        Assert.Contains("free", cast.State.Objects[StackZone.Top(cast.State)!.Value].Stack!.OptionalCostsPaid!);
    }
}

/// <summary>
/// The permission effects and the card <see cref="CastingTests"/> needs for CR 601.3a, built here
/// rather than read from <c>cards/</c>: CARD_ENGINE_PLAN.md § "A card is data or it does not
/// exist" keeps card names out of the engine and out of the tests that stand in for it.
/// </summary>
internal static class CastingTestsPermissions
{
    /// <summary>
    /// "You may cast that card from your graveyard this turn", CR 601.3a, as the interpreter's
    /// may-cast arm creates it: the card the resolution named on <c>AppliesTo</c> and the player
    /// on <c>AppliesToPlayers</c>, with the zone on the effect itself.
    /// </summary>
    public static ContinuousEffectCreated ToPlay(GameState state, ObjectId card, CardZone from, bool free = false, PlayerId? who = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new ContinuousEffectCreated(new ActiveEffect(
            1,
            new MayPlayFromZone(new You(), from, WithoutPayingManaCost: free),
            null,
            who ?? CastingTable.A,
            [card],
            [who ?? CastingTable.A],
            new UntilEndOfTurn(),
            state.NextTimestamp,
            FromStaticAbility: false));
    }

    /// <summary>A sorcery whose additional cost is paying life, CR 601.2b: a cost that names no object.</summary>
    public static CardDefinition PayLifeSorcery() => new(
        "CASTING TESTS TOLL",
        "Casting Tests Toll",
        Layout.Normal,
        [
            new Face(
                "Casting Tests Toll",
                FaceRole.Main,
                new TypeLine(CardType.Sorcery),
                "{2}",
                AdditionalCosts: [new PayLife(new Fixed(2))]),
        ]);
}
