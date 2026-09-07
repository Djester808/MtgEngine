using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 608.2b for players: the targets are re-checked as the spell begins to resolve, and a player
/// who has become an illegal target since it was announced is dropped exactly as an object is. The
/// prohibitions are the ones the casting system reads at announcement (CR 115.6, 702.16j), read
/// from the same point of view, so the two roads cannot disagree about who may be named.
/// </summary>
public sealed partial class InterpreterTests
{
    [Fact]
    [RuleRef("608.2b")]
    public void A_burn_spell_at_a_player_who_gained_protection_after_it_was_cast_does_not_resolve()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(
            table.A,
            new DealDamage(new Fixed(3), ToPlayer: new TargetPlayer(PlayerFilter.Any)),
            new StackEntry(null, null, [], [table.B], [], null, false));

        // CR 702.16j: protection from everything arrived after the announcement, so the spell's
        // only target is illegal and CR 608.2b says it does not resolve.
        table.Layers.Restrict(table.B, new PlayerLimits(ProtectionFromEverything: true));
        var events = table.Resolve(spell);

        Assert.Equal(20, table.Life(table.B));
        Assert.DoesNotContain(events, e => e is DamageDealt);
        Assert.Empty(table.Replacements.Proposed);
        Assert.Contains(events, e => e is StackObjectResolved);
    }

    [Fact]
    [RuleRef("608.2b")]
    public void A_burn_spell_at_a_player_nothing_protects_resolves()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(
            table.A,
            new DealDamage(new Fixed(3), ToPlayer: new TargetPlayer(PlayerFilter.Any)),
            new StackEntry(null, null, [], [table.B], [], null, false));

        var events = table.Resolve(spell);

        Assert.Equal(17, table.Life(table.B));
        Assert.Contains(events, e => e is DamageDealt);
    }

    [Fact]
    [RuleRef("608.2b")]
    public void A_spell_with_two_player_targets_resolves_against_the_one_that_is_still_legal()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(
            table.A,
            new DealDamage(new Fixed(2), ToPlayer: new TargetPlayer(PlayerFilter.Any)),
            new StackEntry(null, null, [], [table.A, table.B], [], null, false));

        table.Layers.Restrict(table.B, new PlayerLimits(ProtectionFromEverything: true));
        table.Resolve(spell);

        // CR 608.2b: one illegal target does not stop the spell — it resolves and does as much as
        // it can for the target that is still legal.
        Assert.Equal(18, table.Life(table.A));
        Assert.Equal(20, table.Life(table.B));
    }

    [Fact]
    [RuleRef("115.6")]
    public void A_player_whose_opponents_cannot_target_them_may_still_be_named_by_their_own_spell()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(
            table.A,
            new DealDamage(new Fixed(2), ToPlayer: new TargetPlayer(PlayerFilter.Any)),
            new StackEntry(null, null, [], [table.A], [], null, false));

        // CR 115.6: the card-text form is "by spells or abilities your opponents control", so it
        // is read from the resolving object's controller and does not stop them naming themselves.
        table.Layers.Restrict(table.A, new PlayerLimits(PlayerRestriction.BeTargeted));
        table.Resolve(spell);

        Assert.Equal(18, table.Life(table.A));
    }

    [Fact]
    [RuleRef("115.6")]
    public void A_player_their_opponents_cannot_target_is_dropped_from_an_opponents_spell()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(
            table.A,
            new DealDamage(new Fixed(2), ToPlayer: new TargetPlayer(PlayerFilter.Any)),
            new StackEntry(null, null, [], [table.B], [], null, false));

        table.Layers.Restrict(table.B, new PlayerLimits(PlayerRestriction.BeTargeted));
        var events = table.Resolve(spell);

        Assert.Equal(20, table.Life(table.B));
        Assert.DoesNotContain(events, e => e is DamageDealt);
    }
}
