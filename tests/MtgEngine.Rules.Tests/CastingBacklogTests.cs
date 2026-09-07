using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using AbilityActivated = MtgEngine.Rules.Events.AbilityActivated;
using CardZone = MtgEngine.Cards.Zone;
using SpellCast = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The costs the first verify run over the translated cards found the caster could not pay:
/// an additional cost printed on a spell (CR 601.2b), a mana cost the game counts (CR 118.8),
/// a repeatable optional cost (CR 702.32b), the non-mana half of an alternative cost
/// (CR 702.138a) and a restriction on which mana may be spent (CR 106.6b). Each test names the
/// rule it holds and checks a refusal by its rule number, never by its wording.
/// </summary>
public sealed class CastingBacklogTests
{
    private readonly CastingSystem casting = new(
        new CastingFakeZones(),
        new CastingBacklogQuery(),
        new PrintedCharacteristics(),
        new RecordingInterpreter());

    [Fact]
    public void A_sacrifice_printed_as_an_additional_cost_is_paid_before_the_spell_resolves()
    {
        // CR 601.2b and 601.2f: the additional cost is announced with the spell and paid as part
        // of the total cost, so the permanent is in the graveyard while the spell is on the stack.
        var table = new CastingTable();
        var spell = table.Add(CastingBacklogCards.SacrificeSorcery(), CastingTable.A, CardZone.Hand);
        var creature = table.Add(CastingBacklogCards.Beast(), CastingTable.A, CardZone.Battlefield);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, spell));

        var sacrificed = Assert.Single(cast.Events.OfType<ObjectLeftBy>(), e => e.Verb == "sacrificed");
        Assert.Equal(creature, sacrificed.Object);
        var toGraveyard = Assert.Single(cast.Events.OfType<ObjectMoved>(), m => m.To.Zone.Zone == CardZone.Graveyard);
        Assert.Equal(ZoneKey.Of(CardZone.Graveyard, CastingTable.A), cast.State.Objects[toGraveyard.To.Id].Zone);

        // The whole payment precedes the spell being cast, CR 601.2h before 601.2i.
        Assert.True(cast.Events.IndexOf(sacrificed) < cast.Events.FindIndex(e => e is SpellCast));
        Assert.Equal(CardZone.Stack, cast.State.Objects[StackZone.Top(cast.State)!.Value].Zone.Zone);
    }

    [Fact]
    public void A_spell_whose_additional_cost_cannot_be_paid_is_refused()
    {
        // CR 601.2h: a cost that cannot be paid is not paid, and the whole casting is rewound.
        var table = new CastingTable();
        var spell = table.Add(CastingBacklogCards.SacrificeSorcery(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));

        Assert.Equal("601.2h", casting.Check(table.State, new CastSpell(CastingTable.A, spell))?.Rule);
    }

    [Fact]
    public void A_choice_of_which_permanent_to_sacrifice_is_refused_rather_than_made_for_the_player()
    {
        // CR 601.2c: the player chooses. Two legal creatures is a choice the command cannot carry.
        var table = new CastingTable();
        var spell = table.Add(CastingBacklogCards.SacrificeSorcery(), CastingTable.A, CardZone.Hand);
        table.Add(CastingBacklogCards.Beast(), CastingTable.A, CardZone.Battlefield);
        table.Add(CastingBacklogCards.Beast(), CastingTable.A, CardZone.Battlefield);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));

        Assert.Equal("601.2c", casting.Check(table.State, new CastSpell(CastingTable.A, spell))?.Rule);
    }

    [Fact]
    public void A_mana_cost_counted_from_the_game_is_evaluated_when_the_cost_is_announced()
    {
        // CR 118.8 and 601.2h: "pay {X}, where X is this creature's power" is counted now, so a
        // 3/3's ability costs three and a pool of two cannot pay it.
        var table = new CastingTable();
        var source = table.Add(CastingBacklogCards.CountedCostCreature(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));
        Assert.Equal("601.2h", casting.Check(table.State, new ActivateAbility(CastingTable.A, source, 0))?.Rule);

        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));
        var activated = casting.Activate(table.State, new ActivateAbility(CastingTable.A, source, 0));

        Assert.Equal(0, activated.State.Player(CastingTable.A).Mana.Total);
        Assert.Contains(activated.Events, e => e is AbilityActivated);
    }

    [Fact]
    public void A_repeatable_optional_cost_is_paid_twice_and_recorded_twice()
    {
        // CR 702.32b: multikicker may be paid any number of times; KeywordAbility.Repeatable is
        // how the printed text says so, and each payment is its own additional cost, CR 601.2b.
        var table = new CastingTable();
        var spell = table.Add(CastingBacklogCards.MultikickerSorcery(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 3));
        var command = new CastSpell(CastingTable.A, spell, OptionalCosts: [KeywordKind.Kicker, KeywordKind.Kicker]);

        var cast = casting.Cast(table.State, command);

        var onStack = cast.State.Objects[StackZone.Top(cast.State)!.Value];
        Assert.Equal(2, onStack.Stack!.OptionalCostsPaid!.Count(k => k == nameof(KeywordKind.Kicker)));
        Assert.Equal(0, cast.State.Player(CastingTable.A).Mana.Total);
    }

    [Fact]
    public void An_optional_cost_that_is_not_repeatable_cannot_be_paid_twice()
    {
        // CR 702.32a: an ordinary kicker is paid once.
        var table = new CastingTable();
        var spell = table.Add(CastingBacklogCards.MultikickerSorcery(repeatable: false), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 3));
        var command = new CastSpell(CastingTable.A, spell, OptionalCosts: [KeywordKind.Kicker, KeywordKind.Kicker]);

        Assert.Equal("702.33", casting.Check(table.State, command)?.Rule);
    }

    [Fact]
    public void Escape_pays_the_non_mana_half_of_its_cost_by_exiling_cards_from_the_graveyard()
    {
        // CR 702.138a: the escape cost is mana plus exiling other cards from that graveyard, and
        // the escaping card itself is never one of them.
        var table = new CastingTable();
        var spell = table.Add(CastingBacklogCards.EscapeSorcery(), CastingTable.A, CardZone.Graveyard);
        var first = table.Add(CastingBacklogCards.Beast(), CastingTable.A, CardZone.Graveyard);
        var second = table.Add(CastingBacklogCards.Beast(), CastingTable.A, CardZone.Graveyard);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));

        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, spell, AlternativeCost: KeywordKind.Escape));

        var exiled = cast.Events.OfType<ObjectMoved>().Where(m => m.To.Zone.Zone == CardZone.Exile).ToList();
        Assert.Equal([first, second], exiled.Select(m => m.From).OrderBy(id => id.Value).ToList());
        Assert.Equal(CardZone.Stack, cast.State.Objects[StackZone.Top(cast.State)!.Value].Zone.Zone);
        Assert.Equal(0, cast.State.Player(CastingTable.A).Mana.Total);
    }

    [Fact]
    public void Escape_is_refused_when_the_graveyard_has_too_few_cards_to_exile()
    {
        var table = new CastingTable();
        var spell = table.Add(CastingBacklogCards.EscapeSorcery(), CastingTable.A, CardZone.Graveyard);
        table.Add(CastingBacklogCards.Beast(), CastingTable.A, CardZone.Graveyard);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));

        Assert.Equal("601.2h", casting.Check(table.State, new CastSpell(CastingTable.A, spell, AlternativeCost: KeywordKind.Escape))?.Rule);
    }

    [Fact]
    public void An_ability_that_names_the_mana_it_accepts_refuses_every_other_colour()
    {
        // CR 106.6b: a restriction on which mana may be spent is checked as the cost is paid, and
        // the mana it excludes stays in the pool.
        var table = new CastingTable();
        var source = table.Add(CastingBacklogCards.GreenOnlyEngine(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        table.AddMana(CastingTable.A, new ManaPool(1, 0, 0, 0, 0, 0));

        Assert.Equal("106.6b", casting.Check(table.State, new ActivateAbility(CastingTable.A, source, 0))?.Rule);

        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 1, 0));
        var activated = casting.Activate(table.State, new ActivateAbility(CastingTable.A, source, 0));

        // Only the green mana paid; the white is untouched, CR 106.6b.
        Assert.Equal(new ManaPool(1, 0, 0, 0, 0, 0), activated.State.Player(CastingTable.A).Mana);
    }

    [Fact]
    public void A_gift_cannot_be_promised_on_a_card_that_offers_none()
    {
        // CR 702.174b: the promise is made as the spell is cast and remembered on the spell — but
        // only a card with gift offers one. The promise itself is CastingChoiceTests'.
        var table = new CastingTable();
        var spell = table.Add(CastingBacklogCards.MultikickerSorcery(), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 3));

        var command = new CastSpell(CastingTable.A, spell, TargetPlayers: [CastingTable.B], OptionalCosts: [KeywordKind.Gift]);
        Assert.Equal("702.174b", casting.Check(table.State, command)?.Rule);
    }
}
