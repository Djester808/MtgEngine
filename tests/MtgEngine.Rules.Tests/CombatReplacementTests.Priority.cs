using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.State;
using CastSpellCommand = MtgEngine.Rules.Commands.CastSpell;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// What the step does once a halted batch has finished, CR 510.3 with 117.3b. The damage was
/// already right after wave 8's resumption; what was missing was the rest of the turn-based
/// action — an unhalted batch is dealt inside the turn system's advance, which grants priority
/// when its action is done, and a batch that came back through an answer had nobody open the
/// step, so the game could not be walked on. These games are played through
/// <see cref="Rules.Engine.Game.Apply"/> on <see cref="Rules.Engine.Subsystems.Wave2"/>, with
/// real prevention effects raising the real CR 616.1 question.
/// </summary>
public sealed partial class CombatReplacementTests
{
    private static PlayerId Attacking => WaveEightIntegrationTable.P0;

    private static PlayerId Defending => WaveEightIntegrationTable.P1;

    /// <summary>
    /// CR 510.3: combat damage has been dealt, so the active player receives priority — the same
    /// end an unhalted batch reaches (CR 117.3b), and the game walks on from there.
    /// </summary>
    [Fact]
    [RuleRef("510.3")]
    public void A_resumed_combat_damage_batch_gives_the_active_player_priority()
    {
        var table = ShieldedAttack(shields: 2);
        var life = table.State.Player(Defending).Life;

        var question = TheQuestion(table);
        Assert.Equal(ChoiceKinds.ReplacementOrder, question.Kind);
        Assert.Null(table.State.Priority.Holder);

        table.Do(new AnswerChoice(Defending, question.Id, [question.Options[0].Id]));

        // Both shields spend against the first attacker's hit; the second attacker meets none,
        // CR 615.1 with 510.2 — and now the step is open again, which is what this test is for.
        Assert.Null(table.State.Pending);
        Assert.Equal(Step.CombatDamage, table.State.Turn.Step);
        Assert.Equal(Attacking, table.State.Priority.Holder);
        Assert.Equal(2, life - table.State.Player(Defending).Life);

        // CR 117.4: a round of passes on the open step ends it, which is the walk that used to
        // stop dead with nobody holding priority.
        table.Do(new PassPriority(Attacking));
        table.Do(new PassPriority(Defending));
        Assert.Equal(Step.EndOfCombat, table.State.Turn.Step);
    }

    /// <summary>
    /// CR 616.1 with 101.4: answering leaves a third applicable effect on the same hit, so the
    /// game asks again and the batch is still owed. Priority is granted only when the last of
    /// those answers finishes the batch — and the second question resumes the batch at all only
    /// because the halt's marks travel onto it.
    /// </summary>
    [Fact]
    [RuleRef("616.1")]
    public void A_batch_halted_twice_holds_priority_only_after_the_second_answer()
    {
        var table = ShieldedAttack(shields: 3);
        var life = table.State.Player(Defending).Life;

        var first = TheQuestion(table);
        Assert.Equal(3, first.Options.Count);
        table.Do(new AnswerChoice(Defending, first.Id, [first.Options[0].Id]));

        // The same hit, still being decided: two shields are left applicable to the one damage
        // that is still on it, so nobody receives priority yet.
        var second = table.State.Pending;
        Assert.NotNull(second);
        Assert.Equal(ChoiceKinds.ReplacementOrder, second.Kind);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(table.State.Priority.Holder);
        Assert.Equal(life, table.State.Player(Defending).Life);

        table.Do(new AnswerChoice(Defending, second.Id, [second.Options[0].Id]));

        // Two shields spent on the first hit, the third on the second hit's first point of
        // damage, so one point lands — and the step opens, CR 510.3.
        Assert.Null(table.State.Pending);
        Assert.Equal(Step.CombatDamage, table.State.Turn.Step);
        Assert.Equal(Attacking, table.State.Priority.Holder);
        Assert.Equal(1, life - table.State.Player(Defending).Life);

        table.Do(new PassPriority(Attacking));
        table.Do(new PassPriority(Defending));
        Assert.Equal(Step.EndOfCombat, table.State.Turn.Step);
    }

