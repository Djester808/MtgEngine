using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 702.15b for the hit that raised the question. The amount a halted hit finally deals is
/// decided inside <see cref="IReplacementSystem.Resume"/>, so the resumption is told the events
/// that finished it and the gain follows the damage actually dealt — the gap E4c wrote down and
/// wave 9 closes. The plain three-argument resumption, which is told nothing, is unchanged.
/// </summary>
public sealed partial class CombatReplacementTests
{
    /// <summary>
    /// CR 702.15b — a lifelink attacker whose own hit raised the order question gains exactly what
    /// that hit dealt once it is answered, and then the rest of the batch is dealt. Before the
    /// resumption was told what was dealt this hit gained nothing at all.
    /// </summary>
    [Fact]
    [RuleRef("702.15b")]
    public void The_halted_hits_lifelink_gains_what_that_hit_dealt()
    {
        var t = new CombatTable();
        var lifelinker = t.Creature(t.A, 3, 3, [KeywordKind.Lifelink]);
        var second = t.Creature(t.A, 2, 2);
        var fake = CombatReplacementFake.HaltingOn(0);
        t.Subject.Replacements = fake;
        t.Attack(
            t.A,
            new AttackDeclaration(lifelinker, new CombatTarget(t.B, null)),
            new AttackDeclaration(second, new CombatTarget(t.B, null)));
        t.Strike(firstStrike: false);

        var events = Answer(t, fake);

        // The answer, the hit it finished, that hit's lifelink, then the hit that was still owed.
        Assert.Null(t.State.Pending);
        Assert.IsType<ChoiceAnswered>(events[0]);
        Assert.Equal(lifelinker, Assert.IsType<DamageDealt>(events[1]).Source);
        Assert.Equal(new LifeChanged(t.A, 3, "lifelink"), events[2]);
        Assert.Equal(second, Assert.IsType<DamageDealt>(events[3]).Source);
        Assert.Equal(23, t.Life(t.A));
        Assert.Equal(15, t.Life(t.B));
    }

    /// <summary>
    /// CR 615.1 with 702.15b — the answer to the question prevented the hit outright, so it dealt
    /// nothing and gains nothing. Lifelink follows the damage actually dealt, and there was none.
    /// This is the real <see cref="MtgEngine.Rules.Replacement.ReplacementSystem"/>: two shields on
    /// the same player raise the question, and the one the answer names prevents the damage.
    /// </summary>
    [Fact]
    [RuleRef("702.15b")]
    public void A_halted_hit_the_answer_prevents_gains_no_lifelink()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 3, 3, [KeywordKind.Lifelink]);
        var real = Replacement(t);
        t.Subject.Replacements = real;
        t.Enact(Shield(t.B, 1));
        t.Enact(Shield(t.B, 2));
        t.Attack(t.A, attacker, t.B);

        var halted = t.Strike(firstStrike: false);
        Assert.Equal("0", Assert.IsType<ChoiceRequested>(Assert.Single(halted)).Choice.Context.Extra[ChoiceKinds.CombatDamageIndex]);

        var events = Answer(t, real);

        // The answer, and nothing else the hit did — no damage, no gain — followed by the priority
        // the finished batch opens the combat damage step with, CR 510.3.
        // CR 615.13: the prevention records itself between the answer and the priority pass.
        Assert.Collection(
            events.Where(e => e is not DamagePrevented),
            e => Assert.IsType<ChoiceAnswered>(e),
            e => Assert.Equal(new PriorityGranted(t.A), e));
        Assert.Null(t.State.Pending);
        Assert.Equal(20, t.Life(t.A));
        Assert.Equal(20, t.Life(t.B));
    }

    /// <summary>
    /// CR 702.2b — deathtouch rides on the <see cref="DamageDealt"/> the replacement system applied,
    /// so a halted hit still marks what it hit. A deathtouch source assigns one as lethal and the
    /// rest to the same blocker (CR 510.1c), so it hits twice: the halt is between the two, and the
    /// resumption gains for the halted hit and then deals and gains for the other.
    /// </summary>
    [Fact]
    [RuleRef("702.2b")]
    public void A_halted_deathtouch_hit_still_marks_what_it_hit()
    {
        var t = new CombatTable();
        var attacker = t.Creature(t.A, 3, 3, [KeywordKind.Deathtouch, KeywordKind.Lifelink]);
        var blocker = t.Creature(t.B, 5, 5);
        var fake = CombatReplacementFake.HaltingOn(0);
        t.Subject.Replacements = fake;
        t.Attack(t.A, attacker, t.B);
        t.Block(t.B, new BlockDeclaration(blocker, attacker));
        t.Strike(firstStrike: false);

        var events = Answer(t, fake);

        Assert.True(t.State.Objects[blocker].DealtDeathtouchDamage);
        Assert.True(Assert.IsType<DamageDealt>(events[1]).Deathtouch);
        Assert.Equal(3, t.DamageOn(blocker));

        // One gain per hit, CR 702.15b: the lethal one the question stopped on, then the rest.
        Assert.Equal(new LifeChanged(t.A, 1, "lifelink"), events[2]);
        Assert.Equal(23, t.Life(t.A));
    }

    /// <summary>
    /// CR 510.2 — the three-argument resumption is unchanged: told only where to pick up, it skips
    /// the hit the question was about and deals the rest, and gains nothing for the skipped hit
    /// because it was never told what that hit dealt.
    /// </summary>
    [Fact]
    [RuleRef("510.2")]
    public void The_resumption_that_is_not_told_what_was_dealt_deals_the_rest_and_gains_nothing()
    {
        var t = new CombatTable();
        var lifelinker = t.Creature(t.A, 2, 2, [KeywordKind.Lifelink]);
        var second = t.Creature(t.A, 3, 3);
        t.Attack(
            t.A,
            new AttackDeclaration(lifelinker, new CombatTarget(t.B, null)),
            new AttackDeclaration(second, new CombatTarget(t.B, null)));

        var events = t.Apply(t.Subject.ResumeCombatDamage(t.State, firstStrike: false, from: 0));

        var dealt = Assert.IsType<DamageDealt>(Assert.Single(events));
        Assert.Equal(second, dealt.Source);
        Assert.Equal(3, dealt.Amount);
        Assert.Equal(20, t.Life(t.A));
        Assert.Equal(17, t.Life(t.B));
    }
}
