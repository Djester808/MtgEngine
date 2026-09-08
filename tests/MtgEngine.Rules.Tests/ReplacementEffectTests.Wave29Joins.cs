using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using DamagePrevented = MtgEngine.Rules.Events.DamagePrevented;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The wave-29 join E9x asked for: an offer seeded with the effects that already applied to the
/// event before an interpreter halt (CR 616.1 with 614.5). Every definition here is invented.
/// </summary>
public sealed partial class ReplacementEffectTests
{
    /// <summary>
    /// CR 616.1: no effect applies to the same event twice. A shield the caller says already
    /// applied before the walk halted is not offered the event again when it is proposed once
    /// more, so the damage arrives whole; the same offer with no seed still meets the shield.
    /// </summary>
    [Fact]
    [RuleRef("616.1")]
    public void An_effect_the_caller_says_already_applied_is_not_offered_the_event_again()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(4, 4, "wave29 shielded thing"));
        var burner = board.Put(Board.Creature(5, 5, "wave29 burner"), owner: 1);
        var state = Wave28Shield(board.State, creature, new Fixed(3), null);

        var fresh = System().Replace(state, Damage(burner, creature, 5), new PlayerId(0), used: null);
        var resumed = System().Replace(state, Damage(burner, creature, 5), new PlayerId(0), used: ["e1"]);

        Assert.Equal(2, Assert.IsType<DamageDealt>(fresh.Events[^1]).Amount);
        Assert.Equal(5, Assert.IsType<DamageDealt>(Assert.Single(resumed.Events)).Amount);
        Assert.DoesNotContain(resumed.Events, e => e is DamagePrevented);
    }
}
