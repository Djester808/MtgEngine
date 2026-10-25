using MtgEngine.Cards;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Engine;

public sealed record AttackDeclaration(ObjectId Attacker, PlayerId Defender);
public sealed record BlockDeclaration(ObjectId Blocker, ObjectId Attacker);
public sealed record CombatDamageAssignment(ObjectId Attacker, ImmutableDictionary<ObjectId, int> DamageToBlockers, int DamageToDefender = 0);

/// <summary>Registered creature combat, bounded evergreen keywords and life/failed-draw losses. No teams or control effects.</summary>
public sealed class BasicCombat(BasicActions actions)
{
    public TurnExecution DeclareAttackers(TurnExecution execution, PlayerId actor, IEnumerable<AttackDeclaration> declarations)
    {
        RequireStep(execution, Step.DeclareAttackers);
        if (actor != DecisionPlayer(execution.State))
            throw new InvalidOperationException("Only the active player (or next living seat after departure) declares attackers.");
        var attacks = declarations.ToImmutableList();
        return execution.CompleteTurnAction(state =>
        {
            ValidateWorld(state);
            if (state.Player(state.Turn.ActivePlayer).HasLost && attacks.Count != 0)
                throw new InvalidOperationException("No creatures can attack for a departed active player.");
            if (attacks.Select(a => a.Attacker).Distinct().Count() != attacks.Count)
                throw new InvalidOperationException("An attacker may be declared only once.");
            foreach (var attack in attacks)
            {
                var creature = Creature(state, attack.Attacker, actor);
                if (creature.Tapped || (creature.SummoningSick && !actions.Has(creature, BasicKeyword.Haste))
                    || actions.Has(creature, BasicKeyword.Defender) || attack.Defender == actor
                    || !state.Players.Any(p => p.Id == attack.Defender && !p.HasLost))
                    throw new InvalidOperationException("Illegal attack declaration.");
            }
            var objects = state.Objects;
            foreach (var attack in attacks)
                if (!actions.Has(objects[attack.Attacker], BasicKeyword.Vigilance))
                    objects = objects.SetItem(attack.Attacker, objects[attack.Attacker] with { Tapped = true });
            return state with
            {
                Objects = objects,
                Combat = new(state.Turn.ActivePlayer, attacks.ToImmutableDictionary(a => a.Attacker, a => new CombatTarget(a.Defender, null)),
                    ImmutableDictionary<ObjectId, ImmutableList<ObjectId>>.Empty, true, false)
                {
                    DefendingPlayers = TurnOrder(state).Where(p => p != state.Turn.ActivePlayer).ToImmutableList(),
                },
            };
        });
    }

    /// <summary>Next attacked living defender in APNAP order; null means an empty block declaration remains to be certified.</summary>
    public PlayerId? NextDefender(TurnExecution execution)
    {
        RequireStep(execution, Step.DeclareBlockers);
        return Defenders(execution.State).Cast<PlayerId?>().FirstOrDefault();
    }

    public TurnExecution DeclareBlockers(TurnExecution execution, PlayerId actor, IEnumerable<BlockDeclaration> declarations)
    {
        RequireStep(execution, Step.DeclareBlockers);
        var blocks = declarations.ToImmutableList();
        return execution.CompleteTurnAction(state =>
        {
            ValidateWorld(state);
            var combat = state.Combat!;
            var defenders = Defenders(state);
            var expected = defenders.Count > 0 ? defenders[0] : DecisionPlayer(state);
            if (actor != expected || (defenders.Count == 0 && blocks.Count != 0)
                || blocks.Select(b => b.Blocker).Distinct().Count() != blocks.Count)
                throw new InvalidOperationException("Illegal blocker declaration or declaration order.");
            foreach (var block in blocks)
            {
                var blocker = Creature(state, block.Blocker, actor);
                if (blocker.Tapped || !combat.Attackers.TryGetValue(block.Attacker, out var target) || target.Player != actor
                    || !IsCreature(state, block.Attacker, combat.AttackingPlayer))
                    throw new InvalidOperationException("A blocker must block a current attacker attacking its controller.");
                if (actions.Has(state.Objects[block.Attacker], BasicKeyword.Flying)
                    && !actions.Has(blocker, BasicKeyword.Flying) && !actions.Has(blocker, BasicKeyword.Reach))
                    throw new InvalidOperationException("Flying requires a blocker with flying or reach.");
            }
            if (blocks.GroupBy(b => b.Attacker).Any(g => g.Count() == 1 && actions.Has(state.Objects[g.Key], BasicKeyword.Menace)))
                throw new InvalidOperationException("Menace requires at least two blockers.");
            var assigned = combat.Blockers;
            foreach (var group in blocks.GroupBy(b => b.Attacker))
                assigned = assigned.Add(group.Key, group.Select(b => b.Blocker).ToImmutableList());
            return state with
            {
                Combat = combat with
                {
                    Blockers = assigned,
                    BlockersDeclaredBy = combat.BlockersDeclaredBy.Add(actor),
                    BlockersDeclared = defenders.Count <= 1,
                },
            };
        });
    }

