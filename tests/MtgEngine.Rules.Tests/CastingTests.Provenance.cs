using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Events;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Mana that remembers where it came from, CR 106.6. Mana is not an object, so the only handle a
/// rule has on a particular mana is the permanent whose ability produced it and the key the card
/// gave it: a spend reports the blocks it took (CR 106.6a), and "activate only using mana produced
/// by ..." reads the source rather than the colour (CR 106.6b).
/// </summary>
public sealed class CastingProvenanceTests
{
    private readonly CastingSystem casting = new(
        new CastingFakeZones(),
        new CastingBacklogQuery(),
        new PrintedCharacteristics(),
        new RecordingInterpreter());

    [Fact]
    public void Mana_from_a_land_remembers_the_permanent_that_produced_it()
    {
        // CR 106.6: the produced mana carries a provenance block naming its source, beside the
        // ordinary counts rather than instead of them — it is still {G} in the pool.
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land("{G}"), CastingTable.A, CardZone.Battlefield, summoningSick: false);

        var activated = casting.Activate(table.State, new ActivateAbility(CastingTable.A, land, 0));

        var pool = activated.State.Player(CastingTable.A).Mana;
        Assert.Equal(1, pool.Green);
        var block = Assert.Single(pool.Provenance);
        Assert.Equal(Color.Green, block.Color);
        Assert.Equal(1, block.Amount);
        Assert.Equal(land, block.Source);
        Assert.Null(block.Key);
    }

    [Fact]
    public void A_key_rides_on_the_mana_the_card_gave_one()
    {
        // CR 106.6a: the key is how a "when that mana is spent" delayed trigger finds its own
        // mana again, so it travels on the block the production put in the pool.
        var table = new CastingTable();
        var engine = table.Add(CastingProvenanceCards.KeyedEngine(), CastingTable.A, CardZone.Battlefield, summoningSick: false);

        var activated = casting.Activate(table.State, new ActivateAbility(CastingTable.A, engine, 0));

        var block = Assert.Single(activated.State.Player(CastingTable.A).Mana.Provenance);
        Assert.Equal("keyed", block.Key);
        Assert.Equal(engine, block.Source);
    }

    [Fact]
    public void Restricted_mana_carries_its_provenance_beside_its_restriction()
    {
        // CR 106.6 and 106.6a: the two blocks answer different questions — what the mana may pay,
        // and where it came from — so mana produced with a restriction carries both.
        var table = new CastingTable();
        var engine = table.Add(RestrictedManaCards.CreatureManaEngine(), CastingTable.A, CardZone.Battlefield, summoningSick: false);

        var activated = casting.Activate(table.State, new ActivateAbility(CastingTable.A, engine, 0));

        var pool = activated.State.Player(CastingTable.A).Mana;
        Assert.Single(pool.Restricted);
        var block = Assert.Single(pool.Provenance);
        Assert.Equal(engine, block.Source);
        Assert.Equal(1, block.Amount);
    }

    [Fact]
    public void Paying_a_cost_spends_the_block_and_the_delta_says_which()
    {
        // CR 106.6a: the spend takes the provenance along with the totals and reports it — the
        // delta carries the block negative, which is how a "when that mana is spent" trigger
        // learns which mana paid, and the pool is left with no block once it is gone.
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land("{G}"), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        var beast = table.Add(TestFaces.Creature("{G}"), CastingTable.A, CardZone.Hand);
        table.Apply(casting.Activate(table.State, new ActivateAbility(CastingTable.A, land, 0)).Events);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, beast));

        var spend = Assert.Single(cast.Events.OfType<ManaChanged>(), m => m.Reason == "cast");
        var block = Assert.Single(spend.Delta.Provenance);
        Assert.Equal(land, block.Source);
        Assert.Equal(-1, block.Amount);
        Assert.Empty(cast.State.Player(CastingTable.A).Mana.Provenance);
    }

    [Fact]
    public void Pay_only_from_pays_with_mana_the_filter_matches()
    {
        // CR 106.6b: "activate only using mana produced by an artifact" reads the source of the
        // mana, so the artifact's own {G} pays a cost no other {G} could.
        var table = new CastingTable();
        var rock = table.Add(CastingProvenanceCards.GreenRock(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        var gate = table.Add(CastingProvenanceCards.ArtifactManaOnly(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        table.Apply(casting.Activate(table.State, new ActivateAbility(CastingTable.A, rock, 0)).Events);

        var activated = casting.Activate(table.State, new ActivateAbility(CastingTable.A, gate, 0));

        var pool = activated.State.Player(CastingTable.A).Mana;
        Assert.Equal(0, pool.Green);
        Assert.Equal(1, pool.White);
    }

    [Fact]
    public void Pay_only_from_refuses_mana_the_filter_does_not_match_naming_the_rule()
    {
        // CR 106.6b: the land's {G} is in the pool and will pay anything else this step, but it
        // cannot pay this cost — and the refusal names the restriction rather than an empty pool,
        // because paying with it would be a different card.
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land("{G}"), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        var gate = table.Add(CastingProvenanceCards.ArtifactManaOnly(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        table.Apply(casting.Activate(table.State, new ActivateAbility(CastingTable.A, land, 0)).Events);

        var refusal = casting.Check(table.State, new ActivateAbility(CastingTable.A, gate, 0));

        Assert.Equal("106.6b", refusal?.Rule);
        Assert.Equal(1, table.State.Player(CastingTable.A).Mana.Green);
    }
}

/// <summary>
/// Cards as data for <see cref="CastingProvenanceTests"/>, built here rather than read from
/// <c>cards/</c>. The names are deliberately not any printed card's, as CARD_ENGINE_PLAN.md
/// § "A card is data or it does not exist" requires of the engine and of its tests.
/// </summary>
internal static class CastingProvenanceCards
{
    /// <summary>"{T}: Add {B}", with a key the card gives the mana, CR 106.6a.</summary>
    public static CardDefinition KeyedEngine() => Artifact(
        "PROVENANCE KEYED ENGINE",
        "Provenance Keyed Engine",
        new ActivatedAbility([new TapSelf()], new Cards.AddMana(new ManaProduction(Fixed: "{B}"), Key: "keyed")));

    /// <summary>"{T}: Add {G}" on an artifact, so the mana's source is an artifact, CR 106.6.</summary>
    public static CardDefinition GreenRock() => Artifact(
        "PROVENANCE GREEN ROCK",
        "Provenance Green Rock",
        new ActivatedAbility([new TapSelf()], new Cards.AddMana(new ManaProduction(Fixed: "{G}"))));

    /// <summary>"{G}: Add {W}. Activate only using mana produced by an artifact", CR 106.6b.</summary>
    public static CardDefinition ArtifactManaOnly() => Artifact(
        "PROVENANCE ARTIFACT GATE",
        "Provenance Artifact Gate",
        new ActivatedAbility(
            [new ManaCost("{G}")],
            new Cards.AddMana(new ManaProduction(Fixed: "{W}")),
            PayOnlyFrom: new ObjectFilter(Types: CardType.Artifact)));

    private static CardDefinition Artifact(string oracleId, string name, Ability ability) => new(
        oracleId,
        name,
        Layout.Normal,
        [new Face(name, FaceRole.Main, new TypeLine(CardType.Artifact), "{2}", Abilities: [ability])]);
}
