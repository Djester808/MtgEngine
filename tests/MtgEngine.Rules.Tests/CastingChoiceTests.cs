using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using LandPlayed = MtgEngine.Rules.Events.LandPlayed;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The choices a command now carries into casting: which objects pay a cost that names some
/// (CR 601.2c), which face of a modal double-faced card is played as a land (CR 712.12), and the
/// opponent a gift is promised to (CR 702.174b) — plus the one restriction the state cannot keep
/// yet, CR 106.6a's restricted mana, which is named here rather than left silent. Every refusal
/// is checked by its rule number, never by its wording.
/// </summary>
public sealed class CastingChoiceTests
{
    private readonly CastingBacklogQuery query = new();
    private readonly CastingSystem casting = new(
        new CastingChoiceZones(),
        new CastingBacklogQuery(),
        new PrintedCharacteristics(),
        new RecordingInterpreter());

    [Fact]
    public void A_sacrifice_cost_is_paid_with_the_permanent_the_command_named()
    {
        // CR 601.2c: the player chooses which of two legal creatures pays, and the command says
        // which. Before CostObjects the same board was refused rather than chosen from.
        var table = new CastingTable();
        var spell = table.Add(CastingBacklogCards.SacrificeSorcery(), CastingTable.A, CardZone.Hand);
        var kept = table.Add(CastingBacklogCards.Beast(), CastingTable.A, CardZone.Battlefield);
        var given = table.Add(CastingBacklogCards.Beast(), CastingTable.A, CardZone.Battlefield);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, spell, CostObjects: [given]));

        var sacrificed = Assert.Single(cast.Events.OfType<ObjectLeftBy>(), e => e.Verb == "sacrificed");
        Assert.Equal(given, sacrificed.Object);
        Assert.Equal(CardZone.Battlefield, cast.State.Objects[kept].Zone.Zone);
        var moved = Assert.Single(cast.Events.OfType<ObjectMoved>(), m => m.From == given);
        Assert.Equal(ZoneKey.Of(CardZone.Graveyard, CastingTable.A), cast.State.Objects[moved.To.Id].Zone);
        Assert.Equal(CardZone.Stack, cast.State.Objects[StackZone.Top(cast.State)!.Value].Zone.Zone);
    }

    [Fact]
    public void A_sacrifice_cost_cannot_be_paid_with_an_opponents_permanent()
    {
        // CR 701.17a: a player may sacrifice only a permanent they control, so an object that is
        // not legal for the cost is refused rather than taken.
        var table = new CastingTable();
        var spell = table.Add(CastingBacklogCards.SacrificeSorcery(), CastingTable.A, CardZone.Hand);
        table.Add(CastingBacklogCards.Beast(), CastingTable.A, CardZone.Battlefield);
        var theirs = table.Add(CastingBacklogCards.Beast(), CastingTable.B, CardZone.Battlefield);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));

        var command = new CastSpell(CastingTable.A, spell, CostObjects: [theirs]);

        Assert.Equal("601.2c", casting.Check(table.State, command)?.Rule);
        Assert.Equal(CardZone.Battlefield, table.State.Objects[theirs].Zone.Zone);
    }

    [Fact]
    public void A_discard_cost_is_paid_with_the_card_the_command_named()
    {
        // CR 701.9a: the named card goes from its owner's hand to their graveyard, and no other
        // card in that hand is touched.
        var table = new CastingTable();
        var spell = table.Add(CastingChoiceCards.DiscardSorcery(), CastingTable.A, CardZone.Hand);
        var kept = table.Add(CastingChoiceCards.Spare(), CastingTable.A, CardZone.Hand);
        var thrown = table.Add(CastingChoiceCards.Spare(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, spell, CostObjects: [thrown]));

        var discarded = Assert.Single(cast.Events.OfType<ObjectLeftBy>(), e => e.Verb == "discarded");
        Assert.Equal(thrown, discarded.Object);
        Assert.Equal(ZoneKey.Of(CardZone.Hand, CastingTable.A), cast.State.Objects[kept].Zone);
        var moved = Assert.Single(cast.Events.OfType<ObjectMoved>(), m => m.From == thrown);
        Assert.Equal(ZoneKey.Of(CardZone.Graveyard, CastingTable.A), cast.State.Objects[moved.To.Id].Zone);
    }

    [Fact]
    public void The_land_face_of_a_modal_card_is_played_and_the_permanent_shows_that_face()
    {
        // CR 712.12: the land face is played, not cast, and the permanent enters showing it —
        // which only the move can say, so the command's face index is passed through to it.
        var table = new CastingTable();
        var card = table.Add(FaceCastingFaces.ModalLand(), CastingTable.A, CardZone.Hand);

        var played = casting.PlayLand(table.State, new PlayLand(CastingTable.A, card, FaceIndex: 1));

        var moved = Assert.Single(played.Events.OfType<ObjectMoved>());
        var permanent = played.State.Objects[moved.To.Id];
        Assert.Equal(1, permanent.FaceIndex);
        Assert.Equal(ZoneKey.Of(CardZone.Battlefield, CastingTable.A), permanent.Zone);
        Assert.True(permanent.Face.Types.Types.HasFlag(CardType.Land));
        Assert.Contains(played.Events, e => e is LandPlayed);
        Assert.Equal(1, played.State.Player(CastingTable.A).LandsPlayedThisTurn);
    }

    [Fact]
    public void A_face_that_is_not_a_land_cannot_be_played_as_one()
    {
        // CR 712.12: only a land face is played; the front face of that card is cast.
        var table = new CastingTable();
        var card = table.Add(FaceCastingFaces.ModalLand(), CastingTable.A, CardZone.Hand);

        Assert.Equal("712.12", casting.Check(table.State, new PlayLand(CastingTable.A, card, FaceIndex: 0))?.Rule);
    }

    [Fact]
    public void A_gift_promised_as_a_spell_is_cast_is_recorded_on_the_stack()
    {
        // CR 702.174b: the promise is made as the spell is cast and remembered on the spell, so
        // its own text can ask later whether the gift was promised.
        var table = new CastingTable();
        var spell = table.Add(CastingChoiceCards.GiftSorcery(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));

        var command = new CastSpell(
            CastingTable.A,
            spell,
            TargetPlayers: [CastingTable.B],
            OptionalCosts: [KeywordKind.Gift]);
        var cast = casting.Cast(table.State, command);

        var onStack = cast.State.Objects[StackZone.Top(cast.State)!.Value].Stack!;
        Assert.Equal(CastingTable.B, onStack.GiftPromisedTo);
        Assert.Contains(nameof(KeywordKind.Gift), onStack.OptionalCostsPaid!);
    }

    [Fact]
    public void A_gift_with_no_opponent_named_is_refused()
    {
        // CR 702.174b: a gift is promised to an opponent, so a promise to nobody is not one.
        var table = new CastingTable();
        var spell = table.Add(CastingChoiceCards.GiftSorcery(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));

        var command = new CastSpell(CastingTable.A, spell, OptionalCosts: [KeywordKind.Gift]);

        Assert.Equal("702.174b", casting.Check(table.State, command)?.Rule);
    }

    [Fact]
    public void Restricted_mana_is_offered_only_to_a_cost_its_filter_matches()
    {
        // CR 106.6a: mana that may be spent only on what its filter matches is offered to a
        // payment for a matching object and held back from every other one.
        var table = new CastingTable();
        var sorcery = table.Add(CastingBacklogCards.SacrificeSorcery(), CastingTable.A, CardZone.Hand);
        var pool = new ManaPool(0, 0, 0, 0, 0, 1);
        var context = Context(sorcery, CastingTable.A);

        var matching = new RestrictedPool(new CastingChoiceRestricted(
            [new RestrictedBlock(Color.Green, 1, new ObjectFilter(Types: CardType.Sorcery))]));
        Assert.Equal(new ManaPool(0, 0, 0, 0, 1, 1), matching.Spendable(table.State, pool, query, sorcery, context));

        var other = new RestrictedPool(new CastingChoiceRestricted(
            [new RestrictedBlock(Color.Green, 1, new ObjectFilter(Types: CardType.Creature))]));
        Assert.Equal(pool, other.Spendable(table.State, pool, query, sorcery, context));

        // CR 106.6a again: with nothing being paid for, no filter can match, so no block is spendable.
        Assert.Equal(pool, matching.Spendable(table.State, pool, query, null, context));
    }

    [Fact]
    public void Mana_produced_with_a_spend_restriction_is_a_block_now_that_the_pool_can_hold_it()
    {
        // CR 106.6a. This was the wave-6 gap: AddMana.SpendOnlyOn had nowhere to live, so the
        // mana arrived unrestricted and every cost could spend it. ManaPool.Restricted landed in
        // the seams after wave 6 and the production reads the filter now, so the pool holds a
        // block and only what the filter matches is offered a payment (RestrictedManaTests).
        var table = new CastingTable();
        var engine = table.Add(CastingChoiceCards.RestrictedProducer(), CastingTable.A, CardZone.Battlefield, summoningSick: false);

        var activated = casting.Activate(table.State, new ActivateAbility(CastingTable.A, engine, 0));

        var pool = activated.State.Player(CastingTable.A).Mana;
        Assert.Equal(0, pool.Green);
        Assert.Equal(new RestrictedBlock(Color.Green, 1, new ObjectFilter(Types: CardType.Creature)), Assert.Single(pool.Restricted));

        // CR 106.6a: the engine that made it is not a creature spell, so its own cost is not
        // something this mana may pay, and the pool offers the payment nothing.
        Assert.Equal(0, RestrictedPool.Pools.Spendable(activated.State, pool, query, engine, Context(engine, CastingTable.A)).Green);
    }

    private static EffectContext Context(ObjectId source, PlayerId controller) => new(
        source,
        controller,
        [],
        [],
        null,
        ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
        ImmutableDictionary<string, string>.Empty,
        ImmutableDictionary<string, int>.Empty,
        [],
        null);
}
