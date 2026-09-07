using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Query;

/// <summary>
/// Which objects a selector names and which players a player reference means. Targets are
/// chosen when a spell is cast (CR 601.2c) and choices when it resolves (CR 608.2), so both
/// are read out of the context here rather than made.
/// </summary>
public sealed partial class Query
{
    public ImmutableList<ObjectId> Resolve(GameState state, Selector selector, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(selector);
        return selector switch
        {
            Self => context.Source is { } source && state.Objects.ContainsKey(source) ? [source] : [], // CR 109.5
            Each each => [.. Candidates(state, each.Filter, context)],

            // The targets were chosen when the spell was cast, CR 601.2c; a keyed target is the
            // one an earlier step remembered under that key.
            Target target => target.Key is { } key && context.Remembered.TryGetValue(key, out var remembered)
                ? remembered
                : context.Targets,
            // "Any target", CR 115.4: one target chosen at cast time (CR 601.2c) from among the
            // creatures, planeswalkers and battles on the battlefield and the players. The object
            // half rides in Targets and the player half in TargetPlayers, so this answers the
            // objects and answers none when a player was chosen — the caller then reads
            // context.TargetPlayers. See Query.Targets.cs for the filter and the CR 608.2b re-check.
            AnyTarget any => any.Key is { } anyKey && context.Remembered.TryGetValue(anyKey, out var chosenTarget)
                ? chosenTarget
                : context.Targets,
            ThisSpellsTargets => context.Targets,
            Choose choose => choose.Key is { } key && context.Remembered.TryGetValue(key, out var chosen)
                ? chosen
                : throw Unanswerable(nameof(Choose), "the choice has not been made and recorded yet (CR 608.2)"),
            Remembered remembered => context.Remembered.GetValueOrDefault(remembered.Key, []),

            // "Enchanted creature", "equipped creature": the object this source is attached to,
            // CR 301.5c. An Aura attached to a player (CR 303.4f) is attached to no object, so
            // this rightly answers none — the player is reached through a PlayerRef, and the
            // vocabulary has no record for "enchanted player" yet; seam-requests/E5c.md asks for
            // one. See Players below, where the same gap is named.
            Attached => context.Source is { } source && state.Objects.TryGetValue(source, out var self)
                && self.AttachedTo is { } host && state.Objects.ContainsKey(host)
                ? [host] : [], // CR 301.5c
            Attachments attachments => [.. state.Objects.Values
                .Where(o => o.AttachedTo == context.Source)
                .OrderBy(o => o.Id.Value)
                .Select(o => o.Id)
                .Where(id => attachments.Filter is null || Matches(state, attachments.Filter, id, context))], // CR 301.5

            // The objects a trigger was about, subject first, then the other one, CR 603.6.
            Triggering triggering => Trigger(context, triggering.Role),

            Cards.Top top => TopOf(state, top, context),
            Union union => [.. union.Of.SelectMany(s => Resolve(state, s, context)).Distinct()],

            Cards.Commander => throw Unanswerable(nameof(Cards.Commander), NoCommander),
            RandomFrom => throw Unanswerable(nameof(RandomFrom), "a random selection draws from the seeded generator and so belongs to a subsystem that may change state (CR 103.1)"),
            SourceOfDamage => throw Unanswerable(nameof(SourceOfDamage), "the source of damage reaches the query layer through the triggering objects, not through state (CR 608.2)"),
            _ => throw Unanswerable(selector.GetType().Name, "no arm in the query layer"),
        };
    }

    private static ImmutableList<ObjectId> Trigger(EffectContext context, TriggerRole role) => role switch
    {
        TriggerRole.Subject => context.Triggering.Count > 0 ? [context.Triggering[0]] : [],
        TriggerRole.Other => context.Triggering.Count > 1 ? [context.Triggering[1]] : [],
        _ => throw new NotImplementedException($"Trigger role {role} is not implemented."),
    };

    /// <summary>"the top card of your library": the first cards of an ordered zone, which is top-first, CR 401.1.</summary>
    private ImmutableList<ObjectId> TopOf(GameState state, Cards.Top top, EffectContext context)
    {
        var count = top.Count is null ? 1 : Evaluate(state, top.Count, context);
        var ids = ImmutableList.CreateBuilder<ObjectId>();
        foreach (var player in Players(state, top.Whose, context))
            ids.AddRange(state.In(ZoneKey.Of(top.Zone, player)).Take(Math.Max(0, count)));
        return ids.ToImmutable();
    }

