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
    /// <summary>
    /// The key an unkeyed target selector reads its slot under, CR 601.2c: the n-th
    /// <see cref="Target"/> or <see cref="AnyTarget"/> in reading order is the n-th announced. The
    /// colon keeps it apart from any key a card could write.
    /// </summary>
    [RuleRef("601.2c")]
    public static string TargetSlot(int slot) => "target:" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public ImmutableList<ObjectId> Resolve(GameState state, Selector selector, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(selector);
        return selector switch
        {
            Self => context.Source is { } source && state.Objects.ContainsKey(source) ? [source] : [], // CR 109.5
            Each each => [.. Candidates(state, each.Filter, context)],

            // The targets were chosen when the spell was cast, CR 601.2c, and each selector reads
            // its own slot: a keyed target is what the interpreter remembered under that key, and
            // a selector the card left unkeyed was keyed by its slot (TargetSlot) before the walk
            // began, so it reads exactly the objects announced for it and nothing when its slot is
            // past what was announced. Only a tree no one keyed — an effect run off the stack with
            // the targets handed in — still reads the whole list.
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

            Cards.Commander commander => CommandersOf(state, commander.Whose, context), // CR 903.3
            RandomFrom => throw Unanswerable(nameof(RandomFrom), "a random selection draws from the seeded generator and so belongs to a subsystem that may change state (CR 103.1)"),
            SourceOfDamage => throw Unanswerable(nameof(SourceOfDamage), "the source of damage reaches the query layer through the triggering objects, not through state (CR 608.2)"),
            _ => throw Unanswerable(selector.GetType().Name, "no arm in the query layer"),
        };
    }

    /// <summary>
    /// "your commander", CR 903.3: the designation is an attribute of the card and not of the
    /// object, so it travels with the card through every zone change and this reads it wherever the
    /// card is — the command zone, the battlefield, a graveyard. That is why no zone narrows it:
    /// "return your commander to your hand" and "your commander deals damage" name the same card in
    /// two places, and a filter is the thing that narrows a selection.
    /// <para>
    /// A commander is designated out of its own deck, so the player it belongs to is its owner
    /// (CR 108.3) and never whoever controls it now — an opponent's Mind Control does not make it
    /// their commander. A player with no commander in the game selects nothing, which is an answer:
    /// the designation is made as the game begins, so "no commander" is a fact about this game and
    /// not a gap in the state. The colour identity reading of CR 903.4 in <c>Query.cs</c> refuses
    /// instead, because a filter that admits everything is a wrong answer rather than an empty one.
    /// </para>
    /// </summary>
    [RuleRef("903.3")]
    private ImmutableList<ObjectId> CommandersOf(GameState state, PlayerRef whose, EffectContext context)
    {
        var owners = Players(state, whose, context);
        return
        [
            .. state.Objects.Values
                .Where(o => o.IsCommander && owners.Contains(o.Owner))
                .OrderBy(o => o.Id.Value)
                .Select(o => o.Id),
        ];
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


    /// <summary>The object, or what it was when it left (CR 400.7 last-known information), or null when the game never held it.</summary>
    private static GameObject? LastKnown(GameState state, ObjectId id) =>
        state.Objects.TryGetValue(id, out var here) ? here
        : state.History.LeftBattlefield.Concat(state.History.Died).FirstOrDefault(o => o.Id == id);

    private PlayerId? LastKnownController(GameState state, ObjectId id) =>
        state.Objects.ContainsKey(id) ? _layers.ControllerOf(state, id) : LastKnown(state, id)?.InitialController;

    public ImmutableList<PlayerId> Players(GameState state, PlayerRef player, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(player);
        return player switch
        {
            You => [context.Controller], // CR 109.5
            EachPlayer => InGame(state),
            EachOpponent => [.. InGame(state).Where(p => p != context.Controller)], // CR 102.1

            // CR 102.2: "an opponent" is some opponent, and the only question it can answer is a
            // condition about one. The condition layer binds it to one opponent at a time and asks
            // the condition once per opponent (CR 103.1 gives the seats); this reads that binding.
            // With none, the reference is being used to act on a player it never named, and it is
            // refused rather than answered with the first opponent — which is a different card at
            // four players and the same card in a duel, so the wrong reading survives every
            // two-player test. The linter refuses the shape too (L041); this is the engine's half.
            AnOpponent => BoundOpponent(context) is { } one ? [one] : throw Unanswerable(nameof(AnOpponent), NoAnOpponentToAct, "102.2"),
            TargetPlayer => context.TargetPlayers, // CR 601.2c
            ActivePlayer => [state.Turn.ActivePlayer], // CR 102.1
            TriggeringPlayer => context.TriggeringPlayer is { } who ? [who] : [], // CR 603.6
            AttachedPlayer => context.Source is { } aura && state.Objects.TryGetValue(aura, out var self) && self.AttachedToPlayer is { } enchanted ? [enchanted] : [], // CR 303.4f

            // CR 400.7 with 608.2h: an object that has left the zone it was in is a new object; "its
            // controller" for a remembered one reads the last-known information the turn kept.
            ControllerOf controller => [.. Resolve(state, controller.Of, context)
                .Select(id => LastKnownController(state, id)).OfType<PlayerId>().Distinct()], // CR 613.1a
            OwnerOf owner => [.. Resolve(state, owner.Of, context)
                .Select(id => LastKnown(state, id)?.Owner).OfType<PlayerId>().Distinct()], // CR 108.3

            DefendingPlayer => Defending(state), // CR 506.2
            PlayerWithMostOf most => Extreme(state, most, context),
            PlayerWhoChose chose => WhoChose(state, chose, context), // CR 700.2

            // CR 725.3: there is at most one monarch, and the state keeps who it is.
            Cards.Monarch => state.Monarch is { } monarch ? [monarch] : [],

            // CR 608.2h: an effect determines its information once, so a step that named a player
            // is read back by a later step of the same resolution rather than asked of the board
            // again. The memory is EffectContext.RememberedPlayers, which the interpreter seeds
            // from the stack entry — the trigger system writes the triggering player there
            // (CR 603.6), and a target player's key is written on casting (CR 601.2c).
            // A key that is present but names nobody is an answer, not a gap: "up to one target
            // player" with none chosen remembers an empty list. Only a key no step ever wrote is
            // unanswerable, and it is refused rather than read as "nobody" so the effect is not
            // silently dropped.
            RememberedPlayer remembered => context.RememberedPlayers is { } players
                && players.TryGetValue(remembered.Key, out var those)
                ? those
                : throw Unanswerable(nameof(RememberedPlayer), NoSuchPlayerKey, "608.2h"),

            // The query layer reads; it never chooses (CR 608.2). Asking a player to pick a player
            // is a question, and questions belong to the choice system, which records the answer
            // where RememberedPlayer above can read it.
            ChoosePlayer => throw Unanswerable(nameof(ChoosePlayer), NoPlayerChoice, "601.2b"),
            // CR 607.2d: "the chosen player" is a choice a *different*, linked ability made and the
            // object kept. GameObject.Chosen holds those, and it maps a key to a string — a name, a
            // type, a colour — with nowhere to put a player. seam-requests/E0g.md.
            ChosenPlayer => throw Unanswerable(nameof(ChosenPlayer), NoChosenPlayer, "607.2d"),
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

    /// <summary>
    /// "each player who chose the highest number", CR 700.2 with 101.4: <em>every</em> seat whose
    /// revealed number is that extreme, because two players may choose the same number and the
    /// printed line reads "each player who chose". They are named in turn order, CR 101.4 — the
    /// order the questions were asked in and the order the effect will act on them.
    /// <para>
    /// A key nobody answered is refused rather than read as nobody: an empty list would drop the
    /// rest of the effect silently, which is the same reading <see cref="RememberedPlayer"/> above
    /// refuses for the same reason.
    /// </para>
    /// </summary>
    [RuleRef("700.2")]
    private ImmutableList<PlayerId> WhoChose(GameState state, PlayerWhoChose chose, EffectContext context)
    {
        var numbers = SecretNumbers(chose.Key, context);
        if (numbers.Count == 0)
            throw NoSecretNumbers(nameof(PlayerWhoChose), chose.Key);

        var extreme = chose.Lowest ? numbers.Min(n => n.Number) : numbers.Max(n => n.Number);
        return
        [
            .. numbers.Where(n => n.Number == extreme)
                .Select(n => n.Player)
                .Distinct()
                .OrderBy(p => TurnOrderIndex(state, p)),
        ];
    }

    /// <summary>Where a seat falls in turn order, CR 101.4: the active player first, then the rest as they take turns.</summary>
    [RuleRef("101.4")]
    private static int TurnOrderIndex(GameState state, PlayerId player)
    {
        var seats = state.Players.Count;
        return seats == 0 ? player.Value : ((player.Value - state.Turn.ActivePlayer.Value) % seats + seats) % seats;
    }

    private const string NoSuchPlayerKey =
        "no step of this resolution remembered a player under that key, so reading it as \"nobody\" " +
        "would drop the rest of the effect instead of naming what is missing";

    private const string NoPlayerChoice =
        "choosing a player is a question and the query layer only reads; the choice system asks it " +
        "and records the answer under the key";

    /// <summary>
    /// The key one opponent of an existential condition is bound under while that condition is
    /// asked about them, CR 102.2. It travels in <see cref="EffectContext.RememberedPlayers"/>
    /// because that is the one place the query layer already carries a player by name, and the
    /// colon keeps it apart from every key a card could write (no vocabulary key contains one).
    /// </summary>
    [RuleRef("102.2")]
    private const string AnOpponentBinding = "an-opponent:bound";

    /// <summary>The opponent an existential condition is currently asked about, or none outside one.</summary>
    private static PlayerId? BoundOpponent(EffectContext context) =>
        context.RememberedPlayers is { } remembered
        && remembered.TryGetValue(AnOpponentBinding, out var bound)
        && bound.Count > 0
            ? bound[0]
            : null;

    private const string NoAnOpponentToAct =
        "\"an opponent\" names some opponent rather than one to act on, so it can only be read " +
        "inside a condition; an effect aimed at one wants ChoosePlayer, TargetPlayer or EachOpponent";

    private const string NoChosenPlayer =
        "the values an object keeps from a linked ability are strings, so a chosen player has " +
        "nowhere to live; see seam-requests/E0g.md";

    /// <summary>
    /// Players still in the game, CR 800.4a: one who has lost is no longer a player - IN TURN
    /// ORDER, CR 101.4: "the active player makes any choices required, then each other player
    /// in turn order does the same."
    ///
    /// This used to return seat order. Every caller here feeds players who then choose or act -
    /// each player, each opponent, and the tie an extreme names - so at a table whose active
    /// player is not seat 0, "each player sacrifices a creature" asked seat 0 first and the
    /// active player last, which is the information order CR 101.4 exists to fix.
    /// </summary>
    [RuleRef("101.4")]
    private static ImmutableList<PlayerId> InGame(GameState state) =>
        [.. state.Players.Where(p => !p.HasLost).Select(p => p.Id).OrderBy(p => TurnOrderIndex(state, p))];

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
