using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// The targets as a resolution reads them, CR 601.2c with 608.2b: the objects and players
/// announced when the spell or ability was put on the stack, re-checked as it begins to resolve,
/// and remembered slot by slot so every target selector reads exactly what was announced for it.
/// </summary>
[RuleRef("608.2b")]
public sealed partial class EffectInterpreter
{
    /// <summary>
    /// The context a resolution or a resumption runs in once the targets have been re-checked,
    /// CR 608.2b: the still-legal objects and players, and every target selector's memory. The
    /// memory is filled after the re-check so a target that has become illegal is read by no
    /// step at all — the spell "doesn't perform any actions on it". A resumption does not fill it
    /// again: the check is made once, as the resolution begins, and what it remembered rode on the
    /// question and is put back by <c>Bind</c> — a target the earlier steps have already moved is
    /// still the target (CR 608.2h), which a second check would wrongly drop.
    /// </summary>
    [RuleRef("608.2b")]
    private EffectContext Rechecked(GameState state, GameObject obj, StackEntry entry, PlayerId controller, Effect? effect, bool resuming = false)
    {
        var context = ContextFor(state, obj, entry, controller, effect);
        var legal = LegalTargets(state, entry, effect, context);
        return context with
        {
            Targets = legal,
            TargetPlayers = LegalTargetPlayers(state, entry, controller),
            Remembered = effect is null || resuming ? context.Remembered : RememberTargets(state, context, effect, entry.Targets, legal),
        };
    }

    /// <summary>
    /// CR 601.2c with 608.2h: the targets were announced in <see cref="EffectWalk.TargetsIn"/>
    /// order, each selector taking as many as it names, so each selector's key — the card's own,
    /// or the slot key <see cref="EffectWalk.KeyTargets"/> gave an unkeyed one — remembers the
    /// announced objects that filled its slot and are still legal (CR 608.2b). The last selector
    /// takes every target still unclaimed, so a tree with one target selector reads the whole
    /// announced list exactly as it always did, whatever its count says; an earlier selector takes
    /// as many as its count works out to now (CR 601.2b: one when it names none). A slot past what
    /// was announced remembers nothing, so the selector resolves to nothing rather than to
    /// another slot's targets. A key the stack entry already carries is the earlier word.
    /// </summary>
    [RuleRef("601.2c")]
    private ImmutableDictionary<string, ImmutableList<ObjectId>> RememberTargets(
        GameState state,
        EffectContext context,
        Effect effect,
        ImmutableList<ObjectId> announced,
        ImmutableList<ObjectId> legal)
    {
        var remembered = context.Remembered;
        var selectors = EffectWalk.TargetsIn(effect);
        var at = 0;
        for (var i = 0; i < selectors.Count; i++)
        {
            var last = i == selectors.Count - 1;
            var (key, count) = selectors[i] switch
            {
                Cards.Target target => (target.Key, last || target.Count is null ? 1 : Math.Max(0, query.Evaluate(state, target.Count, context))),
                Cards.AnyTarget any => (any.Key, 1),
                _ => (null, 0),
            };
            var take = last ? Math.Max(0, announced.Count - at) : count;
            var taken = announced.Skip(at).Take(take).Where(legal.Contains).ToImmutableList();
            at += take;
            if (key is not null && !remembered.ContainsKey(key))
                remembered = remembered.SetItem(key, taken);
        }

        return remembered;
    }

    /// <summary>
    /// The targets that are still legal, CR 608.2b. Each chosen target is re-checked against the
    /// filter of the <see cref="Target"/> selector it came from, in the order the tree names them.
    /// </summary>
    [RuleRef("608.2b")]
    private ImmutableList<ObjectId> LegalTargets(GameState state, StackEntry entry, Effect? effect, EffectContext context)
    {
        if (entry.Targets.Count == 0)
            return entry.Targets;

        var filters = effect is null ? [] : EffectWalk.TargetsIn(effect);
        var legal = ImmutableList.CreateBuilder<ObjectId>();
        for (var i = 0; i < entry.Targets.Count; i++)
        {
            var id = entry.Targets[i];
            if (!state.Objects.ContainsKey(id))
                continue;
            var filter = FilterAt(filters, i);
            if (filter is null || query.Matches(state, filter, id, context))
                legal.Add(id);
        }

        return legal.ToImmutable();
    }

    /// <summary>
    /// The players still legally targeted, CR 608.2b with 115.6 and 702.16j. The two prohibitions
    /// are read exactly as the casting system reads them at announcement, from the same point of
    /// view, so a spell is not announced against a player it could not resolve against:
    /// <see cref="PlayerRestriction.BeTargeted"/> is the card-text form ("by spells or abilities
    /// your opponents control") and so lets the resolving object's own controller keep naming
    /// themselves, while protection from everything is the stronger one that no source targets
    /// through. A gift's promised player (CR 115.10) rides on the same list and is not a target,
    /// so nothing is re-checked for them.
    /// </summary>
    [RuleRef("608.2b")]
    private ImmutableList<PlayerId> LegalTargetPlayers(GameState state, StackEntry entry, PlayerId controller)
    {
        if (entry.TargetPlayers.Count == 0)
            return entry.TargetPlayers;

        var legal = ImmutableList.CreateBuilder<PlayerId>();
        foreach (var player in entry.TargetPlayers)
        {
            if (player == entry.GiftPromisedTo || CanBeTargeted(state, player, controller))
                legal.Add(player);
        }

        return legal.ToImmutable();
    }

    /// <summary>
    /// May this resolving object target that player, CR 115.6 with 702.16j? One reading for the
    /// re-check at resolution (CR 608.2b) and for the candidates a change of targets offers
    /// (CR 115.7a), so a seat the re-check would drop is never offered in the first place:
    /// <see cref="PlayerRestriction.BeTargeted"/> is the card-text form ("by spells or abilities
    /// your opponents control") and leaves the object's own controller able to name themselves,
    /// while protection from everything is the stronger one that no source targets through.
    /// </summary>
    [RuleRef("115.6")]
    private bool CanBeTargeted(GameState state, PlayerId player, PlayerId controller)
    {
        var limits = characteristics.PlayerRestrictions(state, player);
        return !limits.ProtectionFromEverything
            && (!limits.Has(PlayerRestriction.BeTargeted) || player == controller);
    }

    /// <summary>
    /// The players on a stack entry that are targets, CR 608.2b: everyone the spell named, less a
    /// gift's promised player, who travels on the same list and is not one (CR 115.10).
    /// </summary>
    [RuleRef("115.10")]
    private static ImmutableList<PlayerId> Targeted(ImmutableList<PlayerId> players, StackEntry entry) =>
        entry.GiftPromisedTo is { } promised ? players.RemoveAll(p => p == promised) : players;
}
