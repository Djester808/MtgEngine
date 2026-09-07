using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Layers;

/// <summary>
/// The join between the layer system's own filter matcher and the query layer. CR 611.3a with
/// 613.1: a static ability's effect applies, at any given moment, to whatever its filter admits
/// now, so the filter is read afresh on every call. The narrow matcher in
/// <see cref="LayerSystem"/> answers the fields it can read off an object and its computed
/// characteristics; every other field — the <see cref="Predicate"/> tail, a mana value, the
/// combat fields, a bound that has to be counted — is asked of <see cref="IQuery"/>, which is
/// the one matcher that answers them, with the static ability's source as the context (CR 109.5).
/// </summary>
public sealed partial class LayerSystem
{
    /// <summary>
    /// How deep this thread is inside a filter being asked of the query. The query reads an
    /// object's characteristics through <see cref="Of"/>, and the statics whose filters are being
    /// gathered are gathered from every object on the battlefield, so an unguarded read would
    /// gather them again to answer the very filter that is gathering them. CR 613.8's dependency
    /// rules are not built, and the resolution is the one the narrow matcher has always taken:
    /// a filter reads the board without the board's static abilities. Effects from resolved
    /// spells and abilities stay visible, which is what the example under CR 613.9 needs.
    /// </summary>
    [ThreadStatic]
    private static int _matching;

    /// <summary>True while this thread is answering a filter: characteristics are read without statics.</summary>
    private static bool MatchingFilter => _matching > 0;

    /// <summary>
    /// Ask the query the whole filter, with the static ability's source as "this" and its
    /// controller as "you" (CR 109.5). Nothing the query cannot answer reaches a table as a
    /// guess: the query refuses with its own rule, and a shape it never built is turned into a
    /// refusal here rather than thrown out of the layer system.
    /// </summary>
    [RuleRef("611.3a")]
    private bool MatchesThroughQuery(
        GameState state,
        ObjectFilter filter,
        GameObject candidate,
        GameObject source,
        PlayerId controller)
    {
        _matching++;
        try
        {
            return Query.Matches(state, filter, candidate.Id, ContextOf(state, source.Id, controller));
        }
        catch (NotImplementedException unbuilt)
        {
            throw new RefusedException(
                $"A static ability's filter asked the query layer for something it has not built: {unbuilt.Message}",
                "611.3a");
        }
        finally
        {
            _matching--;
        }
    }

    /// <summary>
    /// Which filters go to the query: the ones carrying a field this system's own matcher does
    /// not read, and the ones whose bound is not a literal. A static ability's numbers are read
    /// at the moment the effect applies (CR 611.3a), so "creatures with power less than the
    /// number of Zombies you control" is counted now, through the query, rather than refused.
    /// </summary>
    [RuleRef("611.3a")]
    private static bool NeedsQuery(ObjectFilter filter) =>
        filter.Also is not null
        || filter.ManaValue is not null
        || filter.Attacking is not null
        || filter.Blocking is not null
        || filter.Blocked is not null
        || filter.InCombat is not null
        || filter.Historic is not null
        || filter.Outlaw is not null
        || filter.Spell is not null
        || filter.IsCommander is not null
        || filter.HasBasicLandType is not null
        || filter.ManaCostIs is not null
        || filter.NotInCommanderIdentityColors is not null
        || Counted(filter.Power)
        || Counted(filter.Toughness)
        || Counted(filter.Loyalty)
        || Unnamed(filter.ControlledBy)
        || Unnamed(filter.OwnedBy);

    /// <summary>A comparison whose bound has to be worked out rather than read, CR 611.3a.</summary>
    private static bool Counted(Comparison? comparison) => comparison is { Value: not Fixed };

    /// <summary>
    /// A player this system's own matcher does not name, CR 109.5. It reads the four references a
    /// static ability's filter almost always uses; anything else — the monarch, the player who
    /// was dealt damage — is the query's to resolve, and its refusal is the honest answer when it
    /// cannot either.
    /// </summary>
    [RuleRef("109.5")]
    private static bool Unnamed(PlayerRef? player) =>
        player is not (null or You or EachOpponent or EachPlayer or ActivePlayer);

    /// <summary>
    /// The context a static ability's filter and amounts are read in, CR 113.8 with 109.5 and
    /// 608.2d: the ability's source is "this", its controller is "you", the values that source
    /// chose are the choices its own text refers back to ("creatures of the chosen color"), and
    /// a static ability has no targets, no X and no memory of a resolution.
    /// </summary>
    [RuleRef("109.5")]
    private static EffectContext ContextOf(GameState state, ObjectId source, PlayerId controller) =>
        new(
            source,
            controller,
            [],
            [],
            null,
            ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
            state.Objects.TryGetValue(source, out var self) ? self.Chosen : ImmutableDictionary<string, string>.Empty,
            ImmutableDictionary<string, int>.Empty,
            [],
            null);
}
