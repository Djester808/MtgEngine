using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Library;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Replacement;

/// <summary>
/// Replacement and prevention, CR 614 and CR 615. An event is proposed rather than applied:
/// every effect in force that watches for it is offered the event, one of them modifies it,
/// and the result is offered again to the rest. No effect ever sees the same event twice
/// (CR 616.1), self-replacement goes first (CR 614.15), and when more than one still applies
/// the affected player is asked which (CR 616.1) and nothing else happens until they answer.
/// </summary>
[RuleRef("614")]
public sealed partial class ReplacementSystem(IQuery query, ICharacteristics characteristics, IInterpreter interpreter) : IReplacementSystem
{
    /// <summary>No effect applies twice, so the loop ends on its own; this only bounds a bug.</summary>
    private const int MaxRounds = 64;

    /// <summary>Where the already-applied effects are kept while the game waits for an answer.</summary>
    private const string UsedKey = "used";

    private readonly IQuery _query = query;
    private readonly ICharacteristics _layers = characteristics;
    private readonly IInterpreter _interpreter = interpreter;

    public string Name => "replacement and prevention";

    /// <summary>
    /// CR 616.1: apply one applicable effect, then look again. The returned outcome holds the
    /// events that actually happen, folded into the state; an event replaced away is simply
    /// absent from it.
    /// </summary>
    [RuleRef("616.1")]
    public Outcome Replace(GameState state, GameEvent proposed, PlayerId affected)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(proposed);

