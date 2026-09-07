using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Query;

/// <summary>
/// The questions about this turn that need more of the turn's history than the state records
/// today. Each reads <see cref="TurnFacts"/>: with the field in place the rule is answered as
/// written, and without it the arm falls back to the weaker answer it already gave, or refuses
/// where a weaker answer would be a guess. The fallbacks are named in seam-requests/E0c.md and go
/// away when the seam lands, with no change to the arms in the other files.
/// </summary>
public sealed partial class Query
{
    private const string NoResolutionTally =
        "nothing counts how many times an ability has resolved this turn; see seam-requests/E0c.md";

    /// <summary>
    /// Morbid, CR 603.10's look-back: a creature died this turn. The departed object no longer
    /// exists, so the layers cannot be asked what it was; the honest reading is its last face
    /// (CR 603.10), and a face-down permanent is a 2/2 creature whatever that face says (CR 708.2).
    /// Until the history keeps the object, "something died" is the whole answer — a board where
    /// only a land was sacrificed answers yes, which is wrong and is the reason for the seam.
    /// </summary>
    [RuleRef("603.10")]
    private static bool CreatureDied(GameState state) =>
        TurnFacts.TryDied(state.History, out var died)
            ? died.Any(WasCreature)
            : state.History.Died.Count > 0;

    /// <summary>
    /// Revolt, CR 603.10: a permanent its controller controlled left the battlefield this turn.
    /// Control at the moment it left is not recorded either, so the departed object's
    /// <see cref="GameObject.InitialController"/> is the look-back — right for every permanent that
    /// was never stolen. Without the object the count is unqualified, as above.
    /// </summary>
    [RuleRef("603.10")]
    private bool Left(GameState state, PlayerRef who, EffectContext ctx)
    {
        var players = Players(state, who, ctx);
        if (players.Count == 0)
            return false;

        return TurnFacts.TryLeftBattlefield(state.History, out var left)
            ? left.Any(o => players.Contains(o.InitialController))
            : state.History.LeftBattlefield.Count > 0;
    }

    /// <summary>A creature by its last face, CR 603.10; face down it was a 2/2 creature, CR 708.2.</summary>
    [RuleRef("708.2")]
    private static bool WasCreature(GameObject departed) =>
        departed.FaceDown || (departed.Face.Types.Types & CardType.Creature) != 0;

    /// <summary>
    /// "if it dealt damage to you this turn", CR 120.3: a question about one source and one
    /// receiver, which the two totals cannot answer between them. With the pair map the candidate
    /// is the source and the player reference the receiver; without it the arm refuses rather than
    /// reading a total that is about someone else.
    /// </summary>
    [RuleRef("120.3")]
    private bool DealtDamage(GameState state, DealtDamageThisTurn dealt, ObjectId candidate, EffectContext ctx)
    {
        if (dealt.To is null)
            return state.History.DamageDealtBy.GetValueOrDefault(candidate) > 0;
        if (!TurnFacts.TryDamagePairs(state.History, out var pairs))
            throw Unanswerable(nameof(DealtDamageThisTurn), NoDamagePairs, "120.3");

        var receivers = Players(state, dealt.To, ctx);
        return pairs.Any(pair => pair.Key.Source == candidate && receivers.Contains(pair.Key.To) && pair.Value > 0);
    }

    /// <summary>
    /// "damage dealt to that player by those sources this turn", CR 120.3: the pair map summed over
    /// the sources the selector names and the players the reference names. Refuses without it.
    /// </summary>
    [RuleRef("120.3")]
    private int DamagePairTotal(GameState state, Selector by, PlayerRef to, EffectContext ctx)
    {
        if (!TurnFacts.TryDamagePairs(state.History, out var pairs))
            throw Unanswerable(nameof(DamageDealtThisTurn), NoDamagePairs, "120.3");

        var sources = Resolve(state, by, ctx);
        var receivers = Players(state, to, ctx);
        return pairs
            .Where(pair => sources.Contains(pair.Key.Source) && receivers.Contains(pair.Key.To))
            .Sum(pair => pair.Value);
    }

    /// <summary>
    /// "the first time this ability resolves each turn", CR 603.4. The tally counts resolutions
    /// that have finished, so the first check of the turn reads zero and holds. The ability is
    /// named by its source and its index: while it is on the stack its own entry says which
    /// (CR 601.2), and an intervening "if" checked before it is stacked cannot know the index, so
    /// there every ability of that source counts — exact for a source with one such ability, and
    /// the only reading available before the ability exists as an object.
    /// </summary>
    [RuleRef("603.4")]
    private static bool FirstResolutionThisTurn(GameState state, EffectContext ctx)
    {
        if (!TurnFacts.TryResolutions(state.History, out var tally))
            throw Unanswerable(nameof(FirstTimeThisTurn), NoResolutionTally, "603.4");
        if (ctx.Source is not { } source)
            return true;

        return AbilityIndexOnStack(state, source) is { } index
            ? tally.GetValueOrDefault((source, index)) == 0
            : !tally.Any(entry => entry.Key.Source == source && entry.Value > 0);
    }

    /// <summary>The index of the topmost ability on the stack that came from this source, CR 601.2.</summary>
    private static int? AbilityIndexOnStack(GameState state, ObjectId source)
    {
        var stack = state.In(ZoneKey.Shared(Cards.Zone.Stack));
        for (var i = stack.Count - 1; i >= 0; i--)
        {
            if (state.Objects.TryGetValue(stack[i], out var obj)
                && obj.Stack is { AbilityIndex: { } index } entry
                && (entry.Source ?? obj.Id) == source)
            {
                return index;
            }
        }

        return null;
    }
}
