using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using SpellCast = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Casting a card that has more than one face, and paying for one with a cost keyword: CR 601.3b
/// chooses the face, CR 712.11c and 715.3a put only that face on the stack, CR 715.3d and 720.3d
/// say where it goes as it resolves, and CR 118.9, 601.2b and 702.40a decide what it cost. Each
/// test names its rule, and every refusal is asserted by rule number rather than by wording.
/// </summary>
public sealed partial class FaceCastingTests
{
    private readonly RecordingInterpreter interpreter = new();
    private readonly CastingSystem casting;

    public FaceCastingTests() =>
        casting = new CastingSystem(new FaceCastingZones(), new FaceCastingQuery(), new PrintedCharacteristics(), interpreter);

    [Fact]
    public void A_modal_card_cast_as_its_back_face_is_that_face_on_the_stack()
    {
        // CR 712.11b: the caster chooses which face; CR 712.11c: only that face is put on the
        // stack, so the spell's cost and characteristics are the back face's.
        var table = new CastingTable();
        var card = table.Add(FaceCastingFaces.Modal(front: "{1}{G}", back: "{2}{U}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 1, 0, 0, 0, 2));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card, FaceIndex: 1));

        var spell = Assert.IsType<SpellCast>(cast.Events.Last());
        var onStack = cast.State.Objects[spell.Spell];
        Assert.Equal(1, onStack.FaceIndex);
        Assert.Equal("Test Modal Back", onStack.Face.Name);
        Assert.Equal(ManaPool.Empty, cast.State.Player(CastingTable.A).Mana);

        // CR 202.3a and 601.2h: what was actually spent is kept on the spell.
        var entry = onStack.Stack;
        Assert.NotNull(entry);
        Assert.Equal(CardZone.Hand, entry.CastFrom);
        Assert.Equal(1, entry.ManaSpent?[Color.Blue]);
        Assert.Equal(2, entry.ManaSpent?[Color.Colorless]);
    }

    [Fact]
    public void A_face_that_cannot_be_cast_from_the_hand_is_refused_naming_601_3b()
    {
        // CR 712.11: a transforming double-faced card is cast with its front face up. Its back
        // face is never an option, unlike a modal card's.
        var table = new CastingTable();
        var card = table.Add(FaceCastingFaces.Transforming(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 4, 0, 4));

        Assert.Equal("601.3b", casting.Check(table.State, new CastSpell(CastingTable.A, card, FaceIndex: 1))?.Rule);
        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, card)));
    }

    [Fact]
    public void A_land_face_of_a_modal_card_is_not_cast()
    {
        // CR 712.12: the land face is played as a land; it is never cast, whichever way it is asked for.
        var table = new CastingTable();
        var card = table.Add(FaceCastingFaces.ModalLand(), CastingTable.A, CardZone.Hand);

        Assert.Equal("712.12", casting.Check(table.State, new CastSpell(CastingTable.A, card, FaceIndex: 1))?.Rule);

        // It is played instead, and a card with one land face needs no choice on the command;
        // CastingChoiceTests holds the face the permanent then shows.
        Assert.Null(casting.Check(table.State, new PlayLand(CastingTable.A, card)));
    }

    [Fact]
    public void An_adventure_resolves_into_exile_and_the_creature_is_then_cast_from_there()
    {
        // CR 715.3a: the Adventure half alone is on the stack. CR 715.3d: it is exiled as it
        // resolves and its owner may cast the card as a creature from exile afterwards.
        var table = new CastingTable();
        var card = table.Add(FaceCastingFaces.Adventurer(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, 0));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card, FaceIndex: 1));
        var spell = Assert.IsType<SpellCast>(cast.Events.Last());
        Assert.Equal(1, cast.State.Objects[spell.Spell].FaceIndex);

        var resolved = casting.ResolveTop(cast.State);
        var exiled = Assert.Single(resolved.State.Objects.Values, o => o.Zone.Zone == CardZone.Exile);
        Assert.True(StackZone.IsEmpty(resolved.State));

        table.Apply(resolved.Events);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, 1));
        var creature = casting.Cast(table.State, new CastSpell(CastingTable.A, exiled.Id));

        var second = Assert.IsType<SpellCast>(creature.Events.Last());
        Assert.Equal(0, creature.State.Objects[second.Spell].FaceIndex);
        Assert.Equal(CardZone.Exile, creature.State.Objects[second.Spell].Stack?.CastFrom);
    }

    [Fact]
    public void A_spell_cast_for_its_flashback_cost_comes_from_the_graveyard_and_is_exiled()
    {
        // CR 702.34a: flashback lets the card be cast from the graveyard for its flashback cost,
        // and exiles it instead of putting it anywhere else as it resolves.
        var table = new CastingTable();
        var card = table.Add(FaceCastingFaces.Flashback(cost: "{2}{R}", flashback: "{4}{R}"), CastingTable.A, CardZone.Graveyard);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 1, 0, 4));

        // CR 601.3: without the permission the keyword gives, a card in a graveyard is not castable.
        Assert.Equal("601.3", casting.Check(table.State, new CastSpell(CastingTable.A, card))?.Rule);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card, AlternativeCost: KeywordKind.Flashback));
        var spell = Assert.IsType<SpellCast>(cast.Events.Last());
        var entry = cast.State.Objects[spell.Spell].Stack;
        Assert.NotNull(entry);
        Assert.Equal(CardZone.Graveyard, entry.CastFrom);
        Assert.Contains("Flashback", entry.OptionalCostsPaid ?? []);
        Assert.Equal(ManaPool.Empty, cast.State.Player(CastingTable.A).Mana);

        var resolved = casting.ResolveTop(cast.State);
        Assert.Single(resolved.State.Objects.Values, o => o.Zone.Zone == CardZone.Exile);
        Assert.DoesNotContain(resolved.State.Objects.Values, o => o.Zone.Zone == CardZone.Graveyard);
    }

    [Fact]
    public void A_kicked_spell_says_so_and_the_extra_mana_was_paid()
    {
        // CR 702.32a and 601.2b: the kicker cost is chosen and paid as the spell is cast, and
        // CR 702.32c asks later whether it was.
        var table = new CastingTable();
        var card = table.Add(FaceCastingFaces.Kicker(cost: "{1}{G}", kicker: "{2}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, 3));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card, OptionalCosts: [KeywordKind.Kicker]));

        var spell = Assert.IsType<SpellCast>(cast.Events.Last());
        var entry = cast.State.Objects[spell.Spell].Stack;
        Assert.NotNull(entry);
        Assert.Contains("Kicker", entry.OptionalCostsPaid ?? []);
        Assert.Equal(4, entry.ManaSpent?.Values.Sum());
        Assert.Equal(ManaPool.Empty, cast.State.Player(CastingTable.A).Mana);
    }

    [Fact]
    public void A_kicker_that_the_pool_cannot_cover_is_refused_and_an_unkicked_cast_is_not()
    {
        var table = new CastingTable();
        var card = table.Add(FaceCastingFaces.Kicker(cost: "{1}{G}", kicker: "{2}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, 1));

        Assert.Equal("601.2h", casting.Check(table.State, new CastSpell(CastingTable.A, card, OptionalCosts: [KeywordKind.Kicker]))?.Rule);

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card));
        var spell = Assert.IsType<SpellCast>(cast.Events.Last());
        Assert.Empty(cast.State.Objects[spell.Spell].Stack?.OptionalCostsPaid ?? []);
    }

    [Fact]
    public void Affinity_for_artifacts_reduces_the_generic_cost_by_the_artifacts_controlled()
    {
        // CR 702.40a: {1} less for each artifact its controller controls, and CR 601.2f: the
        // reduction comes off the generic part only.
        var table = new CastingTable();
        var card = table.Add(FaceCastingFaces.Affinity("{4}"), CastingTable.A, CardZone.Hand);
        table.Add(FaceCastingFaces.Trinket(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        table.Add(FaceCastingFaces.Trinket(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, card));

        var spell = Assert.IsType<SpellCast>(cast.Events.Last());
        Assert.Equal(2, cast.State.Objects[spell.Spell].Stack?.ManaSpent?[Color.Colorless]);
        Assert.Equal(ManaPool.Empty, cast.State.Player(CastingTable.A).Mana);
    }

    [Fact]
    public void Affinity_counts_only_the_casters_own_permanents()
    {
        // CR 702.40a: "for each artifact you control". An opponent's artifacts are not counted,
        // so two mana is short of the full four.
        var table = new CastingTable();
        var card = table.Add(FaceCastingFaces.Affinity("{4}"), CastingTable.A, CardZone.Hand);
        table.Add(FaceCastingFaces.Trinket(), CastingTable.B, CardZone.Battlefield, summoningSick: false);
        table.Add(FaceCastingFaces.Trinket(), CastingTable.B, CardZone.Battlefield, summoningSick: false);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));

        Assert.Equal("601.2h", casting.Check(table.State, new CastSpell(CastingTable.A, card))?.Rule);
    }

    [Fact]
    public void An_alternative_cost_this_slice_cannot_pay_is_refused_by_name()
    {
        // The rule the plan turns on: never a silent success. Evoke is a real alternative cost
        // with a rider this slice does not run, so it is refused naming CR 702.72.
        var table = new CastingTable();
        var evoke = FaceCastingFaces.Kicker(cost: "{2}{G}", kicker: "{1}");
        var card = table.Add(
            evoke with { Faces = [evoke.Faces[0] with { Abilities = [new KeywordAbility(KeywordKind.Evoke, Cost: "{G}")] }] },
            CastingTable.A,
            CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 3, 0));

        var refusal = casting.Check(table.State, new CastSpell(CastingTable.A, card, AlternativeCost: KeywordKind.Evoke));
        Assert.Equal(MtgEngine.Rules.Keywords.KeywordSemantics.RuleOf(KeywordKind.Evoke), refusal?.Rule);
    }
}
