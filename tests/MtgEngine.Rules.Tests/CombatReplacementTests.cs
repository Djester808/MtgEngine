using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Combat damage is offered to the replacement system, CR 614.1a and 616.1. CR 510.2 deals the
/// batch simultaneously, so every assignment is still computed against the board the step began
/// on; what changes here is that each event of that batch is proposed rather than applied, and
/// what comes back is folded before the next one is proposed. Lifelink follows (CR 702.15b): the
/// life gained is the damage that was actually dealt, not the damage that was assigned.
/// </summary>
public sealed class CombatReplacementTests
{
    /// <summary>
    /// With no replacement system attached the batch is exactly what it always was: the same
    /// events, in the same order, with the same result. Wave 1 games have no replacement system
    /// and must not notice that combat gained a seam.
    /// </summary>
    [Fact]
    [RuleRef("510.2")]
    public void With_no_replacement_system_the_batch_is_what_it_was()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 2, 2, [KeywordKind.Lifelink]);
        var blocker = t.Creature(t.B, 2, 2);
        t.Attack(t.A, attacker, t.B);
        t.Block(t.B, new BlockDeclaration(blocker, attacker));

        Assert.Null(t.Subject.Replacements);
        var events = t.Strike(firstStrike: false);

        Assert.Collection(
            events,
            e => Dealt(e, attacker, blocker, 2),
            e => Assert.Equal(new LifeChanged(t.A, 2, "lifelink"), e),
            e => Dealt(e, blocker, attacker, 2));
        Assert.Equal(2, t.DamageOn(blocker));
        Assert.Equal(2, t.DamageOn(attacker));
        Assert.Equal(22, t.Life(t.A));
    }

    /// <summary>
    /// CR 616.1 — every event of the batch is offered, and the affected player is the one being
    /// dealt the damage or the controller of the permanent being dealt it. Lifelink's life gain is
    /// offered too: "you can't gain life" is a replacement like any other.
    /// </summary>
    [Fact]
    [RuleRef("616.1")]
    public void Every_event_of_the_batch_is_offered_to_the_affected_player()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 2, 2, [KeywordKind.Lifelink]);
        var blocker = t.Creature(t.B, 2, 2);
        var fake = new CombatReplacementFake();
        t.Subject.Replacements = fake;
        t.Attack(t.A, attacker, t.B);
        t.Block(t.B, new BlockDeclaration(blocker, attacker));

        t.Strike(firstStrike: false);

        Assert.Collection(
            fake.Offered,
            e => Assert.Equal(blocker, Assert.IsType<DamageDealt>(e).ToObject),
            e => Assert.Equal(t.A, Assert.IsType<LifeChanged>(e).Player),
            e => Assert.Equal(attacker, Assert.IsType<DamageDealt>(e).ToObject));
        Assert.Equal(new List<PlayerId> { t.B, t.A, t.A }, fake.Affected);
    }

    /// <summary>
    /// CR 615.1 with 702.15b — a hit that is prevented deals nothing, so it gains nothing either.
    /// The lifelink event is never even proposed, because there is no damage for it to follow.
    /// </summary>
    [Fact]
    [RuleRef("702.15b")]
    public void A_prevented_hit_deals_nothing_and_gains_no_lifelink()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 3, 3, [KeywordKind.Lifelink]);
        var fake = new CombatReplacementFake { Instead = CombatReplacementFake.Nothing };
        t.Subject.Replacements = fake;
        t.Attack(t.A, attacker, t.B);

        var events = t.Strike(firstStrike: false);

        Assert.Empty(events);
        Assert.Equal(20, t.Life(t.B));
        Assert.Equal(20, t.Life(t.A));
        Assert.Single(fake.Offered);
    }

    /// <summary>CR 614.1b with 702.15b — a doubled hit deals double, and lifelink follows it.</summary>
    [Fact]
    [RuleRef("614.1b")]
    public void A_doubled_hit_deals_double_and_gains_double()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 2, 2, [KeywordKind.Lifelink]);
        t.Subject.Replacements = new CombatReplacementFake { Instead = CombatReplacementFake.Doubled };
        t.Attack(t.A, attacker, t.B);

        var events = t.Strike(firstStrike: false);

        Assert.Equal(4, Assert.IsType<DamageDealt>(events[0]).Amount);
        Assert.Equal(new LifeChanged(t.A, 4, "lifelink"), events[1]);
        Assert.Equal(16, t.Life(t.B));
        Assert.Equal(24, t.Life(t.A));
    }

    /// <summary>
    /// CR 510.2 — every assignment is computed against the board the step began on, so what one
    /// event's replacement did cannot change what a later one assigned: the blocker still assigns
    /// its printed power back even though the attacker's doubled damage has already landed on it.
    /// </summary>
    [Fact]
    [RuleRef("510.2")]
    public void Assignments_are_computed_against_the_board_the_step_began_on()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 2, 2);
        var blocker = t.Creature(t.B, 2, 2);
        t.Subject.Replacements = new CombatReplacementFake { Instead = CombatReplacementFake.Doubled };
        t.Attack(t.A, attacker, t.B);
        t.Block(t.B, new BlockDeclaration(blocker, attacker));

        t.Strike(firstStrike: false);

        Assert.Equal(4, t.DamageOn(blocker));
        Assert.Equal(4, t.DamageOn(attacker));
    }

    /// <summary>
    /// CR 702.2b and 903.10a — the deathtouch flag and the commander-damage flag are the proposed
    /// event's, so they ride on whatever the replacement system hands back. A doubled commander hit
    /// is twice as much commander damage.
    /// </summary>
    [Fact]
    [RuleRef("903.10a")]
    public void Deathtouch_and_commander_damage_ride_on_the_surviving_event()
    {
        var t = new CombatTable();
        var general = t.Creature(t.A, 2, 2, [KeywordKind.Deathtouch], commander: true);
        var blocker = t.Creature(t.B, 4, 4);
        var loose = t.Creature(t.A, 3, 3, commander: true);
        var fake = new CombatReplacementFake { Instead = CombatReplacementFake.Doubled };
        t.Subject.Replacements = fake;
        t.Attack(t.A, new AttackDeclaration(general, new CombatTarget(t.B, null)), new AttackDeclaration(loose, new CombatTarget(t.B, null)));
        t.Block(t.B, new BlockDeclaration(blocker, general));

        t.Strike(firstStrike: false);

        Assert.True(Assert.IsType<DamageDealt>(fake.Offered[0]).Deathtouch);
        Assert.True(t.State.Objects[blocker].DealtDeathtouchDamage);
        Assert.Equal(6, t.State.Player(t.B).CommanderDamage[loose]);
        Assert.Equal(14, t.Life(t.B));
    }

    /// <summary>
    /// CR 616.1 and 101.4 — when the affected player has to say which effect applies first, the
    /// game stops. The question stands and the rest of the batch is not dealt.
    /// <para>
    /// <b>The gap this test records.</b> The remaining assignments are lost, not queued: answering
    /// the question resumes the replacement loop for the one event it was asked about, and nothing
    /// re-enters the combat damage step to deal the rest. What resumption would need is written in
    /// <c>seam-requests/E4b.md</c>; until it lands, a halt in combat leaves the pending question
    /// and the undealt assignments, which is what the last two assertions here say.
    /// </para>
    /// </summary>
    [Fact]
    [RuleRef("616.1")]
    public void A_halt_leaves_the_question_pending_and_the_rest_of_the_batch_undealt()
    {
        var t = new CombatTable();
        var first = t.Creature(t.A, 2, 2);
        var second = t.Creature(t.A, 3, 3);
        var asked = 0;
        var fake = new CombatReplacementFake
        {
            Instead = (state, proposed) => asked++ == 0
                ? CombatReplacementFake.Asks(state, proposed)
                : CombatReplacementFake.Stands(state, proposed),
        };
        t.Subject.Replacements = fake;
        t.Attack(t.A, new AttackDeclaration(first, new CombatTarget(t.B, null)), new AttackDeclaration(second, new CombatTarget(t.B, null)));

        var events = t.Strike(firstStrike: false);

        Assert.IsType<ChoiceRequested>(Assert.Single(events));
        Assert.NotNull(t.State.Pending);

        // The gap: the second attacker's assignment was never offered and never dealt.
        Assert.Single(fake.Offered);
        Assert.Equal(20, t.Life(t.B));
    }

    /// <summary>
    /// The same thing through the real <see cref="MtgEngine.Rules.Replacement.ReplacementSystem"/>,
    /// with a made-up "prevent all damage that would be dealt to you this turn" shield (CR 615.1)
    /// rather than a fake that says yes: the hit deals nothing and the lifelink gains nothing.
    /// </summary>
    [Fact]
    [RuleRef("615.1")]
    public void The_real_replacement_system_prevents_combat_damage_to_the_shielded_player()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 3, 3, [KeywordKind.Lifelink]);
        t.Subject.Replacements = Replacement(t);
        t.Enact(Shield(t.B));
        t.Attack(t.A, attacker, t.B);

        var events = t.Strike(firstStrike: false);

        Assert.Empty(events);
        Assert.Equal(20, t.Life(t.B));
        Assert.Equal(20, t.Life(t.A));
    }

    /// <summary>
    /// CR 615.1 — the shield covers the player it applies to and nobody else, so the same wiring
    /// that prevented the hit above deals this one in full and gains the life with it.
    /// </summary>
    [Fact]
    [RuleRef("615.1")]
    public void A_player_the_shield_does_not_cover_still_takes_the_damage()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 3, 3, [KeywordKind.Lifelink]);
        t.Subject.Replacements = Replacement(t);
        t.Enact(Shield(t.A));
        t.Attack(t.A, attacker, t.B);

        var events = t.Strike(firstStrike: false);

        Assert.Equal(3, Assert.IsType<DamageDealt>(events[0]).Amount);
        Assert.Equal(new LifeChanged(t.A, 3, "lifelink"), events[1]);
        Assert.Equal(17, t.Life(t.B));
        Assert.Equal(23, t.Life(t.A));
    }

    /// <summary>One combat damage event, by source, recipient and amount.</summary>
    private static void Dealt(GameEvent actual, ObjectId source, ObjectId toObject, int amount)
    {
        var damage = Assert.IsType<DamageDealt>(actual);
        Assert.Equal(source, damage.Source);
        Assert.Equal(toObject, damage.ToObject);
        Assert.Equal(amount, damage.Amount);
        Assert.True(damage.Combat);
    }

    /// <summary>CR 615.1 — "prevent all damage that would be dealt to you this turn", as state.</summary>
    private static ActiveEffect Shield(PlayerId player) =>
        new(1, new PreventDamage(null, null, null, ToPlayersToo: true), null, player, [], [player], new UntilEndOfTurn(), 1, FromStaticAbility: false);

    private static MtgEngine.Rules.Replacement.ReplacementSystem Replacement(CombatTable table) =>
        new(table.Query, table.Layers, new CombatReplacementNoInterpreter());
}
