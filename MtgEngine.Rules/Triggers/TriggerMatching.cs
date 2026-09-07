using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using Ev = MtgEngine.Rules.Events;

namespace MtgEngine.Rules.Triggers;

/// <summary>
/// One event in the batch being collected, with the batch around it. A trigger matches against
/// the state as it was when its event happened (CR 603.10 for the ones that look back), and a few
/// triggers ask a question the single event cannot answer on its own — "the first card you draw
/// each turn", "the third counter" — which the rest of the batch answers by subtraction: the
/// state is after every event, so what came later is taken back off.
/// </summary>
[RuleRef("603.6")]
internal sealed record TriggerWindow(GameState State, IReadOnlyList<Ev.GameEvent> Since, int Index)
{
    /// <summary>Draws this player has had this turn as of this event, CR 121.1.</summary>
    public int DrawsSoFar(PlayerId player)
    {
        var later = Since.Skip(Index + 1).OfType<Ev.ObjectDrawn>().Count(d => d.Player == player);
        return State.Player(player).ObjectsDrawnThisTurn - later;
    }

    /// <summary>How many counters of this kind were on the object just after this event.</summary>
    public int CountersAfter(ObjectId id, string kind)
    {
        var later = Since.Skip(Index + 1).OfType<Ev.CountersChanged>()
            .Where(c => c.Id == id && string.Equals(c.Kind, kind, StringComparison.Ordinal))
            .Sum(c => c.Delta);
        var now = State.Objects.TryGetValue(id, out var obj) ? obj.Counters.GetValueOrDefault(kind) : 0;
        return now - later;
    }

    /// <summary>The step this event happened in: the last step the batch began before it, else the state's.</summary>
    public Step Step => Since.Take(Index + 1).OfType<Ev.StepBegan>().LastOrDefault()?.Step ?? State.Turn.Step;
}

/// <summary>
/// Does this event trigger this ability, CR 603.2 — and if so, what was it about? An event can
/// trigger one ability more than once (each attacking creature, each blocker), so the answer is a
/// list of firings and not a bool. Every filter is answered by the query layer in a context whose
/// source and controller are the ability's, so "another creature you control" means what it says.
/// </summary>
[RuleRef("603.2")]
internal sealed class TriggerMatching(IQuery query, ICharacteristics layers)
{
    private readonly IQuery _query = query;
    private readonly ICharacteristics _layers = layers;

    public ImmutableList<TriggerSubject> Match(Trigger trigger, Ev.GameEvent happened, EffectContext ctx, TriggerWindow window)
    {
        var state = window.State;
        return trigger switch
        {
            AnyOfTriggers any => any.Of.Select(t => Match(t, happened, ctx, window)).FirstOrDefault(m => m.Count > 0) ?? [],
            EntersBattlefield enters => Entered(state, enters, happened, ctx),
            LeavesBattlefield leaves => Left(state, leaves.What, happened, ctx, diedOnly: false),
            Dies dies => Left(state, dies.What, happened, ctx, diedOnly: true),
            ZoneChange zone => Moved(state, zone, happened, ctx),
            Cards.SpellCast cast => Cast(state, cast, happened, ctx),
            AtStep step => AtStepBegan(state, step, happened, ctx),
            Attacks attacks => Attacked(state, attacks, happened, ctx),
            AttacksAlone alone => AttackedAlone(state, alone, happened, ctx),
            Blocks blocks => Blocked(state, blocks, happened, ctx),
            BecomesBlocked blocked => BecameBlocked(state, blocked, happened, ctx),
            DealsDamage deals => Damage(state, happened, ctx, deals.Combat, deals.Source, deals.ToObject, deals.ToPlayer, subject: DamageSubject.Source),
            IsDealtDamage dealt => Damage(state, happened, ctx, dealt.Combat, dealt.By, dealt.To, null, subject: DamageSubject.Object),
            PlayerIsDealtDamage hit => PlayerDamage(state, hit, happened, ctx),
            BecomesTapped tapped => TapChanged(state, tapped.What, happened, ctx, toTapped: true),
            BecomesUntapped untapped => TapChanged(state, untapped.What, happened, ctx, toTapped: false),
            Draws draws => Drew(state, draws, happened, ctx, window),
            GainsLife gains => Life(state, gains.Who, happened, ctx, up: true),
            LosesLife loses => Life(state, loses.Who, happened, ctx, up: false),
            CounterPlaced placed => Counter(state, placed.On, placed.Counter, placed.Nth, happened, ctx, window, added: true),
            CounterRemoved removed => Counter(state, removed.From, removed.Counter, null, happened, ctx, window, added: false),
            Cards.LandPlayed land => Played(state, land, happened, ctx),
            TokenCreated token => Token(state, token, happened, ctx),
            Sacrifices sacrifices => LeftBy(state, "sacrificed", sacrifices.Who, sacrifices.What, happened, ctx),
            Discards discards => LeftBy(state, "discarded", discards.Who, discards.What, happened, ctx),
            _ => [],
        };
    }

