using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using Ev = MtgEngine.Rules.Events;

namespace MtgEngine.Rules.Triggers;

/// <summary>
/// The two designations-and-searches arms the events of waves 23 and 24 finally gave the matcher
/// something to read: a library being searched (CR 701.23f) and a player becoming the monarch
/// (CR 725.1). Both are whole-game happenings with no object of their own, so each fires about a
/// player and carries no subject object.
/// </summary>
internal sealed partial class TriggerMatching
{
    /// <summary>The trigger kinds added after wave 23's pass of the matcher, CR 603.2.</summary>
    [RuleRef("603.2")]
    private ImmutableList<TriggerSubject> Wave25(GameState state, Trigger trigger, Ev.GameEvent happened, EffectContext ctx) =>
        trigger switch
        {
            Searches searches => Searched(state, searches, happened, ctx),
            BecomesMonarch monarch => BecameMonarch(state, monarch, happened, ctx),
            _ => [],
        };

    /// <summary>
    /// CR 701.23f: an ability that triggers on a library being searched triggers. The event is
    /// written once per library when the search begins (CR 701.23h makes a repeated search of one
    /// library a single search, so the resumption writes nothing), whatever the search finds and
    /// whether it finds anything at all.
    /// <para>
    /// The firing is about <see cref="Ev.LibrarySearched.Searcher"/> and never about
    /// <see cref="Ev.LibrarySearched.Player"/>: the vocabulary's <see cref="Searches"/> has no zone
    /// field because every printed sentence of that shape watches the searcher, so an effect that
    /// sends a player into a library they do not own still fires it. The owner rides on the event so
    /// a stored game says which library was searched, and requiring the two to be equal here would
    /// silently stop that card firing. <see cref="Searches.Who"/> is a filter rather than a
    /// reference for the same reason <c>DealsDamage.ToPlayer</c> is: the trigger matches whoever
    /// searched instead of naming a player in advance.
    /// </para>
    /// </summary>
    [RuleRef("701.23f")]
    private ImmutableList<TriggerSubject> Searched(GameState state, Searches trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not Ev.LibrarySearched searched)
            return [];
        return _query.PlayerMatches(state, trigger.Who, searched.Searcher, ctx)
            ? [TriggerSubject.OfPlayer(searched.Searcher)]
            : [];
    }

    /// <summary>
    /// CR 725.1 with CR 725.3: a player becomes the monarch, and because only one player can be the
    /// monarch at a time the current one ceases to be as that happens. The event names the player
    /// who became it, so "whenever you become the monarch" fires for that player and for nobody
    /// else — the player who lost the designation did not become anything and nothing fires for
    /// them. A re-designation of the player who already holds it writes no event at all (the
    /// interpreter skips it, CR 725.3 leaving nothing to change), so it triggers nothing either.
    /// <para>
    /// The shipped comprehensive rules put the monarch at <b>725</b>; the vocabulary's
    /// <see cref="BecomesMonarch"/>, <c>MonarchChanged</c> and <c>GameState.Monarch</c> all cite 724,
    /// which is "Ending Turns and Phases" in that text. Those citations are in other folders and are
    /// asked for in <c>seam-requests/E6h.md</c>; this arm cites the rule the document actually has.
    /// </para>
    /// </summary>
    [RuleRef("725.1")]
    private ImmutableList<TriggerSubject> BecameMonarch(GameState state, BecomesMonarch trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not Ev.MonarchChanged changed)
            return [];
        return _query.Players(state, trigger.Who, ctx).Contains(changed.Player)
            ? [TriggerSubject.OfPlayer(changed.Player)]
            : [];
    }
}
