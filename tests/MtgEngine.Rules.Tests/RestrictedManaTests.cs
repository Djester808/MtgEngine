using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using SpellCast = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Mana produced with a spend restriction, CR 106.6a: "add {G}. Spend this mana only to cast
/// creature spells" is a block in the pool and not ordinary mana, a cost the filter matches may
/// spend it, every other cost may not, the spend comes off the block rather than off the ordinary
/// counts, and CR 106.6b empties it with the rest as the step ends.
/// </summary>
public sealed class RestrictedManaTests
{
    private readonly CastingSystem casting = new(
        new CastingFakeZones(),
        new CastingBacklogQuery(),
        new PrintedCharacteristics(),
        new RecordingInterpreter());

    [Fact]
    public void Mana_produced_with_a_spend_restriction_arrives_as_a_restricted_block()
    {
        // CR 106.6a: the restriction is part of the mana. It goes into the pool as a block, so
        // the pool itself can say what the mana may be spent on; ordinary mana cannot say it.
        var table = new CastingTable();
        var engine = table.Add(RestrictedManaCards.CreatureManaEngine(), CastingTable.A, CardZone.Battlefield, summoningSick: false);

        var activated = casting.Activate(table.State, new ActivateAbility(CastingTable.A, engine, 0));

        var pool = activated.State.Player(CastingTable.A).Mana;
        Assert.Equal(0, pool.Green);
        Assert.Equal(1, pool.Total);
        var block = Assert.Single(pool.Restricted);
        Assert.Equal(Color.Green, block.Color);
        Assert.Equal(1, block.Amount);
        Assert.Equal(CardType.Creature, Assert.IsType<ObjectFilter>(block.SpendOnlyOn).Types);
    }

    [Fact]
    public void Restricted_mana_pays_a_cost_its_filter_matches()
    {
        // CR 106.6a: the block is offered to a payment for what its filter matches, so the
        // creature spell is cast with mana no other cost could have used.
        var table = new CastingTable();
        var engine = table.Add(RestrictedManaCards.CreatureManaEngine(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        var beast = table.Add(TestFaces.Creature("{G}"), CastingTable.A, CardZone.Hand);
        table.Apply(casting.Activate(table.State, new ActivateAbility(CastingTable.A, engine, 0)).Events);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, beast));

        Assert.Contains(cast.Events, e => e is SpellCast);
        var pool = cast.State.Player(CastingTable.A).Mana;
        Assert.Empty(pool.Restricted);
        Assert.Equal(0, pool.Total);
    }

    [Fact]
    public void Restricted_mana_does_not_pay_a_cost_its_filter_does_not_match()
    {
        // CR 106.6a: a block that matches nothing being paid for is not offered at all, so the
        // sorcery is refused for want of mana (CR 601.2h) and the block is still in the pool.
        var table = new CastingTable();
        var engine = table.Add(RestrictedManaCards.CreatureManaEngine(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        var rite = table.Add(TestFaces.Sorcery("{G}"), CastingTable.A, CardZone.Hand);
        table.Apply(casting.Activate(table.State, new ActivateAbility(CastingTable.A, engine, 0)).Events);

        Assert.Equal("601.2h", casting.Check(table.State, new CastSpell(CastingTable.A, rite))?.Rule);
        Assert.Single(table.State.Player(CastingTable.A).Mana.Restricted);
    }

    [Fact]
    public void The_spend_comes_off_the_block_and_leaves_the_ordinary_mana_alone()
    {
        // CR 106.6a: with a restricted {G} and an ordinary {G} in the pool, the {G} of a creature
        // spell comes off the block — it can pay nothing else, while the ordinary mana can still
        // pay anything later in the step.
        var table = new CastingTable();
        var engine = table.Add(RestrictedManaCards.CreatureManaEngine(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        var beast = table.Add(TestFaces.Creature("{G}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, 0));
        table.Apply(casting.Activate(table.State, new ActivateAbility(CastingTable.A, engine, 0)).Events);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, beast));

        var pool = cast.State.Player(CastingTable.A).Mana;
        Assert.Equal(1, pool.Green);
        Assert.Empty(pool.Restricted);
    }

    [Fact]
    public void The_restriction_rides_in_the_pool_and_empties_as_the_step_ends()
    {
        // CR 106.6b: restricted mana is otherwise ordinary mana, so it empties with the rest.
        var table = new CastingTable();
        var engine = table.Add(RestrictedManaCards.CreatureManaEngine(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        table.Apply(casting.Activate(table.State, new ActivateAbility(CastingTable.A, engine, 0)).Events);
        Assert.Single(table.State.Player(CastingTable.A).Mana.Restricted);

        var emptied = casting.EmptyPools(table.State);

        Assert.Contains(emptied.Events, e => e is ManaPoolEmptied);
        Assert.Empty(emptied.State.Player(CastingTable.A).Mana.Restricted);
        Assert.Equal(0, emptied.State.Player(CastingTable.A).Mana.Total);
    }
}

/// <summary>
/// Cards as data for <see cref="RestrictedManaTests"/>, built here rather than read from
/// <c>cards/</c>. The names are deliberately not any printed card's, as CARD_ENGINE_PLAN.md
/// § "A card is data or it does not exist" requires of the engine and of its tests.
/// </summary>
internal static class RestrictedManaCards
{
    /// <summary>"{T}: Add {G}. Spend this mana only to cast creature spells", CR 106.6a.</summary>
    public static CardDefinition CreatureManaEngine() => new(
        "RESTRICTED TEST ENGINE",
        "Restricted Test Engine",
        Layout.Normal,
        [
            new Face(
                "Restricted Test Engine",
                FaceRole.Main,
                new TypeLine(CardType.Artifact),
                "{2}",
                Abilities:
                [
                    new ActivatedAbility(
                        [new TapSelf()],
                        new Cards.AddMana(new ManaProduction(Fixed: "{G}"), SpendOnlyOn: new ObjectFilter(Types: CardType.Creature))),
                ]),
        ]);
}
