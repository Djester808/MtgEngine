using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Query;

/// <summary>
/// The questions about this turn that read the turn's history: what died and what left the
/// battlefield, damage per source-and-receiver pair, and how many times an ability has resolved.
/// Each is answered from a field of <see cref="TurnHistory"/> as the rule is written; the guarded
/// arms that stood here while those fields were missing (seam-requests/E0c.md) are gone with the
/// guard they read.
/// </summary>
public sealed partial class Query
{
    /// <summary>
    /// Morbid, CR 603.10's look-back: a creature died this turn. The departed object no longer
    /// exists, so the layers cannot be asked what it was; the honest reading is its last face
    /// (CR 603.10), and a face-down permanent is a 2/2 creature whatever that face says (CR 708.2).
    /// A turn in which only a land was sacrificed is not morbid.
    /// </summary>
    [RuleRef("603.10")]
    private static bool CreatureDied(GameState state) => state.History.Died.Any(WasCreature);

    /// <summary>
    /// Revolt, CR 603.10: a permanent its controller controlled left the battlefield this turn.
    /// Control at the moment it left is not recorded, so the departed object's
    /// <see cref="GameObject.InitialController"/> is the look-back — right for every permanent that
    /// was never stolen. An opponent's permanent leaving does not enable yours.
    /// </summary>
    [RuleRef("603.10")]
    private bool Left(GameState state, PlayerRef who, EffectContext ctx)
    {
        var players = Players(state, who, ctx);
        return players.Count != 0 && state.History.LeftBattlefield.Any(o => players.Contains(o.InitialController));
    }

    /// <summary>
    /// "if you created a token this turn", CR 111.1. The turn keeps every token as it was created,
    /// under the player who created it, so the answer survives the token: one sacrificed, exiled or
    /// gone to the "cease to exist" state-based action of CR 111.7 still counts, exactly as
    /// <see cref="Left"/> counts a permanent that has left. A filter is therefore read against the
    /// token's own characteristics at creation and never against the state, which no longer has it.
    /// </summary>
    [RuleRef("111.1")]
    private bool CreatedToken(GameState state, CreatedTokenThisTurn condition, EffectContext ctx)
    {
        var creators = Players(state, condition.Who, ctx);
        if (creators.Count == 0)
            return false;

        foreach (var creator in creators)
        {
            if (!state.History.TokensCreated.TryGetValue(creator, out var made))
                continue;
            if (condition.What is not { } what)
                return made.Count > 0;
            if (made.Any(token => WasAsCreated(state, what, token, ctx)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Does an object the state no longer holds match a description? Its characteristics are the
    /// printed ones (CR 613.1), because the layers of CR 613 are computed from what exists now;
    /// its zone is the one it was created in. A filter whose extra predicate asks about the object
    /// in play (<see cref="ObjectFilter.Also"/>) has no answer here rather than a guessed one.
    /// </summary>
    [RuleRef("603.10")]
    private bool WasAsCreated(GameState state, ObjectFilter filter, GameObject snapshot, EffectContext ctx)
    {
        if (filter.Also is not null)
            throw Unanswerable(nameof(CreatedTokenThisTurn), "a look-back filter cannot ask a predicate of an object the state no longer holds", "111.1");
        return snapshot.Zone.Zone == ZoneOf(filter)
            && MatchesAs(state, filter, snapshot, MtgEngine.Rules.Layers.LayerSystem.PrintedCharacteristics(snapshot), ctx);
    }

    /// <summary>A creature by its last face, CR 603.10; face down it was a 2/2 creature, CR 708.2.</summary>
    [RuleRef("708.2")]
    private static bool WasCreature(GameObject departed) =>
        departed.FaceDown || (departed.Face.Types.Types & CardType.Creature) != 0;

    /// <summary>
    /// "if it dealt damage to you this turn", CR 120.3: a question about one source and one
    /// receiver, which the two totals cannot answer between them. The pair map is what answers it:
    /// the candidate is the source and the player reference the receiver.
    /// </summary>
    [RuleRef("120.3")]
    private bool DealtDamage(GameState state, DealtDamageThisTurn dealt, ObjectId candidate, EffectContext ctx)
    {
        if (dealt.To is null)
            return state.History.DamageDealtBy.GetValueOrDefault(candidate) > 0;

        var receivers = Players(state, dealt.To, ctx);
        return state.History.DamageDealtToPlayer
            .Any(pair => pair.Key.Source == candidate && receivers.Contains(pair.Key.To) && pair.Value > 0);
    }

    /// <summary>
    /// "damage dealt to that player by those sources this turn", CR 120.3: the pair map summed over
    /// the sources the selector names and the players the reference names.
    /// </summary>
    [RuleRef("120.3")]
    private int DamagePairTotal(GameState state, Selector by, PlayerRef to, EffectContext ctx)
    {
        var pairs = state.History.DamageDealtToPlayer;
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
        if (ctx.Source is not { } source)
            return true;

        var tally = state.History.AbilitiesResolvedThisTurn;
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
