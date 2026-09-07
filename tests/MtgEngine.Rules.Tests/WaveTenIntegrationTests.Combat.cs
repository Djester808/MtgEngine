using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.State;
using CastSpellCommand = MtgEngine.Rules.Commands.CastSpell;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The wave-10 joins that only a real turn reaches: the player prohibitions read by the state-based
/// actions (CR 613.10 with 704.5a), protection from everything on a player against a real combat
/// (CR 702.16), and a combat damage batch halted twice by CR 616.1 that finishes and opens its step
/// (CR 510.3). E5e and E0e taught the limits to the rules that read them and E4e opened the step;
/// each was verified against its own fakes, and what is asserted here is the whole turn.
/// </summary>
public sealed partial class WaveTenIntegrationTests
{
    /// <summary>
    /// CR 704.5a with 101.2 and 613.10: a player an effect says can't lose the game is at 0 life
    /// and stays in the game while the effect stands. 704.5a reads the state rather than a moment
    /// that has passed, so the loss lands on the first check after the effect ends — here the
    /// cleanup step of the turn the effect was made in (CR 514.2).
    /// </summary>
    [Fact]
    [RuleRef("704.5a")]
    public void A_player_who_cant_lose_survives_zero_life_until_the_effect_ends()
    {
        var table = new WaveTenIntegrationTable(WardDeck(), LandDeck(), startingLife: 2);

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 2);

