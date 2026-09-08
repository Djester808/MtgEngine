using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The cost some creatures owe to attack (CR 508.1g–508.1j) and the monarch moving on combat
/// damage (CR 725.2). Nothing here names a card: the tax is a continuous effect on the attacker,
/// which is how a "creatures can't attack you unless…" permanent reaches the creatures it taxes.
/// </summary>
public sealed class CombatWave26Tests
{
    /// <summary>{2} per attacking creature, the shape CR 508.1h has to total against the declaration.</summary>
    private static StaticAbility Tax(PlayerRef? who = null) =>
        new(new CantAttack(who, UnlessPays: new PayManaCost(new Times(new AttackersAgainst(new You()), 2))));

    /// <summary>Mana in a pool, CR 106.4 — where it came from is not combat's business.</summary>
    private static GameEvent Mana(PlayerId player, int amount) =>
        new ManaChanged(player, new ManaPool(0, 0, 0, 0, 0, amount), "test");

    [Fact]
    public void An_attack_tax_is_paid_out_of_the_pool_before_the_attackers_are_declared()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 2, 2, statics: [Tax()]);
        t.Enact(Mana(t.A, 2));
        t.Query.Amounts = Counting;

        var events = t.Attack(t.A, attacker, t.B);

        // CR 508.1j — the total was paid, and CR 508.1k then declared the attacker.
        Assert.Equal(0, t.State.Player(t.A).Mana.Colorless);
        Assert.True(t.State.Combat!.AttackersDeclared);
        Assert.True(events.FindIndex(e => e is ManaChanged) < events.FindIndex(e => e is AttackersDeclared));
    }

    [Fact]
    public void An_attack_tax_that_cannot_be_paid_refuses_the_whole_declaration()
    {
        var t = new CombatTable();
        var taxed = t.Creature(t.A, 2, 2, statics: [Tax()]);
        var free = t.Creature(t.A, 2, 2);
        t.Enact(Mana(t.A, 1));
        t.Query.Amounts = Counting;

        // CR 508.1j — partial payments are not allowed, so CR 508.1's preamble makes the whole
        // declaration illegal and CR 733 returns the game to the moment before it.
        Assert.ThrowsAny<RefusedException>(() => t.Attack(
            t.A,
            new AttackDeclaration(taxed, new CombatTarget(t.B, null)),
            new AttackDeclaration(free, new CombatTarget(t.B, null))));

        Assert.Null(t.State.Combat);
        Assert.Equal(1, t.State.Player(t.A).Mana.Colorless);
    }

    [Fact]
    public void The_tax_is_counted_against_the_declaration_being_made_and_charged_once_for_all_of_it()
    {
        var t = new CombatTable();
        var one = t.Creature(t.A, 2, 2, statics: [Tax()]);
        var two = t.Creature(t.A, 2, 2, statics: [Tax()]);
        t.Enact(Mana(t.A, 4));
        t.Query.Amounts = Counting;

        t.Attack(
            t.A,
            new AttackDeclaration(one, new CombatTarget(t.B, null)),
            new AttackDeclaration(two, new CombatTarget(t.B, null)));

        // CR 508.1h — the scaling is inside the cost, so two attackers cost {4} and not {8}: the
        // amount saw both of them, and one effect was totalled once however many creatures carry it.
        Assert.Equal(0, t.State.Player(t.A).Mana.Colorless);
        Assert.Equal(2, t.State.Combat!.Attackers.Count);
    }

    [Fact]
    public void A_tax_naming_a_player_is_owed_only_when_that_player_is_attacked()
    {
        // The player the effect taxes for is the one being attacked: the cost is owed, and with an
        // empty pool CR 508.1j refuses the declaration.
        var taxing = new CombatTable();
        var taxed = taxing.Creature(taxing.A, 2, 2, statics: [Tax(new You())]);
        taxing.Query.RefersTo = [taxing.B];
        taxing.Query.Amounts = Counting;

        Assert.ThrowsAny<RefusedException>(() => taxing.Attack(taxing.A, taxed, taxing.B));
        Assert.Null(taxing.State.Combat);

        // The same effect taxing for somebody else: CR 508.1g owes nothing, so nothing is paid.
        var free = new CombatTable();
        var attacker = free.Creature(free.A, 2, 2, statics: [Tax(new You())]);
        free.Query.RefersTo = [free.A];
        free.Query.Amounts = Counting;

        free.Attack(free.A, attacker, free.B);

        Assert.True(free.State.Combat!.AttackersDeclared);
    }

    [Fact]
    public void A_taxed_creature_is_still_able_to_attack()
    {
        var t = new CombatTable();
        var taxed = t.Creature(t.A, 2, 2, statics: [Tax()]);
        var forbidden = t.Creature(t.A, 2, 2, statics: [new StaticAbility(new CantAttack())]);

        // CR 508.1d — the player is not required to pay the cost, so the tax is not an inability;
        // an unconditional "can't attack" is.
        Assert.True(t.Subject.CouldAttack(t.State, taxed));
        Assert.False(t.Subject.CouldAttack(t.State, forbidden));
    }

    [Fact]
    public void Combat_damage_to_the_monarch_crowns_the_creatures_controller()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 3, 3);
        t.Enact(new MonarchChanged(t.B));

        t.Attack(t.A, attacker, t.B);
        var events = t.Strike(firstStrike: false);

        // CR 725.2 — the crown moves on the damage, CR 725.3 — there is only one monarch.
        Assert.Equal(t.A, t.State.Monarch);
        Assert.Contains(events, e => e is MonarchChanged crowned && crowned.Player == t.A);
    }

    [Fact]
    public void A_blocked_attacker_that_never_reaches_the_monarch_does_not_move_the_crown()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 3, 3);
        var blocker = t.Creature(t.B, 1, 4);
        t.Enact(new MonarchChanged(t.B));

        t.Attack(t.A, attacker, t.B);
        t.Block(t.B, new BlockDeclaration(blocker, attacker));
        t.Strike(firstStrike: false);

        // CR 725.2 asks for combat damage dealt *to the monarch*; a blocker took all of it.
        Assert.Equal(t.B, t.State.Monarch);
    }

    [Fact]
    public void The_monarchs_own_creature_dealing_damage_elsewhere_leaves_the_crown_alone()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 3, 3);
        t.Enact(new MonarchChanged(t.A));

        t.Attack(t.A, attacker, t.B);
        var events = t.Strike(firstStrike: false);

        // CR 725.3 — re-designating the monarch changes nothing, and the damaged player is not one.
        Assert.Equal(t.A, t.State.Monarch);
        Assert.DoesNotContain(events, e => e is MonarchChanged);
    }

    [Fact]
    public void The_crown_moves_with_no_monarch_designated_only_once_there_is_one()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 3, 3);

        t.Attack(t.A, attacker, t.B);
        var events = t.Strike(firstStrike: false);

        // CR 725.1 — there is no monarch until an effect designates one, so nothing to move.
        Assert.Null(t.State.Monarch);
        Assert.DoesNotContain(events, e => e is MonarchChanged);
    }

    /// <summary>The amount the tax scales by, counted off the board it is evaluated against, CR 508.1h.</summary>
    private static int Counting(GameState state, Amount amount) => amount switch
    {
        Times times => times.Factor * Counting(state, times.Value),
        AttackersAgainst => state.Combat?.Attackers.Count ?? 0,
        _ => throw new NotSupportedException($"the test's amount evaluator does not know {amount.GetType().Name}"),
    };
}
