using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
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
public sealed partial class CombatReplacementTests
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

        // CR 615.13: a full prevention records itself; what must not appear is damage or a life change.
        Assert.DoesNotContain(events, e => e is DamageDealt or LifeChanged);
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
        Assert.Equal(6, t.State.Player(t.B).CommanderDamage[CommanderKeys.Commander(t.State, loose)]);
        Assert.Equal(14, t.Life(t.B));
    }

    /// <summary>
    /// CR 616.1 and 101.4 — when the affected player has to say which effect applies first, the
    /// game stops: the question stands and the rest of the batch is not dealt yet. CR 510.2 says
    /// where it will pick up, so the question carries the step it halted in and the index of the
    /// hit it was asked about. Two marks and no continuation: the answer alone is enough.
    /// </summary>
    [Fact]
    [RuleRef("616.1")]
    public void A_halt_marks_the_question_with_its_step_and_the_hit_it_stopped_on()
    {
        var t = Halting(out var fake, out _, out _);

        var events = t.Strike(firstStrike: false);

        var asked = Assert.IsType<ChoiceRequested>(Assert.Single(events));
        Assert.Same(asked.Choice, t.State.Pending);
        Assert.Equal(ChoiceKinds.RegularDamage, asked.Choice.Context.Extra[ChoiceKinds.CombatDamageStep]);
        Assert.Equal("0", asked.Choice.Context.Extra[ChoiceKinds.CombatDamageIndex]);

        // Nothing after the halted hit has happened yet: CR 101.4, the game is waiting.
        Assert.Single(fake.Offered);
        Assert.Equal(20, t.Life(t.B));
    }

    /// <summary>CR 510.4 — the first-strike step marks itself as the step it is.</summary>
    [Fact]
    [RuleRef("510.4")]
    public void A_halt_in_the_first_strike_step_says_so()
    {
        var t = new CombatTable();
        var striker = t.Creature(t.A, 2, 2, [KeywordKind.FirstStrike]);
        t.Subject.Replacements = CombatReplacementFake.HaltingOn(0);
        t.Attack(t.A, striker, t.B);

        var events = t.Strike(firstStrike: true);

        var asked = Assert.IsType<ChoiceRequested>(Assert.Single(events));
        Assert.Equal(ChoiceKinds.FirstStrikeDamage, asked.Choice.Context.Extra[ChoiceKinds.CombatDamageStep]);
    }

    /// <summary>
    /// CR 510.2 and 616.1 — the gap wave 7 recorded, closed. Answering the question finishes the
    /// one event it was about, and then the rest of the batch is dealt: the second attacker's
    /// damage is offered and lands, which is what nothing did before.
    /// </summary>
    [Fact]
    [RuleRef("510.2")]
    public void Answering_the_question_deals_the_rest_of_the_batch()
    {
        var t = Halting(out var fake, out var first, out var second);
        t.Strike(firstStrike: false);

        var events = Answer(t, fake);

        // The halted hit, finished by the replacement system, then the hit that was still owed.
        Assert.Null(t.State.Pending);
        Assert.Equal(2, fake.Offered.Count);
        Assert.Equal(second, Assert.IsType<DamageDealt>(fake.Offered[1]).Source);
        Assert.Equal(15, t.Life(t.B));
        Assert.Contains(events, e => e is DamageDealt { Amount: 3 } d && d.Source == second);

        // The answer itself is the first event, then the hit it finished, CR 101.4.
        Assert.IsType<ChoiceAnswered>(events[0]);
        Assert.Equal(first, Assert.IsType<DamageDealt>(events[1]).Source);
    }

    /// <summary>
    /// CR 616.1 — a second question inside the same batch is a halt like the first, and carries its
    /// own marks, so resuming it picks up after the hit it stopped on rather than at the top.
    /// </summary>
    [Fact]
    [RuleRef("616.1")]
    public void A_second_halt_in_the_same_batch_carries_its_own_marks()
    {
        var t = new CombatTable();
        var first = t.Creature(t.A, 2, 2);
        var second = t.Creature(t.A, 3, 3);
        var third = t.Creature(t.A, 4, 4);
        var fake = CombatReplacementFake.HaltingOn(0, 1);
        t.Subject.Replacements = fake;
        t.Attack(
            t.A,
            new AttackDeclaration(first, new CombatTarget(t.B, null)),
            new AttackDeclaration(second, new CombatTarget(t.B, null)),
            new AttackDeclaration(third, new CombatTarget(t.B, null)));
        t.Strike(firstStrike: false);

        var events = Answer(t, fake);

        var asked = Assert.IsType<ChoiceRequested>(events[^1]);
        Assert.Equal("1", asked.Choice.Context.Extra[ChoiceKinds.CombatDamageIndex]);
        Assert.Equal(18, t.Life(t.B));

        // And the second answer finishes the batch: the third attacker's damage is dealt too.
        Answer(t, fake);
        Assert.Null(t.State.Pending);
        Assert.Equal(11, t.Life(t.B));
    }

    /// <summary>
    /// CR 702.15b and 702.2b — a resumed batch is the same batch, so what follows the damage
    /// follows the damage actually dealt: the surviving attacker's lifelink gains what it dealt,
    /// and its deathtouch is marked on what it hit. A deathtouch attacker assigns lethal (one, CR
    /// 702.2b) and then the rest, so it hits twice and gains for each hit, CR 702.15b.
    /// </summary>
    [Fact]
    [RuleRef("702.15b")]
    public void A_resumed_batch_gains_lifelink_and_marks_deathtouch_for_the_damage_dealt()
    {
        var t = new CombatTable();
        var first = t.Creature(t.A, 2, 2);
        var second = t.Creature(t.A, 3, 3, [KeywordKind.Lifelink, KeywordKind.Deathtouch]);
        var blocker = t.Creature(t.B, 5, 5);
        var fake = CombatReplacementFake.HaltingOn(0);
        t.Subject.Replacements = fake;
        t.Attack(t.A, new AttackDeclaration(first, new CombatTarget(t.B, null)), new AttackDeclaration(second, new CombatTarget(t.B, null)));
        t.Block(t.B, new BlockDeclaration(blocker, second));
        t.Strike(firstStrike: false);

        var events = Answer(t, fake);

        Assert.Equal(3, events.OfType<LifeChanged>().Where(l => string.Equals(l.Reason, "lifelink", StringComparison.Ordinal)).Sum(l => l.Delta));
        Assert.Equal(23, t.Life(t.A));
        Assert.Equal(3, t.DamageOn(blocker));
        Assert.True(t.State.Objects[blocker].DealtDeathtouchDamage);
        Assert.Equal(18, t.Life(t.B));
    }

    /// <summary>
    /// The same through the real <see cref="MtgEngine.Rules.Replacement.ReplacementSystem"/>: two
    /// prevention shields both apply to the same hit, so CR 616.1 asks which goes first, the answer
    /// prevents it, and the batch carries on — into the second attacker's hit, which the same two
    /// shields make the same question about, marked with its own index this time.
    /// </summary>
    [Fact]
    [RuleRef("616.1")]
    public void The_real_replacement_system_asks_resumes_and_asks_again()
    {
        var t = new CombatTable();
        var first = t.Creature(t.A, 3, 3);
        var second = t.Creature(t.A, 4, 4);
        var real = Replacement(t);
        t.Subject.Replacements = real;
        t.Enact(Shield(t.B, 1));
        t.Enact(Shield(t.B, 2));
        t.Attack(t.A, new AttackDeclaration(first, new CombatTarget(t.B, null)), new AttackDeclaration(second, new CombatTarget(t.B, null)));

        var halted = t.Strike(firstStrike: false);
        Assert.Equal("0", Assert.IsType<ChoiceRequested>(Assert.Single(halted)).Choice.Context.Extra[ChoiceKinds.CombatDamageIndex]);

        var events = Answer(t, real);

        var again = Assert.IsType<ChoiceRequested>(events[^1]);
        Assert.Equal(ChoiceKinds.ReplacementOrder, again.Choice.Kind);
        Assert.Equal("1", again.Choice.Context.Extra[ChoiceKinds.CombatDamageIndex]);

        // Both hits are shielded, so answering the second question finishes a batch that dealt nothing.
        Answer(t, real);
        Assert.Null(t.State.Pending);
        Assert.Equal(20, t.Life(t.B));
    }

    /// <summary>
    /// CR 101.4 — no continuation is stored anywhere: the halt, the answer and the rest of the
    /// batch are all events, so folding the log gives the same board the live game reached.
    /// </summary>
    [Fact]
    [RuleRef("101.4")]
    public void A_game_replayed_from_its_log_reaches_the_same_state()
    {
        var t = Halting(out var fake, out _, out _);
        t.Strike(firstStrike: false);
        Answer(t, fake);

        var replayed = GameReducer.Replay([.. t.Log]);

        Assert.Equal(t.Life(t.B), replayed.Player(t.B).Life);
        Assert.Equal(t.Life(t.A), replayed.Player(t.A).Life);
        Assert.Null(replayed.Pending);
        Assert.Equal(t.State.NextChoiceId, replayed.NextChoiceId);
    }

    /// <summary>Two attackers, and a replacement system that halts on the first of them.</summary>
    private static CombatTable Halting(out CombatReplacementFake fake, out ObjectId first, out ObjectId second)
    {
        var t = new CombatTable();
        first = t.Creature(t.A, 2, 2);
        second = t.Creature(t.A, 3, 3);
        fake = CombatReplacementFake.HaltingOn(0);
        t.Subject.Replacements = fake;
        t.Attack(t.A, new AttackDeclaration(first, new CombatTarget(t.B, null)), new AttackDeclaration(second, new CombatTarget(t.B, null)));
        return t;
    }

    /// <summary>
    /// Answer the open question the way a game does: through the choice system, which records the
    /// answer, hands the halted event to the replacement system, asks the combat system for the
    /// rest of the batch (CR 616.1, 510.2) and then asks the turn system to open the step
    /// (CR 510.3 with 117.3b). The turn system is the real one because that last grant is its
    /// rule and no longer spelled a second time in the choice system.
    /// </summary>
    private static ImmutableList<GameEvent> Answer(CombatTable table, IReplacementSystem replacements)
    {
        var pending = table.State.Pending ?? throw new InvalidOperationException("nothing is waiting on an answer.");
        var choices = new ChoiceSystem(
            new ChoiceResumptionFakeZones(),
            new MtgEngine.Rules.Turn.TurnSystem(new ChoiceResumptionFakeZones(), new TurnFixture.FakeCasting()),
            new CombatReplacementResumable(replacements),
            null,
            table.Subject);
        return table.Apply(choices.Answer(table.State, new AnswerChoice(pending.Player, pending.Id, [pending.Options[0].Id])));
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

        // CR 615.13: a full prevention records itself; what must not appear is damage or a life change.
        Assert.DoesNotContain(events, e => e is DamageDealt or LifeChanged);
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
    private static ActiveEffect Shield(PlayerId player, int id = 1) =>
        new(id, new PreventDamage(null, null, null, ToPlayersToo: true), null, player, [], [player], new UntilEndOfTurn(), id, FromStaticAbility: false);

    private static MtgEngine.Rules.Replacement.ReplacementSystem Replacement(CombatTable table) =>
        new(table.Query, table.Layers, new CombatReplacementNoInterpreter());
}
