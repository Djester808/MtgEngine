using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Query;

/// <summary>
/// "Any target", CR 115.4. The phrase is one selector and one choice: a creature, a planeswalker
/// or a battle on the battlefield, or a player. Both halves are chosen as the spell or ability
/// goes on the stack (CR 601.2c), and the halves ride in the two slots the command already has —
/// an object in <c>Targets</c>, a player in <c>TargetPlayers</c> — which is why the record carries
/// no player field of its own.
///
/// The contract the interpreter reads: <see cref="Resolve"/> answers the object half, and answers
/// nothing when the chosen target was a player, so a caller that finds no object reads
/// <c>context.TargetPlayers</c> for the other half. One AnyTarget never fills both slots — a
/// reading that names two things to damage is two targets, not one.
/// </summary>
public sealed partial class Query
{
    /// <summary>
    /// The object half of "any target", CR 115.4: a creature, a planeswalker or a battle. The
    /// filter names no zone, and a filter with no zone describes the battlefield (CR 110.1), where
    /// all three live — so a land, a spell on the stack and a card in a graveyard all fail it.
    /// </summary>
    public static ObjectFilter AnyTargetFilter { get; } =
        new(AnyTypes: CardType.Creature | CardType.Planeswalker | CardType.Battle);

    /// <summary>
    /// The re-check at resolution, CR 608.2b: an object chosen as "any target" is still a legal
    /// target while it still matches the CR 115.4 filter. It sees the object as the layers made it
    /// (CR 613), so a creature that has lost its types since being targeted is no longer legal.
    /// </summary>
    [RuleRef("608.2b")]
    public bool MatchesAnyTarget(GameState state, ObjectId candidate, EffectContext context) =>
        Matches(state, AnyTargetFilter, candidate, context);

    /// <summary>
    /// The other half of the same re-check: a player is a legal "any target" while they are still
    /// in the game (CR 115.4, 800.4a). A player who has lost is no longer a player, so a spell
    /// targeting only them is countered on resolution like any other illegal target.
    /// </summary>
    [RuleRef("608.2b")]
    public static bool MatchesAnyTarget(GameState state, PlayerId candidate)
    {
        ArgumentNullException.ThrowIfNull(state);
        return candidate.Value >= 0
            && candidate.Value < state.Players.Count
            && !state.Player(candidate).HasLost;
    }
}
