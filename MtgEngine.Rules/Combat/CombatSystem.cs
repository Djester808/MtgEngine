using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Combat;

/// <summary>
/// The combat phase's turn-based actions, CR 506–511: who attacks, who blocks, and how much
/// damage that deals. Every characteristic it reasons from — power, toughness, keywords,
/// controller — comes from <see cref="ICharacteristics"/>, so a creature that gained flying
/// this turn evades exactly like one printed with it, and every question the rules ask about
/// an object matching a description goes to <see cref="IQuery"/>. Nothing here knows a card.
/// <para>
/// The step is not checked: which step a declaration belongs to is the turn system's, and
/// coupling the two would make a combat test depend on turn order. Legality here is the
/// declaration's own, CR 508.1 and 509.1.
/// </para>
/// </summary>
[RuleRef("506")]
public sealed partial class CombatSystem(ICharacteristics characteristics, IQuery query) : ICombatSystem
{
    private readonly ICharacteristics _characteristics = characteristics;

    private readonly IQuery _query = query;

    public string Name => "combat";

    /// <summary>
    /// CR 508.1. Each attacker must be an untapped creature its controller has controlled
    /// since the turn began (CR 302.6) without defender (CR 702.3b), attacking a player,
    /// planeswalker or battle an opponent controls. One illegal attacker refuses the whole
    /// declaration, CR 508.1 — the game returns to the moment before it, CR 733.
    /// </summary>
    [RuleRef("508.1")]
    public Outcome DeclareAttackers(GameState state, DeclareAttackers command)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);

        // CR 508.1a — the active player chooses which creatures they control will attack.
        if (command.Player != state.Turn.ActivePlayer)
            throw new RefusedException("Only the active player declares attackers.", "508.1a");
        if (state.Combat is { AttackersDeclared: true })
            throw new RefusedException("Attackers have already been declared this combat.", "508.1");

        var declared = new Dictionary<ObjectId, CombatTarget>();
        foreach (var attack in command.Attacks)
        {
            if (!declared.TryAdd(attack.Attacker, attack.Target))
                throw new RefusedException("A creature can only be declared as an attacker once.", "508.1a");
            CheckAttacker(state, command.Player, attack);
        }

        CheckAttackRequirements(state, command.Player, declared);

        // CR 508.1f then 508.1g: the creatures become attacking, and then they become tapped.
        var events = ImmutableList.Create<GameEvent>(new AttackersDeclared(command.Player, declared.ToImmutableDictionary()));
        foreach (var attack in command.Attacks)
        {
            // CR 702.20b — attacking does not cause creatures with vigilance to tap.
            if (!_characteristics.Of(state, attack.Attacker).Has(KeywordKind.Vigilance) && !state.Objects[attack.Attacker].Tapped)
                events = events.Add(new ObjectChanged(attack.Attacker, Tapped: true));
        }

        return new Outcome(GameReducer.Apply(state, events), events);
    }

    /// <summary>
    /// CR 509.1. Each blocker must be an untapped creature the declaring player controls,
    /// blocking one attacker attacking that player or a permanent they control, and the
    /// declaration must disobey no restriction — the evasion keywords among them, CR 509.1b.
    /// </summary>
    [RuleRef("509.1")]
    public Outcome DeclareBlockers(GameState state, DeclareBlockers command)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);

        var combat = state.Combat;
        if (combat is null || !combat.AttackersDeclared)
            throw new RefusedException("No attackers have been declared.", "509.1");
        if (combat.BlockersDeclared)
            throw new RefusedException("Blockers have already been declared this combat.", "509.1");
        if (command.Player == combat.AttackingPlayer)
            throw new RefusedException("The attacking player does not declare blockers.", "509.1a");

        var byAttacker = new Dictionary<ObjectId, ImmutableList<ObjectId>>();
        var seen = new HashSet<ObjectId>();
        foreach (var block in command.Blocks)
        {
            // CR 509.1a — the defending player chooses one creature for each blocker to block.
            if (!seen.Add(block.Blocker))
                throw new RefusedException("A creature can only be declared as a blocker once.", "509.1a");
            CheckBlocker(state, combat, command.Player, block);
            byAttacker[block.Attacker] = byAttacker.TryGetValue(block.Attacker, out var list) ? list.Add(block.Blocker) : [block.Blocker];
        }

        foreach (var (attacker, blockers) in byAttacker)
            CheckBlockerCount(state, attacker, blockers);

        // CR 509.1g and 509.1h — the chosen creatures become blocking, their attackers blocked.
        var events = ImmutableList.Create<GameEvent>(new BlockersDeclared(command.Player, byAttacker.ToImmutableDictionary()));
        return new Outcome(GameReducer.Apply(state, events), events);
    }

    /// <summary>CR 511. The end of combat step has no turn-based action of its own; combat is over and nothing else changes.</summary>
    [RuleRef("511")]
    public Outcome EndCombat(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Combat is null)
            return Outcome.Unchanged(state);

        var events = ImmutableList.Create<GameEvent>(new CombatEnded());
        return new Outcome(GameReducer.Apply(state, events), events);
    }

    /// <summary>A context with nothing remembered: combat asks the query layer about objects, never about an effect's memory.</summary>
    internal static EffectContext Context(ObjectId? source, PlayerId controller) => new(
        source,
        controller,
        [],
        [],
        null,
        ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
        ImmutableDictionary<string, string>.Empty,
        ImmutableDictionary<string, int>.Empty,
        [],
        null);

    /// <summary>The continuous effects a static ability of this object puts in force, CR 604.</summary>
    internal static IEnumerable<T> Statics<T>(Characteristics of)
        where T : ContinuousEffect =>
        of.Abilities.OfType<StaticAbility>().Select(a => a.Effect).OfType<T>();

    /// <summary>Every battlefield object, in object order, so a declaration refuses the same way twice.</summary>
    internal static IEnumerable<ObjectId> Battlefield(GameState state) =>
        state.Objects.Values.Where(o => o.Zone.Zone == Zone.Battlefield).OrderBy(o => o.Id.Value).Select(o => o.Id);

    internal static bool OnBattlefield(GameState state, ObjectId id) =>
        state.Objects.TryGetValue(id, out var obj) && obj.Zone.Zone == Zone.Battlefield;

    /// <summary>Who is being attacked: the player, or the controller of the planeswalker or battle, CR 506.1.</summary>
    [RuleRef("506.1")]
    private PlayerId? DefendingPlayer(GameState state, CombatTarget target) =>
        target.Player ?? (target.Permanent is { } permanent && OnBattlefield(state, permanent)
            ? _characteristics.Of(state, permanent).Controller
            : null);

    [RuleRef("508.1a")]
    private void CheckAttacker(GameState state, PlayerId player, AttackDeclaration attack)
    {
        if (!OnBattlefield(state, attack.Attacker))
            throw new RefusedException("An attacker must be a permanent on the battlefield.", "508.1a");

        var obj = state.Objects[attack.Attacker];
        var attacker = _characteristics.Of(state, attack.Attacker);
        if (!attacker.Types.HasFlag(CardType.Creature))
            throw new RefusedException("Only a creature can attack.", "508.1a");
        if (attacker.Controller != player)
            throw new RefusedException("A player may only attack with creatures they control.", "508.1a");
        if (obj.Tapped)
            throw new RefusedException("A tapped creature can't attack.", "508.1a");

        // CR 302.6 — summoning sickness. CR 702.10b: haste is the exemption.
        if (obj.SummoningSick && !attacker.Has(KeywordKind.Haste))
            throw new RefusedException("A creature can't attack unless it has been controlled since the turn began, or has haste.", "302.6");

        // CR 702.3b — a creature with defender can't attack.
        if (attacker.Has(KeywordKind.Defender))
            throw new RefusedException("A creature with defender can't attack.", "702.3b");

        CheckAttackRestrictions(state, player, attack, attacker);
        CheckAttackTarget(state, player, attack.Target);
    }

    /// <summary>CR 508.1d — a restriction ("can't attack") disobeyed makes the whole declaration illegal.</summary>
    [RuleRef("508.1d")]
    private void CheckAttackRestrictions(GameState state, PlayerId player, AttackDeclaration attack, Characteristics attacker)
    {
        var context = Context(attack.Attacker, player);
        foreach (var restriction in Statics<CantAttack>(attacker))
        {
            // "can't attack unless ..." — the restriction lifts when the attacker matches.
            if (restriction.Unless is { } unless && _query.Matches(state, unless, attack.Attacker, context))
                continue;
            if (restriction.Player is null)
                throw new RefusedException("An effect says that creature can't attack.", "508.1d");

            var forbidden = _query.Players(state, restriction.Player, context);
            if (DefendingPlayer(state, attack.Target) is { } defender && forbidden.Contains(defender))
                throw new RefusedException("An effect says that creature can't attack that player.", "508.1d");
        }
    }

    /// <summary>CR 508.1c — the attacker's target: a player, or a planeswalker or battle that player's opponents defend.</summary>
    [RuleRef("508.1c")]
    private void CheckAttackTarget(GameState state, PlayerId player, CombatTarget target)
    {
        if (target.Player is { } defender)
        {
            if (defender == player)
                throw new RefusedException("A creature can't attack its controller.", "506.2");
            if (state.Player(defender).HasLost)
                throw new RefusedException("That player is no longer in the game.", "506.2");
            return;
        }

        if (target.Permanent is not { } permanent || !OnBattlefield(state, permanent))
            throw new RefusedException("An attacker must attack a player, a planeswalker or a battle.", "508.1c");

        var attacked = _characteristics.Of(state, permanent);
        if (!attacked.Types.HasFlag(CardType.Planeswalker) && !attacked.Types.HasFlag(CardType.Battle))
            throw new RefusedException("An attacker must attack a player, a planeswalker or a battle.", "508.1c");

        // A battle is attacked by a player who does not protect it; its controller stands in
        // for its protector until the battle rules land, CR 506.1 and 310.
        if (attacked.Controller == player)
            throw new RefusedException("A creature can't attack a permanent its own controller controls.", "506.2");
    }

    /// <summary>
    /// CR 508.1d and 508.1g — a requirement ("attacks each combat if able") is disobeyed when a
    /// creature that could have attacked was left out. Costs are never required to be paid.
    /// </summary>
    [RuleRef("508.1d")]
    private void CheckAttackRequirements(GameState state, PlayerId player, Dictionary<ObjectId, CombatTarget> declared)
    {
        foreach (var id in Battlefield(state))
        {
            if (declared.ContainsKey(id))
                continue;

            var candidate = _characteristics.Of(state, id);
            if (candidate.Controller != player || !candidate.Types.HasFlag(CardType.Creature))
                continue;
            if (!Statics<MustAttack>(candidate).Any())
                continue;
            if (CouldAttack(state, id, candidate))
                throw new RefusedException("A creature that must attack if able was not declared as an attacker.", "508.1d");
        }
    }

    /// <summary>Whether this creature is able to attack at all, ignoring who it would attack, CR 508.1a.</summary>
    [RuleRef("508.1a")]
    private bool CouldAttack(GameState state, ObjectId id, Characteristics candidate)
    {
        var obj = state.Objects[id];
        if (obj.Tapped || (obj.SummoningSick && !candidate.Has(KeywordKind.Haste)) || candidate.Has(KeywordKind.Defender))
            return false;

        var context = Context(id, candidate.Controller);
        return !Statics<CantAttack>(candidate).Any(r =>
            r.Player is null && !(r.Unless is { } unless && _query.Matches(state, unless, id, context)));
    }
}
