using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using SpellCast = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// What a question may offer for the costs that name objects, CR 601.2c. <c>Check</c> refuses such
/// a cost by its name; <c>CostChoices</c> says what it is looking for — the objects that could
/// legally pay it and how many it takes — so nothing outside the casting system has to keep its
/// own copy of which costs consume what, and the offer and the payment cannot disagree.
/// </summary>
public sealed class CastingCostChoiceTests
{
    private readonly CastingSystem casting = new(
        new CastingFakeZones(),
        new CastingBacklogQuery(),
        new PrintedCharacteristics(),
        new RecordingInterpreter());

    [Fact]
    public void A_sacrifice_cost_names_the_permanents_that_could_pay_it_and_how_many()
    {
        // CR 601.2c: the player chooses, so the question needs the legal objects and the count.
        // The filter alone could not be offered from — the cost also wants the battlefield and
        // this player's control, which is why the casting system answers and not the caller.
        var table = new CastingTable();
        var rite = table.Add(CastingBacklogCards.SacrificeSorcery(), CastingTable.A, CardZone.Hand);
        var first = table.Add(TestFaces.Creature(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        var second = table.Add(TestFaces.Creature(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        table.Add(TestFaces.Creature(), CastingTable.B, CardZone.Battlefield, summoningSick: false);

        var asked = Assert.Single(casting.CostChoices(table.State, new CastSpell(CastingTable.A, rite)));

        Assert.Equal("permanents to sacrifice", asked.Cost);
        Assert.Equal(1, asked.Count);
        Assert.Equal([first, second], asked.Legal);
    }

    [Fact]
    public void The_objects_the_question_offers_are_the_ones_the_payment_accepts()
    {
        // CR 601.2c: an offer the payment then refuses would be worse than no offer at all, so
        // the answer to the question is cast back and the spell goes on the stack.
        var table = new CastingTable();
        var rite = table.Add(CastingBacklogCards.SacrificeSorcery(), CastingTable.A, CardZone.Hand);
        table.Add(TestFaces.Creature(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        table.Add(TestFaces.Creature(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 2));

        var asked = Assert.Single(casting.CostChoices(table.State, new CastSpell(CastingTable.A, rite)));
        var cast = casting.Cast(table.State, new CastSpell(CastingTable.A, rite, CostObjects: [asked.Legal[0]]));

        Assert.Contains(cast.Events, e => e is SpellCast);
    }

    [Fact]
    public void A_cost_that_names_no_objects_asks_for_nothing()
    {
        // CR 601.2c names only the costs that consume objects. Paying life is not one of them, so
        // there is nothing to offer and the answer is empty rather than an ask with no options.
        var table = new CastingTable();
        var altar = table.Add(CastingCostChoiceCards.LifeAltar(), CastingTable.A, CardZone.Battlefield, summoningSick: false);

        Assert.Empty(casting.CostChoices(table.State, new ActivateAbility(CastingTable.A, altar, 0)));
    }

    [Fact]
    public void An_activated_abilitys_own_object_cost_is_asked_for()
    {
        // CR 602.2a: an ability's costs are announced the same way a spell's are, so the same
        // table answers for both and the interpreter needs one channel, not two.
        var table = new CastingTable();
        var altar = table.Add(CastingCostChoiceCards.SacrificeAltar(), CastingTable.A, CardZone.Battlefield, summoningSick: false);
        var fodder = table.Add(TestFaces.Creature(), CastingTable.A, CardZone.Battlefield, summoningSick: false);

        var asked = Assert.Single(casting.CostChoices(table.State, new ActivateAbility(CastingTable.A, altar, 0)));

        Assert.Equal("permanents to sacrifice", asked.Cost);
        Assert.Equal([fodder], asked.Legal);
    }

    [Fact]
    public void A_command_that_cannot_be_announced_at_all_asks_for_nothing()
    {
        // A question offers what a cost needs; why an action is impossible is Check's answer, so
        // an object that is not there asks for nothing rather than throwing at the question.
        var table = new CastingTable();

        Assert.Empty(casting.CostChoices(table.State, new CastSpell(CastingTable.A, new ObjectId(999))));
    }
}

/// <summary>
/// Cards as data for <see cref="CastingCostChoiceTests"/>, built here rather than read from
/// <c>cards/</c>. The names are deliberately not any printed card's, as CARD_ENGINE_PLAN.md
/// § "A card is data or it does not exist" requires of the engine and of its tests.
/// </summary>
internal static class CastingCostChoiceCards
{
    /// <summary>"Pay 2 life: Add {W}" — a cost that names no objects, CR 119.4.</summary>
    public static CardDefinition LifeAltar() => Artifact(
        "COST CHOICE LIFE ALTAR",
        "Cost Choice Life Altar",
        new ActivatedAbility([new PayLife(new Fixed(2))], new Cards.AddMana(new ManaProduction(Fixed: "{W}"))));

    /// <summary>"Sacrifice a creature: Add {B}" — a cost that names one, CR 601.2c.</summary>
    public static CardDefinition SacrificeAltar() => Artifact(
        "COST CHOICE SACRIFICE ALTAR",
        "Cost Choice Sacrifice Altar",
        new ActivatedAbility(
            [new SacrificeCost(new ObjectFilter(Types: CardType.Creature))],
            new Cards.AddMana(new ManaProduction(Fixed: "{B}"))));

    private static CardDefinition Artifact(string oracleId, string name, Ability ability) => new(
        oracleId,
        name,
        Layout.Normal,
        [new Face(name, FaceRole.Main, new TypeLine(CardType.Artifact), "{2}", Abilities: [ability])]);
}