    private enum DamageSubject
    {
        Source,
        Object,
    }

    /// <summary>CR 603.6a: an enters trigger looks at the object as it exists on the battlefield now.</summary>
    [RuleRef("603.6a")]
    private ImmutableList<TriggerSubject> Entered(GameState state, EntersBattlefield trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        var entered = happened switch
        {
            Ev.ObjectMoved moved when moved.To.Zone.Zone == Zone.Battlefield => moved.To.Id,
            Ev.ObjectCreated created when created.Object.Zone.Zone == Zone.Battlefield => created.Object.Id,
            _ => (ObjectId?)null,
        };
        if (entered is not { } id || !_query.Matches(state, trigger.What, id, ctx))
            return [];
        if (trigger.UnderControlOf is { } who && !_query.Players(state, who, ctx).Contains(Controller(state, id)))
            return [];
        return [TriggerSubject.Of(id, Controller(state, id))];
    }

    /// <summary>CR 603.10: the state is the one before the object left, which the caller rebuilt.</summary>
    [RuleRef("603.10")]
    private ImmutableList<TriggerSubject> Left(GameState state, ObjectFilter what, Ev.GameEvent happened, EffectContext ctx, bool diedOnly)
    {
        if (happened is not Ev.ObjectMoved moved)
            return [];
        if (!state.History.LeftBattlefield.Contains(moved.From))
            return [];
        if (diedOnly && !state.History.Died.Contains(moved.From))
            return [];
        return _query.Matches(state, what, moved.From, ctx) ? [TriggerSubject.Of(moved.From, Controller(state, moved.From))] : [];
    }

    /// <summary>Any other zone change, CR 603.6. The destination is on the event; a named source zone is only knowable for the battlefield today.</summary>
    private ImmutableList<TriggerSubject> Moved(GameState state, ZoneChange trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not Ev.ObjectMoved moved || moved.To.Zone.Zone != trigger.To)
            return [];
        var left = state.History.LeftBattlefield.Contains(moved.From);
        if (trigger.From is { } from && (from != Zone.Battlefield || !left))
            return [];