    /// <summary>Supply exact splits for positive-power attackers with multiple current blockers, or trample and any blockers.
    /// Each blocker blocks one attacker. First/double strike participation and simultaneous damage follow CR 510.</summary>
    public TurnExecution DealDamage(TurnExecution execution, PlayerId actor, IEnumerable<CombatDamageAssignment>? assignments = null)
    {
        if (execution.State.Turn.Step is not (Step.FirstStrikeCombatDamage or Step.CombatDamage))
            throw new InvalidOperationException("Combat damage is unavailable.");
        RequireStep(execution, execution.State.Turn.Step);
        if (actor != DecisionPlayer(execution.State))
            throw new InvalidOperationException("The attacking player assigns attacker damage.");
        var choices = (assignments ?? []).ToImmutableList();
        if (choices.Select(c => c.Attacker).Distinct().Count() != choices.Count)
            throw new InvalidOperationException("Duplicate damage assignment.");
        return execution.CompleteTurnAction(state =>
        {
            ValidateWorld(state);
            var combat = state.Combat!;
            if (!combat.BlockersDeclared)
                throw new InvalidOperationException("Complete blocker declarations first.");
            var creatureDamage = new Dictionary<ObjectId, int>();
            var playerDamage = new Dictionary<PlayerId, int>();
            var lifeGain = new Dictionary<PlayerId, int>();
            var deathtouch = new HashSet<ObjectId>();
            var used = new HashSet<ObjectId>();
            bool Deals(ObjectId id) => state.Turn.Step == Step.FirstStrikeCombatDamage
                ? combat.FirstStrikeParticipants.Contains(id)
                : !combat.FirstStrikeParticipants.Contains(id) || actions.Has(state.Objects[id], BasicKeyword.DoubleStrike);
            void Assign(ObjectId source, ObjectId? creature, PlayerId player, int amount)
            {
                if (amount <= 0)
                    return;
                if (creature is { } recipient)
                {
                    Add(creatureDamage, recipient, amount);
                    if (actions.Has(state.Objects[source], BasicKeyword.Deathtouch))
                        deathtouch.Add(recipient);
                }
                else
                    Add(playerDamage, player, amount);
                if (actions.Has(state.Objects[source], BasicKeyword.Lifelink))
                    Add(lifeGain, state.Objects[source].Controller, amount);
            }
            foreach (var (id, target) in combat.Attackers)
            {
                if (!IsCreature(state, id, combat.AttackingPlayer))
                    continue;
                var power = Deals(id) ? Math.Max(0, state.Objects[id].Face.Power!.Number!.Value) : 0;
                var defender = target.Player!.Value;
                if (!combat.Blockers.TryGetValue(id, out var declared))
                {
                    if (!state.Player(defender).HasLost)
                        Assign(id, null, defender, power);
                    continue;
                }
                var blockers = declared.Where(b => IsCreature(state, b, defender)).ToImmutableList();
                // A blocked attacker remains blocked even when every blocker has left (CR 509.1h).
                var trample = actions.Has(state.Objects[id], BasicKeyword.Trample);
                if (blockers.Count == 0 && trample && !state.Player(defender).HasLost)
                    Assign(id, null, defender, power);
                else if (blockers.Count == 1 && !trample)
                    Assign(id, blockers[0], defender, power);
                else if (blockers.Count > 0 && power > 0)
                {
                    var choice = choices.SingleOrDefault(c => c.Attacker == id)
                        ?? throw new InvalidOperationException("Choose the damage split among current blockers.");
                    if (choice.DamageToBlockers.Any(p => !blockers.Contains(p.Key) || p.Value < 0)
                        || choice.DamageToDefender < 0 || (choice.DamageToDefender > 0 && (!trample || state.Player(defender).HasLost))
                        || choice.DamageToBlockers.Values.Sum(v => (long)v) + choice.DamageToDefender != power)
                        throw new InvalidOperationException("Assign exactly the attacker's power to legal recipients.");
                    if (choice.DamageToDefender > 0 && blockers.Any(b =>
                        choice.DamageToBlockers.GetValueOrDefault(b) < Math.Max(0,
                            Math.Min(actions.Has(state.Objects[id], BasicKeyword.Deathtouch) ? 1 : int.MaxValue,
                                state.Objects[b].Face.Toughness!.Number!.Value - state.Objects[b].Damage))))
                        throw new InvalidOperationException("Trample requires lethal assignment to every current blocker.");
                    used.Add(id);
                    foreach (var (blocker, amount) in choice.DamageToBlockers)
                        Assign(id, blocker, defender, amount);
                    Assign(id, null, defender, choice.DamageToDefender);
                }
                foreach (var blocker in blockers)
                    if (Deals(blocker))
                        Assign(blocker, id, combat.AttackingPlayer, Math.Max(0, state.Objects[blocker].Face.Power!.Number!.Value));
            }
            if (used.Count != choices.Count)
                throw new InvalidOperationException("Unexpected or stale damage assignment.");
            var objects = state.Objects;
            foreach (var (id, amount) in creatureDamage)
                objects = objects.SetItem(id, objects[id] with
                {
                    Damage = checked(objects[id].Damage + amount),
                    DealtDeathtouchDamage = objects[id].DealtDeathtouchDamage || deathtouch.Contains(id),
                });
            var players = state.Players;
            foreach (var player in state.Players)
                players = players.SetItem(player.Id.Value, player with
                {
                    Life = checked((int)((long)player.Life - playerDamage.GetValueOrDefault(player.Id) + lifeGain.GetValueOrDefault(player.Id))),
                });
            return state with { Objects = objects, Players = players };
        });
    }

