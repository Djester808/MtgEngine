using MtgEngine.Cards;
using MtgEngine.Rules.Combat;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// An attack cost that is not mana, CR 508.1h with 508.1j. The total is determined and locked in
/// at CR 508.1h and paid in full at CR 508.1j; nothing in either rule says it is mana, and a cost
/// that moves objects or takes life is paid through the casting system's CR 118.6 entry point
/// (E3p § 2, E3q § 3) because that is the system built with the zone system a cost context needs.
/// Nothing here names a card: the tax is a continuous effect on the attacker.
/// </summary>
public sealed class CombatWave29Tests
{
    /// <summary>A cost combat cannot pay itself: life is not mana and never reaches <c>PaySymbols</c>.</summary>
    private static StaticAbility LifeTax(int life) =>
        new(new CantAttack(null, UnlessPays: new PayLife(new Fixed(life))));

    /// <summary>The mana half, so a declaration can owe both kinds at once (CR 508.1h totals them together).</summary>
    private static StaticAbility ManaTax() =>
        new(new CantAttack(null, UnlessPays: new PayManaCost(new Fixed(2))));

    /// <summary>Mana in a pool, CR 106.4 — where it came from is not combat's business.</summary>
    private static GameEvent Mana(PlayerId player, int amount) =>
        new ManaChanged(player, new ManaPool(0, 0, 0, 0, 0, amount), "test");

    [Fact]
    public void An_attack_cost_that_is_not_mana_is_paid_through_the_casting_system()
    {
        var t = new CombatTable();
        var casting = new TaxCasting();
        t.Subject.Casting = casting;
        var attacker = t.Creature(t.A, 2, 2, statics: [LifeTax(3)]);

        var events = t.Attack(t.A, attacker, t.B);

        // CR 508.1j — paid in full, through the one entry point CR 118.6 gives the rules.
        Assert.Equal(17, t.Life(t.A));
        Assert.Single(casting.Paid);
        Assert.True(t.State.Combat!.AttackersDeclared);

        // CR 508.1j then 508.1k: the cost is paid before the creatures become attacking.
        Assert.True(events.FindIndex(e => e is LifeChanged) < events.FindIndex(e => e is AttackersDeclared));
    }

    [Fact]
    public void A_non_mana_attack_cost_that_cannot_be_paid_refuses_the_whole_declaration()
    {
        var t = new CombatTable();
        var casting = new TaxCasting();
        t.Subject.Casting = casting;
        var taxed = t.Creature(t.A, 2, 2, statics: [LifeTax(21)]);
        var free = t.Creature(t.A, 2, 2);

        // CR 508.1j allows no partial payment, so CR 508.1's preamble makes the whole declaration
        // illegal and CR 733 returns the game to the moment before it — the untaxed attacker is
        // not left attacking on its own.
        var refused = Assert.ThrowsAny<RefusedException>(() => t.Attack(
            t.A,
            new AttackDeclaration(taxed, new CombatTarget(t.B, null)),
            new AttackDeclaration(free, new CombatTarget(t.B, null))));

        Assert.Equal("508.1h", refused.Rule);
        Assert.Null(t.State.Combat);
        Assert.Equal(20, t.Life(t.A));
        Assert.Empty(casting.Paid);
    }

    [Fact]
    public void An_unpayable_cost_is_found_at_the_total_before_any_of_the_total_is_paid()
    {
        var t = new CombatTable();
        var casting = new TaxCasting();
        t.Subject.Casting = casting;
        t.Query.Amounts = (_, _) => 2;
        var attacker = t.Creature(t.A, 2, 2, statics: [ManaTax(), LifeTax(21)]);
        t.Enact(Mana(t.A, 2));

        Assert.ThrowsAny<RefusedException>(() => t.Attack(t.A, attacker, t.B));

        // CR 508.1h determines the whole total, and this half of it is unpayable, so CR 508.1j
        // never runs: the refusal is a question asked (CanPayForRule) and not a payment taken.
        Assert.Empty(casting.Paid);
        Assert.NotEmpty(casting.Asked);
        Assert.Equal(2, t.State.Player(t.A).Mana.Colorless);
    }

