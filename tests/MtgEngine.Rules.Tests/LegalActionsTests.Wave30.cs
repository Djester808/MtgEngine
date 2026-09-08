using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;
using LegalActionsSubsystem = MtgEngine.Rules.Legal.LegalActions;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The legal-actions view carries the price of every attacker it offers, CR 508.1d. The reading
/// is the combat system's — the same one <c>DeclareAttackers</c> is charged by — so what is
/// wired here is the asking, not the rule.
/// </summary>
public sealed class LegalActionsCombatTestsWave30
{
    /// <summary>
    /// CR 508.1d: a creature that can't attack unless its controller pays a cost <em>can</em>
    /// attack, so it is offered as an attacker and the price is offered beside it. The untaxed
    /// creature in the same declaration has no entry, which is what keeps an ordinary combat's
    /// list empty.
    /// </summary>
    [Fact]
    [RuleRef("508.1d")]
    public void The_view_prices_the_attackers_it_offers()
    {
        var t = new CombatTable();
        var taxed = t.Creature(t.A, 2, 2, statics: [new StaticAbility(new CantAttack(null, UnlessPays: new ManaCost("{2}")))]);
        var free = t.Creature(t.A, 2, 2);
        var declaring = Declaring(t);

        var offered = View(t, declaring, t.Subject);

        Assert.Equal([taxed, free], offered.EligibleAttackers);
        var price = Assert.Single(offered.AttackTaxes);
        Assert.Equal(taxed, price.Attacker);
        Assert.Equal("{2}", price.Cost);
        Assert.False(price.Payable);
    }

    /// <summary>
    /// A composition with no producer says nothing rather than guessing at CR 508.1h — the same
    /// bargain the view makes when it is composed without a combat system. The attackers are still
    /// offered, so a client that cannot show a price still shows the declaration.
    /// </summary>
    [Fact]
    [RuleRef("508.1h")]
    public void Composed_without_a_producer_the_view_offers_no_prices()
    {
        var t = new CombatTable();
        var taxed = t.Creature(t.A, 2, 2, statics: [new StaticAbility(new CantAttack(null, UnlessPays: new ManaCost("{2}")))]);
        var declaring = Declaring(t);

        var blind = View(t, declaring, taxes: null);

        Assert.Equal([taxed], blind.EligibleAttackers);
        Assert.Empty(blind.AttackTaxes);
    }

    /// <summary>
    /// The step and the declaration are the view's own gates (CR 508.1), and a price is only
    /// offered for a creature that is on the list. Outside the declare attackers step there is no
    /// list, so there are no prices either.
    /// </summary>
    [Fact]
    [RuleRef("508.1")]
    public void Outside_the_declaration_nothing_is_offered_and_nothing_is_priced()
    {
        var t = new CombatTable();
        t.Creature(t.A, 2, 2, statics: [new StaticAbility(new CantAttack(null, UnlessPays: new ManaCost("{2}")))]);
        var declaring = Declaring(t);
        var elsewhere = declaring with { Turn = declaring.Turn with { Step = Step.PrecombatMain } };

        var offered = View(t, elsewhere, t.Subject);

        Assert.Empty(offered.EligibleAttackers);
        Assert.Empty(offered.AttackTaxes);
    }

    /// <summary>
    /// The declare attackers step with the priority held elsewhere, so the view answers combat's
    /// half and asks the casting system nothing (CR 508.1: declaring is a turn-based action).
    /// </summary>
    private static GameState Declaring(CombatTable table) =>
        table.State with
        {
            Turn = table.State.Turn with { Number = 1, ActivePlayer = table.A, Step = Step.DeclareAttackers },
            Priority = new PriorityState(table.B, []),
        };

    private static Views.LegalActions View(CombatTable table, GameState state, Views.IAttackTaxes? taxes)
    {
        var casting = new Casting.CastingSystem(new ZoneSystem(), new Query.Query(table.Layers), table.Layers, new Stubs.Interpreter());
        return new LegalActionsSubsystem(casting, table.Layers, table.Subject, taxes).For(state, table.A);
    }
}
