using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The price on one taxed attacker, CR 508.1d with 508.1h — what the legal-actions view asks so
/// that a player offered a taxed creature is also told what declaring it costs. Every answer here
/// comes from the same reading the declaration is charged by, and nothing here is a card: the tax
/// is a continuous effect on the attacker.
/// </summary>
public sealed class CombatTestsWave30
{
    /// <summary>A tax printed as mana, so no amount has to be evaluated to total it.</summary>
    private static StaticAbility ManaTax(string printed) =>
        new(new CantAttack(null, UnlessPays: new ManaCost(printed)));

    /// <summary>A tax whose amount the game counts, CR 118.8 — the half that reads a board.</summary>
    private static StaticAbility CountedTax() =>
        new(new CantAttack(null, UnlessPays: new PayManaCost(new Fixed(0))));

    /// <summary>A cost combat cannot pay itself: life is not mana and never reaches the pool.</summary>
    private static StaticAbility LifeTax(int life) =>
        new(new CantAttack(null, UnlessPays: new PayLife(new Fixed(life))));

    private static GameEvent Mana(PlayerId player, int amount) =>
        new ManaChanged(player, new ManaPool(0, 0, 0, 0, 0, amount), "test");

    /// <summary>
    /// CR 508.1d prices what is owed and nothing else. An ordinary creature owes nothing, so it
    /// has no offer at all and a client draws no price tag on an ordinary attack.
    /// </summary>
    [Fact]
    [RuleRef("508.1d")]
    public void An_attacker_that_owes_no_tax_is_not_priced()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 2, 2);

        Assert.Null(t.Subject.TaxToAttack(t.State, t.A, attacker));
    }

    /// <summary>
    /// CR 508.1h totals the cost and CR 508.1j spends it out of the pool. The pool is the only
    /// thing that differs between the two readings below, so <c>Payable</c> is that reading and
    /// not a constant.
    /// </summary>
    [Fact]
    [RuleRef("508.1j")]
    public void A_mana_tax_is_priced_as_printed_and_paid_out_of_the_pool()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 2, 2, statics: [ManaTax("{2}")]);

        var broke = t.Subject.TaxToAttack(t.State, t.A, attacker);
        Assert.NotNull(broke);
        Assert.Equal("{2}", broke.Cost);
        Assert.Equal(attacker, broke.Attacker);
        Assert.False(broke.Payable);

        t.Enact(Mana(t.A, 2));
        Assert.True(t.Subject.TaxToAttack(t.State, t.A, attacker)!.Payable);
    }

    /// <summary>
    /// CR 508.1h: "the active player determines the total cost to attack" against the declaration
    /// being made, so an amount inside the cost counts that declaration. The evaluator here answers
    /// differently for the two boards, and only the locked-in one gives the price the declaration
    /// would actually be charged.
    /// </summary>
    [Fact]
    [RuleRef("508.1h")]
    public void A_counted_tax_is_totalled_against_the_declaration_being_made()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 2, 2, statics: [CountedTax()]);
        t.Query.Amounts = (state, _) => state.Combat is { AttackersDeclared: true } combat && combat.Attackers.ContainsKey(attacker) ? 2 : 9;

        var offer = t.Subject.TaxToAttack(t.State, t.A, attacker);

        Assert.NotNull(offer);
        Assert.Equal("{2}", offer.Cost);

        // The price is read, not taken: no declaration was made and no combat exists afterwards.
        Assert.Null(t.State.Combat);
    }

    /// <summary>
    /// CR 508.1d makes paying the player's choice, and a choice cannot be made from a list that
    /// does not say the price exists. A cost this engine cannot express as mana — which the
    /// declaration itself refuses with a <see cref="RefusedException"/> naming CR 508.1h — is an
    /// offer that is not payable, carrying the cost's own text.
    /// </summary>
    [Fact]
    [RuleRef("508.1d")]
    public void A_tax_that_is_not_mana_is_offered_unpayable_rather_than_refused()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 2, 2, statics: [LifeTax(3)]);

        var offer = t.Subject.TaxToAttack(t.State, t.A, attacker);

        Assert.NotNull(offer);
        Assert.False(offer.Payable);
        Assert.Contains("PayLife", offer.Cost, StringComparison.Ordinal);

        // The same board through the declaration refuses, which is the behaviour the view exists
        // to spare the player from having to discover (CR 508.1h with no casting system).
        Assert.ThrowsAny<RefusedException>(() => t.Attack(t.A, attacker, t.B));
    }

    /// <summary>
    /// CR 118.6 is the one entry point for a cost the rules impose, so payability for a cost that
    /// is not mana is that system's answer and never combat's guess. Asking is not paying: the
    /// question changes nothing, and CR 508.1j has not run.
    /// </summary>
    [Fact]
    [RuleRef("118.6")]
    public void A_tax_that_is_not_mana_is_payable_when_the_system_that_would_pay_it_says_so()
    {
        var t = new CombatTable();
        var casting = new CombatWave30Casting();
        t.Subject.Casting = casting;
        var affordable = t.Creature(t.A, 2, 2, statics: [LifeTax(3)]);
        var ruinous = t.Creature(t.A, 2, 2, statics: [LifeTax(21)]);

        Assert.True(t.Subject.TaxToAttack(t.State, t.A, affordable)!.Payable);
        Assert.False(t.Subject.TaxToAttack(t.State, t.A, ruinous)!.Payable);

        Assert.Empty(casting.Paid);
        Assert.Equal(20, t.Life(t.A));
    }

    /// <summary>
    /// CR 508.1c's tax is owed only by an attack on the player the effect names, and CR 506.2
    /// forces that player in a two-player game — the nonactive player is the defending player —
    /// so the price is read against them rather than guessed. A tax naming the attacking player
    /// is owed by nobody's attack here and is not priced.
    /// </summary>
    [Fact]
    [RuleRef("506.2")]
    public void A_tax_owed_by_an_attack_on_one_player_is_priced_against_the_forced_defender()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 2, 2, statics: [new StaticAbility(new CantAttack(new EachOpponent(), UnlessPays: new ManaCost("{1}")))]);

        t.Query.RefersTo = [t.B];
        Assert.NotNull(t.Subject.TaxToAttack(t.State, t.A, attacker));

        t.Query.RefersTo = [t.A];
        Assert.Null(t.Subject.TaxToAttack(t.State, t.A, attacker));
    }

    /// <summary>
    /// CR 508.1a: only a permanent on the battlefield can be declared, so nothing else has a price.
    /// The view asks about the list it holds and an object can leave between the two moments.
    /// </summary>
    [Fact]
    [RuleRef("508.1a")]
    public void A_creature_that_is_not_on_the_battlefield_is_not_priced()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 2, 2, statics: [ManaTax("{2}")]);

        t.Remove(attacker);

        Assert.Null(t.Subject.TaxToAttack(t.State, t.A, attacker));
    }

    /// <summary>
    /// The casting system as the price asks it, CR 118.6: only <see cref="PayLife"/> is understood,
    /// so combat is proved to hand the cost over without reading it. CR 119.4 — a player may pay
    /// life only down to nothing.
    /// </summary>
    private sealed class CombatWave30Casting : ICastingSystem
    {
        public string Name => "combat wave-30 casting";

        /// <summary>Every cost actually paid, so a test can prove a question took nothing.</summary>
        public List<Cost> Paid { get; } = [];

        public Outcome Cast(GameState state, CastSpell command) => throw new NotSupportedException();

        public Outcome Activate(GameState state, ActivateAbility command) => throw new NotSupportedException();

        public Outcome PlayLand(GameState state, PlayLand command) => throw new NotSupportedException();

        public Outcome ResolveTop(GameState state) => throw new NotSupportedException();

        public Refusal? Check(GameState state, Command command) => throw new NotSupportedException();

        public Refusal? CanPayForRule(GameState state, PlayerId player, Cost cost, ObjectId? source = null, int? x = null)
        {
            ArgumentNullException.ThrowIfNull(state);
            return state.Player(player).Life >= Owed(cost) ? null : new Refusal("A player has that much life to pay.", "119.4");
        }

        public Outcome PayForRule(GameState state, PlayerId player, Cost cost, ObjectId? source = null, int? x = null)
        {
            Paid.Add(cost);
            return Outcome.Unchanged(state);
        }

        private static int Owed(Cost cost) =>
            cost is PayLife { Amount: Fixed life } ? life.N : throw new NotSupportedException("only a life cost is faked here");
    }
}