    /// <summary>Closed-subset CR 704 checkpoint: creature deaths, life/empty-draw losses, departure and final outcome.</summary>
    public TurnExecution CompleteCheckpoint(TurnExecution execution)
    {
        if (execution.Boundary != ExecutionBoundary.Checkpoint)
            throw new InvalidOperationException("A checkpoint is required.");
        ValidateWorld(execution.State);
        var activity = DeadCreatures(execution.State).Any() || Losers(execution.State).Any();
        return execution.CompleteCheckpoint(ProcessCheckpoint,
            cleanupActivity: execution.State.Turn.Step == Step.Cleanup && activity);
    }

    private GameState ProcessCheckpoint(GameState state)
    {
        var dead = DeadCreatures(state).OrderBy(o => o.Id.Value).ToImmutableList();
        var losers = Losers(state).Select(p => p.Id).ToImmutableHashSet();
        if (losers.Count > 0 && state.Objects.Values.Any(o => (o.IsPermanent || o.Zone.Zone == Zone.Stack)
            && o.Owner != o.Controller))
            throw new NotSupportedException("Departure with cross-player control needs control-effect history.");
        // Compute the whole SBA batch from the same snapshot, before any movement or departure.
        foreach (var obj in dead)
            state = ZoneTransitions.Move(state, new(obj.Id, ZoneKey.Of(Zone.Graveyard, obj.Owner))).State;
        // Deathtouch looks only since the previous SBA check, even when indestructible prevented destruction.
        state = state with
        {
            Objects = state.Objects.ToImmutableDictionary(p => p.Key,
            p => p.Value.DealtDeathtouchDamage ? p.Value with { DealtDeathtouchDamage = false } : p.Value)
        };
        if (losers.Count > 0)
        {
            var removed = state.Objects.Values.Where(o => losers.Contains(o.Owner)).Select(o => o.Id).ToImmutableHashSet();
            state = state with
            {
                Players = state.Players.Select(p => losers.Contains(p.Id) ? p with { HasLost = true, Mana = ManaPool.Empty } : p).ToImmutableList(),
                Objects = state.Objects.RemoveRange(removed),
                Zones = state.Zones.ToImmutableDictionary(p => p.Key, p => p.Value.RemoveAll(removed.Contains)),
                ExileLookPermissions = state.ExileLookPermissions.RemoveRange(removed),
                LastReveal = null,
                LastLooks = state.LastLooks.Clear(),
                LibraryInspections = state.LibraryInspections.Clear(),
            };
        }
        var living = state.Players.Where(p => !p.HasLost).Select(p => p.Id).ToImmutableList();
        if (living.Count <= 1)
            state = state with { Outcome = new(living, living.IsEmpty, living.IsEmpty ? "All remaining players lost simultaneously." : "All opponents have left the game.") };
        if (state.Turn.Step == Step.DeclareBlockers && state.Combat is { } combat)
        {
            var participants = combat.Attackers.Keys.Where(id => IsCreature(state, id, combat.AttackingPlayer))
                .Concat(combat.Blockers.Where(p => combat.Attackers.ContainsKey(p.Key)).SelectMany(p => p.Value)
                    .Where(id => state.Objects.TryGetValue(id, out var obj) && IsCreature(state, id, obj.Controller)))
                .Where(id => actions.Has(state.Objects[id], BasicKeyword.FirstStrike) || actions.Has(state.Objects[id], BasicKeyword.DoubleStrike))
                .ToImmutableHashSet();
            state = state with { Combat = combat with { FirstStrikeParticipants = participants } };
        }
        return state;
    }