    public ImmutableList<PlayerId> Players(GameState state, PlayerRef player, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(player);
        return player switch
        {
            You => [context.Controller], // CR 109.5
            EachPlayer => InGame(state),
            EachOpponent => [.. InGame(state).Where(p => p != context.Controller)], // CR 102.1
            TargetPlayer => context.TargetPlayers, // CR 601.2c
            ActivePlayer => [state.Turn.ActivePlayer], // CR 102.1
            TriggeringPlayer => context.TriggeringPlayer is { } who ? [who] : [], // CR 603.6
            AttachedPlayer => context.Source is { } aura && state.Objects.TryGetValue(aura, out var self) && self.AttachedToPlayer is { } enchanted ? [enchanted] : [], // CR 303.4f

            ControllerOf controller => [.. Resolve(state, controller.Of, context)
                .Select(id => _layers.ControllerOf(state, id)).Distinct()], // CR 613.1a
            OwnerOf owner => [.. Resolve(state, owner.Of, context)
                .Select(id => state.Objects[id].Owner).Distinct()], // CR 108.3

            DefendingPlayer => Defending(state), // CR 506.2
            PlayerWithMostOf most => Extreme(state, most, context),

            // CR 724.2: there is at most one monarch, and the state keeps who it is.
            Cards.Monarch => state.Monarch is { } monarch ? [monarch] : [],
            // CR 303.4f: "enchanted player" — the player this source is attached to — has no
            // record in the vocabulary, so a card that says it cannot be written at all. The state
            // and the view carry the attachment; the arm is one line the day the record lands.
            // seam-requests/E5c.md.
            ChoosePlayer => throw Unanswerable(nameof(ChoosePlayer), NoPlayerMemory),
            ChosenPlayer => throw Unanswerable(nameof(ChosenPlayer), NoPlayerMemory),
            RememberedPlayer => throw Unanswerable(nameof(RememberedPlayer), NoPlayerMemory),
            _ => throw Unanswerable(player.GetType().Name, "no arm in the query layer"),
        };
    }

    /// <summary>
    /// Whether a player answers a <see cref="PlayerFilter"/>, CR 702.5b with 601.2c. The one
    /// reading, for every caller: <c>IsYou</c> and <c>Opponent</c> are read from the context's
    /// controller (CR 109.5 — "you" is whoever controls the ability doing the asking), and
    /// <c>Also</c> is asked <em>as the candidate</em>.
    /// <para>
    /// The rebinding is what makes <c>Also</c> a filter at all. A <see cref="Condition"/> has no
    /// record meaning "the player being filtered"; the only <see cref="PlayerRef"/> that can name
    /// them is <see cref="You"/>, which reads <see cref="EffectContext.Controller"/>. Evaluated in
    /// the ambient context the condition would be a board question unrelated to the candidate, so
    /// "each opponent who controls a creature" would admit an opponent who controls none whenever
    /// the ability's own controller did. <see cref="Extreme"/> already reads a per-player amount
    /// this way, and seam-requests/E5d.md records the two private copies this replaces.
    /// </para>
    /// </summary>
    [RuleRef("702.5b")]
    public bool PlayerMatches(GameState state, PlayerFilter filter, PlayerId candidate, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(filter);

        // CR 109.5: "you" is the controller of the ability, so the candidate is compared to them.
        if (filter.IsYou is { } you && you != (candidate == context.Controller))
            return false;

        // CR 102.1: an opponent is any other player in the game.
        if (filter.Opponent is { } opponent && opponent != (candidate != context.Controller))
            return false;

        return filter.Also is null || Holds(state, filter.Also, context with { Controller = candidate });
    }

    private const string NoPlayerMemory =
        "EffectContext remembers objects by key but not players; see seam-requests/E0.md";

    /// <summary>Players still in the game, CR 800.4a: one who has lost is no longer a player.</summary>
    private static ImmutableList<PlayerId> InGame(GameState state) =>
        [.. state.Players.Where(p => !p.HasLost).Select(p => p.Id)];

    /// <summary>The players being attacked, and the controllers of the permanents being attacked, CR 506.2.</summary>
    private static ImmutableList<PlayerId> Defending(GameState state)
    {
        if (state.Combat is not { } combat)
            return [];

        var defenders = ImmutableList.CreateBuilder<PlayerId>();
        foreach (var attacked in combat.Attackers.Values)
        {
            if (attacked.Player is { } player)
                defenders.Add(player);
            else if (attacked.Permanent is { } permanent && state.Objects.TryGetValue(permanent, out var obj))
                defenders.Add(obj.InitialController);
        }

        return [.. defenders.Distinct()];
    }

    /// <summary>
    /// "the player with the most life", "the player who controls the fewest creatures": the amount
    /// is evaluated once per player, as that player, and the extreme wins. A tie names everyone
    /// tied: the query layer never chooses, so the effect asks which, CR 608.2.
    /// </summary>
    private ImmutableList<PlayerId> Extreme(GameState state, PlayerWithMostOf most, EffectContext context)
    {
        var scored = InGame(state)
            .Select(p => (Player: p, Value: Evaluate(state, most.Of, context with { Controller = p })))
            .ToList();
        if (scored.Count == 0)
            return [];

        var best = most.Fewest ? scored.Min(s => s.Value) : scored.Max(s => s.Value);
        return [.. scored.Where(s => s.Value == best).Select(s => s.Player)];
    }
}