        // CR 613.10: the prohibition is a continuous effect on a player, and it lasts this turn.
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveTenIntegrationTable.WardName)));
        table.PassUntilStackIsEmpty();
        Assert.Contains(table.State.ActiveEffects, e => e.Effect is PlayerCant && e.AppliesToPlayers.Contains(P0));

        // CR 119.3: two damage to a player at 2 life, so the life total is 0 and 704.5a applies.
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveTenIntegrationTable.BoltName), TargetPlayers: [P0]));
        table.PassUntilStackIsEmpty();

        Assert.Equal(0, table.State.Player(P0).Life);
        Assert.False(table.State.Player(P0).HasLost);
        Assert.Null(table.State.Outcome);

        // CR 514.2 with 704.5a: the effect ends in this turn's cleanup, and the next check performs
        // the loss with nothing needed to re-propose it.
        table.PassTo(4, Step.PrecombatMain);

        Assert.True(table.State.Player(P0).HasLost);
        Assert.NotNull(table.State.Outcome);
        Assert.Equal([P1], table.State.Outcome!.Winners);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// CR 702.16 with 702.16j: a player with protection from everything is not dealt damage by any
    /// source, and an unblocked attacker's combat damage is damage from a source (CR 510.1c). E5e's
    /// replacement arm reads the player's limits; this is that arm reached through a real combat
    /// rather than through a fake damage event.
    /// </summary>
    [Fact]
    [RuleRef("702.16")]
    public void A_player_with_protection_from_everything_takes_no_combat_damage()
    {
        var table = new WaveTenIntegrationTable(OxDeck(), AegisDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(2, Step.PrecombatMain);
        table.PlayALand(P1);

        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 2);
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveTenIntegrationTable.OxName)));
        table.PassUntilStackIsEmpty();

        table.PassTo(4, Step.PrecombatMain);
        table.PlayALand(P1);
        table.TapForMana(P1, 1);
        table.Do(new CastSpellCommand(P1, table.InHand(P1, WaveTenIntegrationTable.AegisName)));
        table.PassUntilStackIsEmpty();
        Assert.Contains(table.State.ActiveEffects, e => e.Effect is PlayerProtectionFromEverything && e.AppliesToPlayers.Contains(P1));

        // CR 508.1: one 2/2 attacking, nothing blocking, so the whole hit is aimed at the player.
        table.PassTo(5, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(5, Step.DeclareAttackers);
        var attacker = Assert.Single(table.Battlefield(P0, WaveTenIntegrationTable.OxName));
        table.Do(new DeclareAttackers(P0, [new AttackDeclaration(attacker, new CombatTarget(P1, null))]));
        table.PassTo(5, Step.DeclareBlockers);
        table.Do(new DeclareBlockers(P1, []));

        var life = table.State.Player(P1).Life;
        table.PassTo(5, Step.EndOfCombat);

        // CR 702.16: none of it is dealt, and the attacker is still an attacking creature — the
        // protection is not a rule about the combat, it is a rule about the damage.
        Assert.Equal(life, table.State.Player(P1).Life);
        Assert.Equal(0, table.State.Objects[attacker].Damage);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// The control the test above needs to mean anything: the same table, the same turns and the
    /// same unblocked 2/2, against a defender who cast nothing. CR 510.1c deals the two, so a
    /// prevented hit is proved to be prevention and not a combat that never happened.
    /// </summary>
    [Fact]
    [RuleRef("510.1c")]
    public void The_same_unblocked_attacker_deals_its_damage_to_a_player_with_no_protection()
    {
        var table = new WaveTenIntegrationTable(OxDeck(), LandDeck());

        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(2, Step.PrecombatMain);
        table.PlayALand(P1);

        table.PassTo(3, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 2);
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveTenIntegrationTable.OxName)));
        table.PassUntilStackIsEmpty();

        table.PassTo(5, Step.DeclareAttackers);
        var attacker = Assert.Single(table.Battlefield(P0, WaveTenIntegrationTable.OxName));
        table.Do(new DeclareAttackers(P0, [new AttackDeclaration(attacker, new CombatTarget(P1, null))]));
        table.PassTo(5, Step.DeclareBlockers);
        table.Do(new DeclareBlockers(P1, []));

        var life = table.State.Player(P1).Life;
        table.PassTo(5, Step.EndOfCombat);

        Assert.Equal(2, life - table.State.Player(P1).Life);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// CR 616.1 with 510.3 and 117.3b: three one-damage prevention shields apply to the first hit of
    /// a two-attacker batch, so the batch stops twice to be ordered. Each answer resumes it, the
    /// last one finishes it, and the active player receives priority on an open combat damage step —
    /// which is E4e's half, and the half EINT3 found missing after wave 8.
    /// </summary>
    [Fact]
    [RuleRef("510.3")]
    public void A_batch_halted_twice_deals_everything_after_the_last_answer_and_opens_the_step()
    {
        var table = ShieldedAttack(shields: 3);
        var life = table.State.Player(P1).Life;

        var first = PassUntilTheBatchAsks(table);
        Assert.Equal(ChoiceKinds.ReplacementOrder, first.Kind);
        Assert.Equal(3, first.Options.Count);
        Assert.Null(table.State.Priority.Holder);

        table.Do(new AnswerChoice(P1, first.Id, [first.Options[0].Id]));

        // The same hit, still being decided: two shields are left applicable to the one damage that
        // is still on it, so nobody receives priority yet and no life has been lost.
        var second = table.State.Pending;
        Assert.NotNull(second);
        Assert.Equal(ChoiceKinds.ReplacementOrder, second.Kind);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(table.State.Priority.Holder);
        Assert.Equal(life, table.State.Player(P1).Life);

        table.Do(new AnswerChoice(P1, second.Id, [second.Options[0].Id]));

        // CR 615.1 with 510.2: two shields spent on the first hit, the third on one point of the
        // second, so one point lands — and CR 510.3 opens the step, which is what this game is for.
        Assert.Null(table.State.Pending);
        Assert.Equal(Step.CombatDamage, table.State.Turn.Step);
        Assert.Equal(P0, table.State.Priority.Holder);
        Assert.Equal(1, life - table.State.Player(P1).Life);

        // CR 117.4: a round of passes on the open step ends it, which is the walk that used to stop
        // dead with nobody holding priority.
        table.Do(new PassPriority(P0));
        table.Do(new PassPriority(P1));
        Assert.Equal(Step.EndOfCombat, table.State.Turn.Step);

        AssertTheLogIsTheGame(table);
    }

    /// <summary>
    /// Two 2/2 attackers cast a turn apart so neither is summoning sick (CR 302.6), and this many
    /// one-damage prevention shields on the defending player, so the first hit of the batch has two
    /// or more effects wanting to apply to it (CR 616.1). Left in the declare blockers step of turn
    /// 7 with nothing blocking.
    /// </summary>
    [RuleRef("615.1")]
    private static WaveTenIntegrationTable ShieldedAttack(int shields)
    {
        var table = new WaveTenIntegrationTable(OxDeck(), ShieldDeck());
        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(P0);
        table.PassTo(2, Step.PrecombatMain);
        table.PlayALand(P1);

        AnOx(table, 3);
        Shields(table, 4, 1);
        AnOx(table, 5);
        Shields(table, 6, shields - 1);

        // CR 615.1: one shield per effect, every one of them applying to the same player.
        Assert.Equal(shields, table.State.ActiveEffects.Count(e => e.Effect is PreventDamage && e.AppliesToPlayers.Contains(P1)));

        table.PassTo(7, Step.DeclareAttackers);
        var attackers = table.Battlefield(P0, WaveTenIntegrationTable.OxName);
        Assert.Equal(2, attackers.Count);
        table.Do(new DeclareAttackers(P0, [.. attackers.Select(a => new AttackDeclaration(a, new CombatTarget(P1, null)))]));
        table.PassTo(7, Step.DeclareBlockers);
        table.Do(new DeclareBlockers(P1, []));
        return table;
    }

    /// <summary>A land and a vanilla 2/2, cast with the mana that land and its fellows make, CR 608.3.</summary>
    [RuleRef("608.3")]
    private static void AnOx(WaveTenIntegrationTable table, int turn)
    {
        table.PassTo(turn, Step.PrecombatMain);
        table.PlayALand(P0);
        table.TapForMana(P0, 2);
        table.Do(new CastSpellCommand(P0, table.InHand(P0, WaveTenIntegrationTable.OxName)));
        table.PassRound();
    }

    /// <summary>A land, and this many prevention shields cast and resolved in that one turn, CR 615.1.</summary>
    [RuleRef("615.1")]
    private static void Shields(WaveTenIntegrationTable table, int turn, int count)
    {
        table.PassTo(turn, Step.PrecombatMain);
        table.PlayALand(P1);
        if (count <= 0)
            return;

        table.TapForMana(P1, count);
        for (var i = 0; i < count; i++)
        {
            table.Do(new CastSpellCommand(P1, table.InHand(P1, WaveTenIntegrationTable.ShieldName)));
            table.PassRound();
        }
    }

    /// <summary>
    /// Pass priority until the batch stops the game to ask, CR 101.4. The question is the
    /// destination, because what happens after its answer is what is under test.
    /// </summary>
    [RuleRef("101.4")]
    private static PendingChoice PassUntilTheBatchAsks(WaveTenIntegrationTable table)
    {
        for (var guard = 0; guard < 200; guard++)
        {
            if (table.State.Pending is { } pending)
                return pending;
            if (table.State.Priority.Holder is not { } holder)
                break;
            table.Do(new PassPriority(holder));
        }

        throw new InvalidOperationException($"Nothing was asked; the game reached turn {table.State.Turn.Number} {table.State.Turn.Step}.");
    }

    /// <summary>Lands, the prohibition that keeps a player in the game, and the damage that tests it.</summary>
    private static ImmutableList<string> WardDeck() =>
    [
        .. Enumerable.Repeat(WaveTenIntegrationTable.GroveId, 12),
        .. Enumerable.Repeat(WaveTenIntegrationTable.WardId, 6),
        .. Enumerable.Repeat(WaveTenIntegrationTable.BoltId, 6),
    ];

    /// <summary>Lands and the vanilla creatures that attack with them.</summary>
    private static ImmutableList<string> OxDeck() =>
    [
        .. Enumerable.Repeat(WaveTenIntegrationTable.GroveId, 14),
        .. Enumerable.Repeat(WaveTenIntegrationTable.OxId, 10),
    ];

    /// <summary>Lands and the protection the defender gives themselves.</summary>
    private static ImmutableList<string> AegisDeck() =>
    [
        .. Enumerable.Repeat(WaveTenIntegrationTable.GroveId, 14),
        .. Enumerable.Repeat(WaveTenIntegrationTable.AegisId, 10),
    ];

    /// <summary>Lands and the shields the defender casts.</summary>
    private static ImmutableList<string> ShieldDeck() =>
    [
        .. Enumerable.Repeat(WaveTenIntegrationTable.GroveId, 14),
        .. Enumerable.Repeat(WaveTenIntegrationTable.ShieldId, 10),
    ];
}