    private void ValidateWorld(GameState state)
    {
        actions.ValidateWorld(state);
        if (state.Objects.Values.Any(o => o.IsCommander))
            throw new NotSupportedException("Commander designation, combat-damage losses and command-zone choices require a separate rules subset.");
    }

    private IEnumerable<GameObject> DeadCreatures(GameState state) => state.Objects.Values.Where(o => o.IsPermanent
        && !o.PhasedOut && o.Face.Types.Types.HasFlag(CardType.Creature)
        && (o.Face.Toughness!.Number <= 0 || (!actions.Has(o, BasicKeyword.Indestructible)
            && (o.DealtDeathtouchDamage || (o.Damage > 0 && o.Damage >= o.Face.Toughness.Number)))));

    private static IEnumerable<PlayerState> Losers(GameState state) => state.Players.Where(p => !p.HasLost && (p.Life <= 0 || p.DrewFromEmptyLibrary));

    private static ImmutableList<PlayerId> Defenders(GameState state) => TurnOrder(state).Where(p =>
        !state.Combat!.BlockersDeclaredBy.Contains(p) && state.Combat.Attackers.Any(a => a.Value.Player == p
            && IsCreature(state, a.Key, state.Combat.AttackingPlayer))).ToImmutableList();

    private static IEnumerable<PlayerId> TurnOrder(GameState state) => Enumerable.Range(1, state.Players.Count)
        .Select(i => new PlayerId((state.Turn.ActivePlayer.Value + i) % state.Players.Count)).Where(p => !state.Player(p).HasLost);

    private static PlayerId DecisionPlayer(GameState state) => state.Player(state.Turn.ActivePlayer).HasLost
        ? TurnExecution.Next(state, state.Turn.ActivePlayer) : state.Turn.ActivePlayer;

    private static bool IsCreature(GameState state, ObjectId id, PlayerId controller) => !state.Player(controller).HasLost
        && !(state.Combat?.RemovedFromCombat.Contains(id) ?? false)
        && state.Objects.TryGetValue(id, out var obj) && obj.IsPermanent && !obj.PhasedOut && obj.Controller == controller
        && obj.Face.Types.Types.HasFlag(CardType.Creature);

    private static GameObject Creature(GameState state, ObjectId id, PlayerId controller) => IsCreature(state, id, controller)
        ? state.Objects[id] : throw new InvalidOperationException("Expected a current creature controlled by the declaring player.");

    private static void RequireStep(TurnExecution execution, Step step)
    {
        if (execution.Boundary != ExecutionBoundary.TurnAction || execution.State.Turn.Step != step)
            throw new InvalidOperationException("Combat declaration is unavailable at this boundary.");
    }

    private static void Add<T>(Dictionary<T, int> damage, T key, int amount) where T : notnull =>
        damage[key] = checked(damage.GetValueOrDefault(key) + amount);
}