    [Fact]
    public void Whether_a_non_mana_cost_can_be_paid_is_asked_against_the_declaration_being_made()
    {
        var t = new CombatTable();
        var casting = new TaxCasting();
        t.Subject.Casting = casting;
        var attacker = t.Creature(t.A, 2, 2, statics: [LifeTax(3)]);

        t.Attack(t.A, attacker, t.B);

        // CR 508.1h — "the active player determines the total cost to attack": the board the
        // declaration would produce, which is what an amount inside the cost has to count.
        var asked = Assert.Single(casting.Asked);
        Assert.NotNull(asked.Combat);
        Assert.True(asked.Combat.AttackersDeclared);
    }

    [Fact]
    public void With_no_casting_system_a_non_mana_attack_cost_is_still_refused()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 2, 2, statics: [LifeTax(1)]);

        // A null handle keeps the older refusal (CR 508.1h): a cost combat cannot pay is never
        // waved through, so the wiring and the call could land in either order.
        var refused = Assert.ThrowsAny<RefusedException>(() => t.Attack(t.A, attacker, t.B));

        Assert.Equal("508.1h", refused.Rule);
        Assert.Null(t.State.Combat);
        Assert.Equal(20, t.Life(t.A));
    }

    [Fact]
    public void The_composed_game_hands_combat_the_casting_system_it_pays_through()
    {
        foreach (var subsystems in new[] { LegacySubsystems.Create(), Subsystems.Wave2() })
        {
            var combat = Assert.IsType<CombatSystem>(subsystems.Combat);
            Assert.Same(subsystems.Casting, combat.Casting);
        }
    }

    /// <summary>
    /// The casting system as combat sees it, CR 118.6: a cost the rules impose, answered for and
    /// paid. Only <see cref="PayLife"/> is understood, which is the point — combat hands the cost
    /// over without reading it, and a fake that understood everything would prove nothing.
    /// CR 119.4: a player may pay life only down to nothing.
    /// </summary>
    private sealed class TaxCasting : ICastingSystem
    {
        public string Name => "combat wave-29 casting";

        /// <summary>Every state the payability question was asked against, so a test can prove which board CR 508.1h read.</summary>
        public List<GameState> Asked { get; } = [];

        /// <summary>Every cost actually paid, so a test can prove nothing was taken before the refusal.</summary>
        public List<Cost> Paid { get; } = [];

        public Outcome Cast(GameState state, CastSpell command) => throw new NotSupportedException();

        public Outcome Activate(GameState state, ActivateAbility command) => throw new NotSupportedException();

        public Outcome PlayLand(GameState state, PlayLand command) => throw new NotSupportedException();

        public Outcome ResolveTop(GameState state) => throw new NotSupportedException();

        public Refusal? Check(GameState state, Command command) => throw new NotSupportedException();

        public Refusal? CanPayForRule(GameState state, PlayerId player, Cost cost, ObjectId? source = null, int? x = null)
        {
            ArgumentNullException.ThrowIfNull(state);
            Asked.Add(state);
            return state.Player(player).Life >= Owed(cost) ? null : new Refusal("A player has that much life to pay.", "119.4");
        }

        public Outcome PayForRule(GameState state, PlayerId player, Cost cost, ObjectId? source = null, int? x = null)
        {
            Paid.Add(cost);
            var lost = new LifeChanged(player, -Owed(cost), "attack");
            return new Outcome(lost.Apply(state), [lost]);
        }

        private static int Owed(Cost cost) =>
            cost is PayLife { Amount: Fixed life } ? life.N : throw new NotSupportedException("only a life cost is faked here");
    }
}