        return Continue(state, proposed, affected, new HashSet<string>(StringComparer.Ordinal), []);
    }

    /// <summary>
    /// CR 616.1 with 614.5: the loop seeded with the effects that already applied to this event
    /// before an interpreter halt, so none of them is offered it again on the resumed walk.
    /// </summary>
    [RuleRef("616.1")]
    public Outcome Replace(GameState state, GameEvent proposed, PlayerId affected, IEnumerable<string>? used)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(proposed);

        return Continue(state, proposed, affected, new HashSet<string>(used ?? [], StringComparer.Ordinal), []);
    }

    /// <summary>
    /// The loop of CR 616.1, from wherever it currently stands, so that an answered
    /// replacement-order question resumes it rather than starting it again.
    /// <paramref name="used"/> is every effect that has already applied to this event and may
    /// therefore never apply to it again.
    /// </summary>
    [RuleRef("616.1")]
    private Outcome Continue(GameState state, GameEvent? current, PlayerId affected, HashSet<string> used, ImmutableList<GameEvent> extra)
    {
        var working = state;

        for (var round = 0; round < MaxRounds && current is not null; round++)
        {
            // CR 101.2 with 120.3 and 119.3: what the affected player's own limits forbid never
            // happens, so it is dropped before any card-driven candidate is gathered and no
            // effect — not even a self-replacement of CR 614.15 — is offered it.
            if (Forbidden(working, current))
                return new Outcome(working, extra);

            var candidates = Applicable(working, current, used);
            if (candidates.Count == 0)
                break;

            // CR 614.15: a self-replacement effect is applied before any other that would apply.
            var selfFirst = candidates.FindAll(c => c.SelfReplacement);
            var tier = selfFirst.Count > 0 ? selfFirst : candidates;

            if (tier.Count > 1)
            {
                // CR 616.1: the affected player chooses one to apply; the rest wait for the answer.
                var ask = Ask(working, current, affected, tier, used);
                return new Outcome(ask.Apply(working), extra.Add(ask));
            }

            used.Add(tier[0].Key);
            var applied = Apply(working, current, tier[0], used);
            working = applied.State;
            extra = extra.AddRange(applied.Extra);
            current = applied.Event;
        }

        if (current is null)
            return new Outcome(working, extra);
        var completed = current is ZoneReordered { AwaitingRandomization: true } shuffle
            ? LibraryShuffleCoordinator.Complete(working, shuffle)
            : LibraryDrawCoordinator.Complete(working, current);
        return new Outcome(working, extra).Then(_ => completed);
    }

    /// <summary>
    /// One effect that could apply to this event: an ability in force, or a prevention shield,
    /// which is carried as the whole <see cref="ActiveEffect"/> because spending it edits that
    /// effect (CR 615.7).
    /// </summary>
    private sealed record Candidate(
        string Key,
        string Label,
        ObjectId? Source,
        PlayerId Controller,
        bool SelfReplacement,
        ReplacementAbility? Ability,
        ActiveEffect? Active);

    /// <summary>
    /// What applying one effect produced: the event as it now stands, or none at all.
    /// <paramref name="Prevented"/> is what a prevention in this application took off the event
    /// (CR 615), which the rest of the same effect is then run with; none means no prevention ran.
    /// </summary>
    private sealed record Applied(GameEvent? Event, GameState State, ImmutableList<GameEvent> Extra, int? Prevented = null);

    /// <summary>
    /// Every effect that would apply to this event and has not applied to it already. Abilities
    /// come from characteristics on the battlefield (CR 613, so a granted replacement counts) and
    /// from the printed face elsewhere; shields come from the active effects a prevention effect
    /// left behind (CR 615.1).
    /// </summary>
    [RuleRef("614.1")]
    private List<Candidate> Applicable(GameState state, GameEvent proposed, HashSet<string> used)
    {
        var probe = Probe(state, proposed);
        var found = new List<Candidate>();
        if (CommanderCandidate(state, proposed, used) is { } commander)
            found.Add(commander);

        foreach (var obj in probe.Objects.Values)
        {
            var abilities = AbilitiesOf(probe, obj);
            for (var i = 0; i < abilities.Count; i++)
            {
                if (abilities[i] is not ReplacementAbility ability || ability.Where != obj.Zone.Zone)
                    continue;

                var key = $"o{obj.Id.Value}:a{i}";
                if (used.Contains(key))
                    continue;

                // CR 614.15: an effect that names only its own source replaces nothing else.
                var about = IsAbout(proposed, obj.Id);
                if (ability.SelfOnly && !about)
                    continue;

                var controller = ControllerOf(probe, obj);
                var context = Context(obj.Id, controller, proposed);
                if (!Matches(probe, ability.Instead, proposed, context))
                    continue;
                if (ability.If is { } condition && !_query.Holds(probe, condition, context))
                    continue;

                found.Add(new Candidate(key, Label(state, obj, ability, i, controller), obj.Id, controller, ability.SelfOnly && about, ability, null));
            }
        }

        if (proposed is DamageDealt damage)
        {
            foreach (var active in state.ActiveEffects)
            {
                if (active.Effect is not PreventDamage shield)
                    continue;
                var key = $"e{active.Id}";
                if (used.Contains(key) || !Shields(probe, active, shield, damage))
                    continue;
                found.Add(new Candidate(key, "prevention shield", active.Source, active.Controller, SelfReplacement: false, null, active));
            }
        }

        return found;
    }

    /// <summary>
    /// CR 614.12: an effect that watches an object entering reads that object as it would exist
    /// on the battlefield, so matching runs against a state where it already does. The old object
    /// is left where it is, so an effect watching something leave still sees it.
    /// </summary>
    [RuleRef("614.12")]
    private static GameState Probe(GameState state, GameEvent proposed) =>
        proposed is ObjectMoved moved && !state.Objects.ContainsKey(moved.To.Id)
            ? new ObjectCreated(moved.To, moved.Position).Apply(state)
            : state;

    private ImmutableList<Ability> AbilitiesOf(GameState state, GameObject obj) =>
        obj.Zone.Zone == Zone.Battlefield ? _layers.Of(state, obj.Id).Abilities : [.. obj.Face.AbilityList];

    private PlayerId ControllerOf(GameState state, GameObject obj) =>
        obj.Zone.Zone == Zone.Battlefield ? _layers.ControllerOf(state, obj.Id) : obj.InitialController;

    /// <summary>
    /// CR 616.1: the affected player, or the affected object's controller, chooses. The event as
    /// it now stands travels with the question, because resuming means applying the chosen effect
    /// to <em>that</em> event; so does the set of effects that have already applied, which may
    /// not apply a second time once the answer comes back.
    /// </summary>
    [RuleRef("616.1")]
    private static ChoiceRequested Ask(GameState state, GameEvent current, PlayerId affected, List<Candidate> tier, HashSet<string> used)
    {
        var context = new ChoiceContext(
            null,
            null,
            "replacement",
            ImmutableDictionary<string, string>.Empty
                .Add("event", current.GetType().Name)
                .Add("candidates", string.Join(",", tier.Select(c => c.Key)))
                .Add(UsedKey, string.Join(",", used)),
            current);

        return new ChoiceRequested(new PendingChoice(
            new ChoiceId(state.NextChoiceId),
            affected,
            ChoiceKinds.ReplacementOrder,
            "Choose which replacement or prevention effect to apply first",
            [.. tier.Select(c => new ChoiceOption(c.Key, c.Label, c.Source))],
            1,
            1,
            context));
    }

    private static EffectContext Context(ObjectId? source, PlayerId controller, GameEvent proposed, int? prevented = null) =>
        new(
            source,
            controller,
            [],
            [],
            ChosenX(proposed),
            ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
            Prevented(prevented),
            ImmutableDictionary<string, int>.Empty,
            Triggering(proposed),
            AffectedPlayer(proposed));

    /// <summary>Whether the event is about this object, which is what CR 614.15 turns on.</summary>
    private static bool IsAbout(GameEvent proposed, ObjectId id) => proposed switch
    {
        ObjectMoved m => m.From == id || m.To.Id == id,
        ObjectCreated c => c.Object.Id == id,
        DamageDealt d => d.ToObject == id || d.Source == id,
        CountersChanged c => c.Id == id,
        ObjectChanged o => o.Id == id,
        ObjectDrawn draw => draw.Object == id,
        ObjectLeftBy left => left.Object == id,
        _ => false,
    };

    /// <summary>The object the event was about, bound as the replacement's triggering object.</summary>
    private static ImmutableList<ObjectId> Triggering(GameEvent proposed) => proposed switch
    {
        // Arriving, the new object is the one the effect talks about; leaving, the old one still is.
        ObjectMoved m => ImmutableList.Create(m.To.Zone.Zone == Zone.Battlefield ? m.To.Id : m.From),
        ObjectCreated c => [c.Object.Id],
        DamageDealt { ToObject: { } hit } => [hit],
        CountersChanged c => [c.Id],
        ObjectChanged o => [o.Id],
        ObjectDrawn draw => [draw.Object],
        ObjectLeftBy left => [left.Object],
        _ => [],
    };

    private static PlayerId? AffectedPlayer(GameEvent proposed) => proposed switch
    {
        DamageDealt d => d.ToPlayer,
        LifeChanged l => l.Player,
        ObjectDrawn draw => draw.Player,
        DrawFromEmptyLibraryAttempted draw => draw.Player,
        PlayerCountersChanged p => p.Player,
        PlayerLost p => p.Player,
        ManaChanged m => m.Player,
        _ => null,
    };

    private static PlayerId? DrawingPlayer(GameEvent proposed) => proposed switch
    {
        ObjectDrawn draw => draw.Player,
        DrawFromEmptyLibraryAttempted draw => draw.Player,
        _ => null,
    };
}