        // The object that arrived is the one to describe when it is still there; when the trigger
        // looks back past the battlefield the caller restored the object that left, CR 603.10.
        var subject = left ? moved.From : moved.To.Id;
        return _query.Matches(state, trigger.What, subject, ctx) ? [TriggerSubject.Of(subject, Controller(state, subject))] : [];
    }

    [RuleRef("603.2")]
    private ImmutableList<TriggerSubject> Cast(GameState state, Cards.SpellCast trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not Ev.SpellCast cast)
            return [];
        if (!_query.Players(state, trigger.By, ctx).Contains(cast.Caster))
            return [];
        return _query.Matches(state, trigger.Spell, cast.Spell, ctx) ? [TriggerSubject.Of(cast.Spell, cast.Caster)] : [];
    }

    /// <summary>"At the beginning of your upkeep", CR 603.2: the step began, and whose turn it is decides.</summary>
    [RuleRef("603.2")]
    private ImmutableList<TriggerSubject> AtStepBegan(GameState state, AtStep trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not Ev.StepBegan began || began.Step != trigger.Step)
            return [];
        var active = state.Turn.ActivePlayer;
        return _query.Players(state, trigger.Whose, ctx).Contains(active) ? [TriggerSubject.OfPlayer(active)] : [];
    }

    /// <summary>CR 508.2: attack triggers trigger once for each attacking creature that matches.</summary>
    [RuleRef("508.2")]
    private ImmutableList<TriggerSubject> Attacked(GameState state, Attacks trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not Ev.AttackersDeclared declared)
            return [];
        var subjects = ImmutableList.CreateBuilder<TriggerSubject>();
        foreach (var (attacker, target) in Ordered(declared.Attackers))
        {
            if (!_query.Matches(state, trigger.Who, attacker, ctx))
                continue;
            if (trigger.Defending is { } defending && (target.Player is not { } p || !_query.Players(state, defending, ctx).Contains(p)))
                continue;
            if (trigger.AttackedPermanent is { } permanent && (target.Permanent is not { } id || !_query.Matches(state, permanent, id, ctx)))
                continue;
            subjects.Add(TriggerSubject.Of(attacker, declared.Player));
        }

        return subjects.ToImmutable();
    }

    private ImmutableList<TriggerSubject> AttackedAlone(GameState state, AttacksAlone trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not Ev.AttackersDeclared declared || declared.Attackers.Count != 1)
            return [];
        var attacker = declared.Attackers.Keys.Single();
        return _query.Matches(state, trigger.Who, attacker, ctx) ? [TriggerSubject.Of(attacker, declared.Player)] : [];
    }

    [RuleRef("509.2")]
    private ImmutableList<TriggerSubject> Blocked(GameState state, Blocks trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not Ev.BlockersDeclared declared)
            return [];
        var subjects = ImmutableList.CreateBuilder<TriggerSubject>();
        foreach (var (attacker, blockers) in declared.Blockers.OrderBy(b => b.Key.Value))
        {
            if (trigger.Blocked is { } blockedBy && !_query.Matches(state, blockedBy, attacker, ctx))
                continue;
            foreach (var blocker in blockers.OrderBy(b => b.Value))
            {
                if (_query.Matches(state, trigger.Blocker, blocker, ctx))
                    subjects.Add(new TriggerSubject([blocker, attacker], declared.Player));
            }
        }

        return subjects.ToImmutable();
    }

    [RuleRef("509.1h")]
    private ImmutableList<TriggerSubject> BecameBlocked(GameState state, BecomesBlocked trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not Ev.BlockersDeclared declared)
            return [];
        var subjects = ImmutableList.CreateBuilder<TriggerSubject>();
        foreach (var (attacker, blockers) in declared.Blockers.OrderBy(b => b.Key.Value))
        {
            if (blockers.Count == 0 || !_query.Matches(state, trigger.Attacker, attacker, ctx))
                continue;
            if (trigger.By is { } by && !blockers.Any(b => _query.Matches(state, by, b, ctx)))
                continue;
            subjects.Add(TriggerSubject.Of(attacker, declared.Player));
        }

        return subjects.ToImmutable();
    }

    [RuleRef("120")]
    private ImmutableList<TriggerSubject> Damage(
        GameState state,
        Ev.GameEvent happened,
        EffectContext ctx,
        bool? combat,
        ObjectFilter? source,
        ObjectFilter? toObject,
        PlayerFilter? toPlayer,
        DamageSubject subject)
    {
        if (happened is not Ev.DamageDealt damage || damage.Amount <= 0)
            return [];
        if (combat is { } isCombat && isCombat != damage.Combat)
            return [];
        if (source is { } from && !_query.Matches(state, from, damage.Source, ctx))
            return [];
        if (toObject is { } to && (damage.ToObject is not { } hit || !_query.Matches(state, to, hit, ctx)))
            return [];
        if (toPlayer is { } who && (damage.ToPlayer is not { } player || !PlayerMatches(state, who, player, ctx)))
            return [];
        if (subject == DamageSubject.Object && damage.ToObject is null)
            return [];

        var id = subject == DamageSubject.Source ? damage.Source : damage.ToObject!.Value;
        return damage.ToPlayer is { } hurt ? [TriggerSubject.Of(id, hurt)] : [TriggerSubject.Of(id)];
    }

    [RuleRef("120")]
    private ImmutableList<TriggerSubject> PlayerDamage(GameState state, PlayerIsDealtDamage trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not Ev.DamageDealt damage || damage.Amount <= 0 || damage.ToPlayer is not { } player)
            return [];
        if (trigger.Combat is { } isCombat && isCombat != damage.Combat)
            return [];
        if (!_query.Players(state, trigger.Who, ctx).Contains(player))
            return [];
        if (trigger.By is { } by && !_query.Matches(state, by, damage.Source, ctx))
            return [];
        return [TriggerSubject.Of(damage.Source, player)];
    }

    [RuleRef("701.21")]
    private ImmutableList<TriggerSubject> TapChanged(GameState state, ObjectFilter what, Ev.GameEvent happened, EffectContext ctx, bool toTapped)
    {
        if (happened is not Ev.ObjectChanged changed || changed.Tapped != toTapped)
            return [];
        return _query.Matches(state, what, changed.Id, ctx) ? [TriggerSubject.Of(changed.Id, Controller(state, changed.Id))] : [];
    }

    [RuleRef("121.1")]
    private ImmutableList<TriggerSubject> Drew(GameState state, Draws trigger, Ev.GameEvent happened, EffectContext ctx, TriggerWindow window)
    {
        if (happened is not Ev.ObjectDrawn drawn)
            return [];
        if (!_query.Players(state, trigger.Who, ctx).Contains(drawn.Player))
            return [];
        if (trigger.FirstEachTurn && window.DrawsSoFar(drawn.Player) != 1)
            return [];
        if (trigger.DuringDrawStep is { } during && during != (window.Step == Step.Draw))
            return [];
        return [TriggerSubject.Of(drawn.Object, drawn.Player)];
    }

    [RuleRef("119")]
    private ImmutableList<TriggerSubject> Life(GameState state, PlayerRef who, Ev.GameEvent happened, EffectContext ctx, bool up)
    {
        if (happened is not Ev.LifeChanged changed || changed.Delta == 0 || up != changed.Delta > 0)
            return [];
        return _query.Players(state, who, ctx).Contains(changed.Player) ? [TriggerSubject.OfPlayer(changed.Player)] : [];
    }

    [RuleRef("122.7")]
    private ImmutableList<TriggerSubject> Counter(
        GameState state,
        ObjectFilter on,
        string? kind,
        int? nth,
        Ev.GameEvent happened,
        EffectContext ctx,
        TriggerWindow window,
        bool added)
    {
        if (happened is not Ev.CountersChanged changed || changed.Delta == 0 || added != changed.Delta > 0)
            return [];
        if (kind is { } named && !string.Equals(named, changed.Kind, StringComparison.Ordinal))
            return [];
        if (nth is { } target)
        {
            // "the third counter": the count crossed it with this event, CR 122.7.
            var after = window.CountersAfter(changed.Id, changed.Kind);
            if (after < target || after - changed.Delta >= target)
                return [];
        }

        return _query.Matches(state, on, changed.Id, ctx) ? [TriggerSubject.Of(changed.Id, Controller(state, changed.Id))] : [];
    }

    [RuleRef("305")]
    private ImmutableList<TriggerSubject> Played(GameState state, Cards.LandPlayed trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not Ev.LandPlayed played)
            return [];
        return _query.Players(state, trigger.By, ctx).Contains(played.Player) ? [TriggerSubject.Of(played.Land, played.Player)] : [];
    }

    [RuleRef("111.1")]
    private ImmutableList<TriggerSubject> Token(GameState state, TokenCreated trigger, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not Ev.ObjectCreated created || !created.Object.IsToken)
            return [];
        var id = created.Object.Id;
        if (!_query.Players(state, trigger.By, ctx).Contains(created.Object.InitialController))
            return [];
        if (trigger.What is { } what && !_query.Matches(state, what, id, ctx))
            return [];
        return [TriggerSubject.Of(id, created.Object.InitialController)];
    }

    /// <summary>Sacrificed and discarded are named on the event that says why the object left, CR 701.17 and 701.9.</summary>
    [RuleRef("701.17")]
    private ImmutableList<TriggerSubject> LeftBy(GameState state, string verb, PlayerRef who, ObjectFilter? what, Ev.GameEvent happened, EffectContext ctx)
    {
        if (happened is not Ev.ObjectLeftBy left || !string.Equals(left.Verb, verb, StringComparison.OrdinalIgnoreCase))
            return [];
        var owner = state.Objects.TryGetValue(left.Object, out var obj) ? obj.Owner : (PlayerId?)null;
        var by = state.History.LeftBattlefield.Contains(left.Object) ? Controller(state, left.Object) : owner;
        if (by is not { } player || !_query.Players(state, who, ctx).Contains(player))
            return [];
        if (what is { } filter && !_query.Matches(state, filter, left.Object, ctx))
            return [];
        return [TriggerSubject.Of(left.Object, player)];
    }

    private bool PlayerMatches(GameState state, PlayerFilter filter, PlayerId player, EffectContext ctx)
    {
        if (filter.IsYou is { } you && you != (player == ctx.Controller))
            return false;
        if (filter.Opponent is { } opponent && opponent != (player != ctx.Controller))
            return false;
        return filter.Also is null || _query.Holds(state, filter.Also, ctx);
    }

    /// <summary>Control is a computed characteristic, CR 613.1b; an object no longer in the state has none, so its owner stands in.</summary>
    [RuleRef("613.1b")]
    private PlayerId Controller(GameState state, ObjectId id) =>
        state.Objects.ContainsKey(id) ? _layers.ControllerOf(state, id) : state.Turn.ActivePlayer;

    private static IEnumerable<KeyValuePair<ObjectId, CombatTarget>> Ordered(ImmutableDictionary<ObjectId, CombatTarget> attackers) =>
        attackers.OrderBy(a => a.Key.Value);
}