    /// <summary>
    /// The EINT3 game: two 2/2 attackers cast a turn apart so neither is summoning sick
    /// (CR 302.6), and this many one-damage prevention shields on the defending player, so that
    /// the first hit of the batch has two or more effects wanting to apply to it (CR 616.1).
    /// The game is left in the declare blockers step of turn 7 with nothing blocking.
    /// </summary>
    [RuleRef("615.1")]
    private static WaveEightIntegrationTable ShieldedAttack(int shields)
    {
        var table = new WaveEightIntegrationTable(OxDeck(), ShieldDeck());
        table.PassTo(1, Step.PrecombatMain);
        table.PlayALand(Attacking);
        table.PassTo(2, Step.PrecombatMain);
        table.PlayALand(Defending);

        AnOx(table, 3);
        Shields(table, 4, 1);
        AnOx(table, 5);
        Shields(table, 6, shields - 1);

        // CR 615.1: one shield per effect, every one of them applying to the same player.
        Assert.Equal(shields, table.State.ActiveEffects.Count(e => e.Effect is PreventDamage && e.AppliesToPlayers.Contains(Defending)));

        table.PassTo(7, Step.DeclareAttackers);
        var attackers = table.Battlefield(Attacking, WaveEightIntegrationTable.OxName);
        Assert.Equal(2, attackers.Count);
        table.Do(new DeclareAttackers(Attacking, [.. attackers.Select(a => new AttackDeclaration(a, new CombatTarget(Defending, null)))]));
        table.PassTo(7, Step.DeclareBlockers);
        table.Do(new DeclareBlockers(Defending, []));
        return table;
    }

    /// <summary>A land and a vanilla 2/2, cast with the mana that land and its fellows make, CR 608.3.</summary>
    [RuleRef("608.3")]
    private static void AnOx(WaveEightIntegrationTable table, int turn)
    {
        table.PassTo(turn, Step.PrecombatMain);
        table.PlayALand(Attacking);
        table.TapForMana(Attacking, 2);
        table.Do(new CastSpellCommand(Attacking, table.InHand(Attacking, WaveEightIntegrationTable.OxName)));
        table.PassRound();
    }

    /// <summary>A land, and this many prevention shields cast and resolved in that one turn, CR 615.1.</summary>
    [RuleRef("615.1")]
    private static void Shields(WaveEightIntegrationTable table, int turn, int count)
    {
        table.PassTo(turn, Step.PrecombatMain);
        table.PlayALand(Defending);
        if (count <= 0)
            return;

        table.TapForMana(Defending, count);
        for (var i = 0; i < count; i++)
        {
            table.Do(new CastSpellCommand(Defending, table.InHand(Defending, WaveEightIntegrationTable.ShieldName)));
            table.PassRound();
        }
    }

    /// <summary>
    /// Pass priority until the batch stops the game to ask, CR 101.4. The question is the
    /// destination, because what happens after its answer is what is under test.
    /// </summary>
    [RuleRef("101.4")]
    private static PendingChoice TheQuestion(WaveEightIntegrationTable table)
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

    /// <summary>Lands and the vanilla creatures that attack with them.</summary>
    private static ImmutableList<string> OxDeck() =>
    [
        .. Enumerable.Repeat(WaveEightIntegrationTable.GroveId, 14),
        .. Enumerable.Repeat(WaveEightIntegrationTable.OxId, 10),
    ];

    /// <summary>Lands and the shields the defender casts.</summary>
    private static ImmutableList<string> ShieldDeck() =>
    [
        .. Enumerable.Repeat(WaveEightIntegrationTable.GroveId, 14),
        .. Enumerable.Repeat(WaveEightIntegrationTable.ShieldId, 10),
    ];
}
