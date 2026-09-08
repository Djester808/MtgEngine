using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 29: the two halves a play permission can carry beyond its zone — the once-during-each-of-
/// your-turns limit (CR 116.2a) and an additional cost paid with the spell's (CR 118.8) — and the
/// prohibition half of CR 601.3, a restriction naming two zones at once. Every fixture here is
/// invented in this file; no card file is read and no card name appears.
/// </summary>
public sealed class CastingWave29Tests
{
    private readonly CastingSystem casting;

    public CastingWave29Tests()
    {
        var chars = new PrintedCharacteristics();
        var zones = new CastingFakeZones();
        var query = new Query.Query(chars);
        casting = new CastingSystem(zones, query, chars, new EffectInterpreter(query, zones, new CastingPassThroughReplacements(), chars));
    }

    // --- CR 116.2a: the permission is used once during each of your turns -------------------

    [Fact]
    [RuleRef("116.2a")]
    public void A_once_per_turn_permission_allows_the_first_cast_and_no_second_one()
    {
        // CR 116.2a written on a permission: "once during each of your turns, you may cast …".
        // The limit is on the permission, not on the card, so the second card is refused too —
        // and the refusal is CR 601.3's, because a spent permission is no permission at all.
        var table = Wave29Table(out var first, out var second, CardZone.Graveyard);
        table.Apply([Wave29Permission(table.State, CardZone.Graveyard, oncePerTurn: true)]);

        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, first)));

        table.Apply(casting.Cast(table.State, new CastSpell(CastingTable.A, first)).Events);

        Assert.Equal("601.3", casting.Check(table.State, new CastSpell(CastingTable.A, second))?.Rule);
    }

    [Fact]
    [RuleRef("116.2a")]
    public void A_permission_without_the_limit_allows_a_second_cast_the_same_turn()
    {
        // The same table with the flag off: the limit is a field on the record and not a default,
        // so nothing about casting twice from a zone changed for every permission that lacks it.
        var table = Wave29Table(out var first, out var second, CardZone.Graveyard);
        table.Apply([Wave29Permission(table.State, CardZone.Graveyard, oncePerTurn: false)]);

        table.Apply(casting.Cast(table.State, new CastSpell(CastingTable.A, first)).Events);

        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, second)));
    }

    [Fact]
    [RuleRef("116.2a")]
    public void A_once_per_turn_permission_is_not_spent_by_a_cast_it_did_not_allow()
    {
        // The count is of the casts this permission allowed, so a spell cast from hand — which
        // needs no permission at all (CR 601.3) — leaves the graveyard permission unused.
        var table = Wave29Table(out _, out var fromGraveyard, CardZone.Graveyard);
        var inHand = table.Add(TestFaces.Instant("{1}"), CastingTable.A, CardZone.Hand);
        table.Apply([Wave29Permission(table.State, CardZone.Graveyard, oncePerTurn: true)]);

        table.Apply(casting.Cast(table.State, new CastSpell(CastingTable.A, inHand)).Events);

        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, fromGraveyard)));
    }

    [Fact]
    [RuleRef("116.2a")]
    public void A_once_per_turn_permission_is_not_spent_by_a_refused_announcement()
    {
        // CR 601.2h: nothing is paid and nothing happens when a cast is refused, so the limit is
        // spent only if the cast is actually made — Check runs the whole announcement and throws
        // its state away, and a real cast that cannot pay its mana does the same.
        var table = Wave29Table(out var first, out _, CardZone.Graveyard);
        table.Apply([Wave29Permission(table.State, CardZone.Graveyard, oncePerTurn: true)]);

        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, first)));

        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, first)));
    }

    // --- CR 118.8: the permission's additional cost ------------------------------------------

    [Fact]
    [RuleRef("118.8")]
    public void A_permission_with_an_additional_cost_pays_it_beside_the_mana_cost()
    {
        // CR 118.8 with 601.2f: "…in addition to paying its other costs" is a cost applied to the
        // spell from another effect, paid at the same time as the mana cost. Both are taken.
        var table = Wave29Table(out var spell, out _, CardZone.Graveyard);
        table.Apply([Wave29Permission(table.State, CardZone.Graveyard, addition: new PayLife(new Fixed(3)))]);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, spell));

        Assert.Equal(17, cast.State.Player(CastingTable.A).Life);
        Assert.Equal(3, cast.State.Player(CastingTable.A).Mana.Colorless);
        Assert.Contains(cast.Events, e => e is LifeChanged { Delta: -3, Reason: "cost" });
    }

    [Fact]
    [RuleRef("118.8")]
    public void A_permission_with_an_unpayable_additional_cost_refuses_the_cast()
    {
        // CR 118.3a: a cost that cannot be paid is not paid, and CR 601.2h refuses the whole
        // announcement rather than casting the spell for less than the permission asked.
        var table = Wave29Table(out var spell, out _, CardZone.Graveyard);
        table.Apply([Wave29Permission(table.State, CardZone.Graveyard, addition: new PayLife(new Fixed(40)))]);

        Assert.NotNull(casting.Check(table.State, new CastSpell(CastingTable.A, spell)));
        Assert.Equal(20, table.State.Player(CastingTable.A).Life);
    }

    [Fact]
    [RuleRef("118.8")]
    public void An_additional_cost_beside_a_waived_mana_cost_is_refused()
    {
        // CR 118.8 with 118.9: an additional cost is additional to a mana cost, and a permission
        // that has already set the mana cost aside has nothing for it to be added to. The pair is
        // refused naming the rule rather than one half being silently dropped.
        var table = Wave29Table(out var spell, out _, CardZone.Graveyard);
        table.Apply([Wave29Permission(table.State, CardZone.Graveyard, addition: new PayLife(new Fixed(1)), free: true)]);

        Assert.Equal("118.8", casting.Check(table.State, new CastSpell(CastingTable.A, spell))?.Rule);
    }

    // --- CR 601.3: one restriction over two zones --------------------------------------------

    [Fact]
    [RuleRef("601.3")]
    public void A_restriction_naming_two_zones_prohibits_a_cast_from_either()
    {
        // CR 601.3: "players can't cast spells from graveyards or libraries" is one restriction
        // over two zones, so both are refused by the same effect and neither is a separate
        // ability whose duration could end on its own.
        var table = Wave29Table(out var graveyard, out _, CardZone.Graveyard);
        var library = table.Add(TestFaces.Instant("{1}"), CastingTable.A, CardZone.Library);
        table.Apply([Wave29Permission(table.State, CardZone.Graveyard)]);
        table.Apply([Wave29Permission(table.State, CardZone.Library)]);
        table.Apply([Wave29CantCast(table.State, [CardZone.Graveyard, CardZone.Library])]);

        Assert.Equal("601.3", casting.Check(table.State, new CastSpell(CastingTable.A, graveyard))?.Rule);
        Assert.Equal("601.3", casting.Check(table.State, new CastSpell(CastingTable.A, library))?.Rule);
    }

    [Fact]
    [RuleRef("601.3")]
    public void A_restriction_naming_zones_leaves_a_cast_from_another_zone_alone()
    {
        // The list is exhaustive when it is written: a card in hand is cast as it always was.
        var table = Wave29Table(out _, out _, CardZone.Graveyard);
        var inHand = table.Add(TestFaces.Instant("{1}"), CastingTable.A, CardZone.Hand);
        table.Apply([Wave29CantCast(table.State, [CardZone.Graveyard, CardZone.Library])]);

        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, inHand)));
    }

    [Fact]
    [RuleRef("601.3")]
    public void A_restriction_naming_no_zone_prohibits_every_cast()
    {
        // "You can't cast spells" names no zone, and CR 601.3 is about beginning to cast at all,
        // so the absent list is every zone rather than none.
        var table = Wave29Table(out _, out _, CardZone.Graveyard);
        var inHand = table.Add(TestFaces.Instant("{1}"), CastingTable.A, CardZone.Hand);
        table.Apply([Wave29CantCast(table.State, zones: null)]);

        Assert.Equal("601.3", casting.Check(table.State, new CastSpell(CastingTable.A, inHand))?.Rule);
    }

    [Fact]
    [RuleRef("601.3")]
    public void A_restriction_on_one_player_leaves_the_other_alone()
    {
        // CR 601.3 prohibits a player, so the restriction is read against the caster and nobody
        // else — the effect that names B does not touch A's cast.
        var table = Wave29Table(out _, out _, CardZone.Graveyard);
        var inHand = table.Add(TestFaces.Instant("{1}"), CastingTable.A, CardZone.Hand);
        table.Apply([Wave29CantCast(table.State, zones: null, who: CastingTable.B)]);

        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, inHand)));
    }

    [Fact]
    [RuleRef("601.3")]
    public void A_restriction_with_a_filter_prohibits_only_what_it_names()
    {
        // The filter is asked about the card where the card is now (there is no spell on the stack
        // yet), so "can't cast creature spells" leaves an instant in the same zone castable.
        var table = Wave29Table(out var instant, out _, CardZone.Graveyard);
        var creature = table.Add(TestFaces.Creature("{1}"), CastingTable.A, CardZone.Graveyard);
        table.Apply([Wave29Permission(table.State, CardZone.Graveyard)]);
        table.Apply([Wave29CantCast(table.State, [CardZone.Graveyard], matching: new ObjectFilter(Types: CardType.Creature))]);

        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, instant)));
        Assert.Equal("601.3", casting.Check(table.State, new CastSpell(CastingTable.A, creature))?.Rule);
    }

    [Fact]
    [RuleRef("601.3")]
    public void A_restriction_prohibits_an_instructed_cast_too()
    {
        // CR 601.3 is a conjunction: an instruction to cast (CR 608.2g) is the effect that allows
        // the cast, and allowing it does not answer the prohibition.
        var table = Wave29Table(out var graveyard, out _, CardZone.Graveyard);
        table.Apply([Wave29CantCast(table.State, [CardZone.Graveyard])]);

        var refusal = Assert.Throws<RefusedException>(
            () => casting.CastInstructed(table.State, new CastSpell(CastingTable.A, graveyard)));

        Assert.Equal("601.3", refusal.Rule);
    }

    // --- fixtures ----------------------------------------------------------------------------

    /// <summary>Two instants of the same cost in one zone, with mana enough on the table for both.</summary>
    private static CastingTable Wave29Table(out ObjectId first, out ObjectId second, CardZone zone)
    {
        var table = new CastingTable();
        first = table.Add(TestFaces.Instant("{1}"), CastingTable.A, zone);
        second = table.Add(TestFaces.Instant("{1}"), CastingTable.A, zone);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 4));
        return table;
    }

    /// <summary>
    /// "You may cast cards from that zone", CR 601.3a, as a static ability's permission stands: no
    /// card named, the player on <c>AppliesToPlayers</c>, and the limit and the additional cost on
    /// the record itself.
    /// </summary>
    private static ContinuousEffectCreated Wave29Permission(
        GameState state,
        CardZone from,
        bool oncePerTurn = false,
        Cost? addition = null,
        bool free = false) =>
        new(new ActiveEffect(
            1,
            new MayPlayFromZone(
                new You(),
                from,
                WithoutPayingManaCost: free,
                OncePerTurn: oncePerTurn,
                AdditionalCost: addition),
            null,
            CastingTable.A,
            [],
            [CastingTable.A],
            new UntilEndOfTurn(),
            state.NextTimestamp,
            FromStaticAbility: true));

    /// <summary>"Players can't cast spells from graveyards or libraries", CR 601.3: one restriction, two zones.</summary>
    private static ContinuousEffectCreated Wave29CantCast(
        GameState state,
        IReadOnlyList<CardZone>? zones,
        ObjectFilter? matching = null,
        PlayerId? who = null) =>
        new(new ActiveEffect(
            1,
            new CantCastSpells(new You(), matching, zones),
            null,
            who ?? CastingTable.A,
            [],
            [who ?? CastingTable.A],
            new UntilEndOfTurn(),
            state.NextTimestamp,
            FromStaticAbility: true));
}
