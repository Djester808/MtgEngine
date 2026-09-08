using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Scenarios;

/// <summary>
/// Runs a scenario through the real engine: builds the board from the setup, applies each step
/// as a command, checks each expectation against the final state.
/// </summary>
/// <remarks>
/// The board is placed by honour-system events rather than by playing a game up to it, because a
/// scenario states a position and not the history that reached it. That is the same admission the
/// table makes with <see cref="ManualMove"/> — the log carries every placement, so a run still
/// replays — and it is why the setup does not walk the untap step, which would untap what the
/// scenario tapped, or the draw step, which would draw from a library the scenario did not fill.
/// Everything after the setup is the engine's: a step is a <see cref="Command"/> through
/// <see cref="Game.Apply"/>, and a refusal is the engine's refusal.
/// </remarks>
public sealed class ScenarioRunner(Game game) : IScenarioRunner
{
    public Game Game { get; } = game;

    /// <summary>
    /// Whether this scenario's questions are answered with the first legal option
    /// (<see cref="ScenarioAnswers"/>). The opt-in is the scenario's own
    /// (<see cref="ScenarioSetup.AutoAnswer"/>) and not the runner's, so one card file can hold a
    /// generated scenario that answers itself beside a hand-written one that does not — a run used
    /// to be all one way or all the other. A scenario that carries an <see cref="AnswerStep"/> is
    /// left alone whatever the flag says: a scenario about a choice must still make it, and
    /// interleaving first-legal answers with its own would erase the point of the test.
    /// </summary>
    [RuleRef("101.4")]
    public static bool AutoAnswers(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        return scenario.Setup.AutoAnswer && !scenario.Steps.OfType<AnswerStep>().Any();
    }

    public ScenarioResult Run(Scenario scenario, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ct.ThrowIfCancellationRequested();

        var run = new ScenarioRun(Game, scenario, AutoAnswers(scenario));
        try
        {
            run.Build(ct);
            run.Play(ct);
        }
        catch (ScenarioFailure failure)
        {
            return run.Report(failure.Message);
        }

        return run.Report(null);
    }
}

/// <summary>A scenario could not be run as written, or a step the engine should have allowed was refused.</summary>
internal sealed class ScenarioFailure(string message) : Exception(message);

/// <summary>One run of one scenario: the state, the log, and the names the scenario used for things.</summary>
internal sealed partial class ScenarioRun(Game game, Scenario scenario, bool autoAnswer = false)
{
    private readonly Dictionary<string, PlayerId> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ObjectId> _objects = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GameEvent> _log = [];
    private GameState _state = GameStarted.Empty;
    private string? _refusal;
    private bool _expectingRefusal;

    internal GameState State => _state;

    internal IReadOnlyDictionary<string, ObjectId> Objects => _objects;

    internal IReadOnlyDictionary<string, PlayerId> Players => _players;

    internal Game Host => game;

    internal IReadOnlyList<GameEvent> Events => _log;

    /// <summary>Where in <see cref="Events"/> the scenario's own steps begin: everything before it is the setup (CR 103), which shuffled the libraries.</summary>
    internal int StepsBeganAt { get; private set; }

    /// <summary>CR 103: the game is created empty and the scenario's board is placed into it.</summary>
    [RuleRef("103")]
    internal void Build(CancellationToken ct)
    {
        var setup = scenario.Setup;
        if (setup.Players.Count == 0)
            throw new ScenarioFailure("A scenario needs at least one player.");

        for (var seat = 0; seat < setup.Players.Count; seat++)
            _players[setup.Players[seat].Name] = new PlayerId(seat);
        var active = Player(setup.ActivePlayer);

        Merge(game.Create(new GameSetup(
            [.. setup.Players.Select(p => new PlayerStart(p.Name, [], []))],
            Seed: unchecked((ulong)setup.Seed),
            StartingLife: StartingLife,
            OpeningHandSize: 0,
            StartingPlayer: active.Value,
            OfferMulligans: false)));

        foreach (var player in setup.Players)
        {
            ct.ThrowIfCancellationRequested();
            var id = Player(player.Name);
            if (player.Life != StartingLife)
                Emit(new LifeChanged(id, player.Life - StartingLife, "scenario"));
            // Hand first: a scenario that stocks a library with copies of the card under test
            // still means the one in hand when it names it, and the name is first-wins.
            Place(id, Zone.Hand, player.Hand);
            Place(id, Zone.Library, player.Library);
            Place(id, Zone.Battlefield, player.Battlefield);
            Place(id, Zone.Graveyard, player.Graveyard);
            Place(id, Zone.Exile, player.Exile);
            Place(id, Zone.Command, player.Command);
            if (player.ManaPool is { Length: > 0 })
                Emit(new ManaChanged(id, ParseMana(player.ManaPool), "scenario"));
            foreach (var (kind, count) in player.Counters ?? new Dictionary<string, int>())
                Emit(new PlayerCountersChanged(id, kind, count));
        }

        Attach(setup);

        if (setup.Turn != 1)
            Emit(new TurnBegan(setup.Turn, active, Extra: false));
        if (_state.Turn.Step != setup.StartAt)
            Emit(new StepBegan(setup.StartAt));
        Emit(new PriorityGranted(active));
    }

    internal void Play(CancellationToken ct)
    {
        StepsBeganAt = _log.Count;
        foreach (var step in scenario.Steps)
        {
            ct.ThrowIfCancellationRequested();
            Step(step);
            Answer();
            if (_state.Outcome is not null)
                break;
        }
    }

    /// <summary>
    /// CR 101.4: the game stopped and is waiting. A generated scenario answers with the first
    /// legal option, through a real <see cref="AnswerChoice"/> command, so the log carries the
    /// answer and the run still replays. A question with nothing legal to say is left standing —
    /// except a search that found nothing, which is answered with none (CR 701.23b).
    /// </summary>
    [RuleRef("101.4")]
    private bool Answer()
    {
        var answered = false;
        for (var guard = 0; autoAnswer && guard < 64; guard++)
        {
            if (_state.Pending is not { } pending || _state.Outcome is not null)
                return answered;
            var choice = ScenarioAnswers.FirstLegal(pending);
            if (choice.Count == 0 && !ScenarioAnswers.FailsToFind(pending))
                return answered;
            Command(new AnswerChoice(pending.Player, pending.Id, [.. choice]));
            if (_state.Pending == pending)
                return answered;
            answered = true;
        }

        return answered;
    }

    internal ScenarioResult Report(string? error)
    {
        IReadOnlyList<ExpectationResult> results = error is null
            ? ScenarioExpectations.Check(this, scenario.Expect)
            : [.. scenario.Expect.Select(e => new ExpectationResult(e, false, "not run"))];
        return new ScenarioResult(
            scenario.Id,
            Passed: error is null && results.All(r => r.Passed),
            Results: results,
            Error: error,
            Log: [.. _log.Select(Describe)]);
    }

    internal const int StartingLife = 20;

    internal PlayerId Player(string name) =>
        _players.TryGetValue(name ?? string.Empty, out var id)
            ? id
            : throw new ScenarioFailure($"No player called '{name}' in this scenario.");

    internal bool IsPlayer(string name) => name is not null && _players.ContainsKey(name);

    internal ObjectId Object(string key) =>
        _objects.TryGetValue(key ?? string.Empty, out var id)
            ? id
            : throw new ScenarioFailure($"No card called '{key}' in this scenario.");

    private void Step(ScenarioStep step)
    {
        switch (step)
        {
            case ResolveAllStep:
                ResolveAll();
                break;
            case AdvanceToStep advance:
                AdvanceTo(advance);
                break;
            case AddManaStep mana:
                Emit(new ManaChanged(Player(mana.Player), ParseMana(mana.Mana), "scenario"));
                break;
            case ExpectRefusedStep refused:
                Expect(refused);
                break;
            default:
                Command(Commands(step));
                break;
        }
    }

    /// <summary>CR 601.2 and 602.2: the scenario says who acts, so the runner opens their window and the engine judges the rest.</summary>
    [RuleRef("117.1a")]
    private void Command(Command command)
    {
        if (command is not PassPriority and not AnswerChoice && _state.Pending is null && _state.Priority.Holder != command.Player)
            Emit(new PriorityGranted(command.Player));

        switch (game.Apply(_state, command))
        {
            case Applied applied:
                _state = applied.State;
                Record(applied.Events);
                break;
            case Refused refusal:
                _refusal = refusal.Rule is null ? refusal.Reason : $"{refusal.Reason} ({refusal.Rule})";
                if (!_expectingRefusal)
                    throw new ScenarioFailure($"The engine refused {command.GetType().Name}: {_refusal}");
                break;
            default:
                break;
        }
    }

    private Command Commands(ScenarioStep step) => step switch
    {
        CastStep c => new CastSpell(
            Player(c.Player), Object(c.Card), 0, TargetObjects(c.Targets), TargetPlayers(c.Targets),
            c.Modes is null ? null : [.. c.Modes], c.X, c.CostChoices is null ? null : [.. c.CostChoices], Keyword(c.AlternativeCost),
            CostObjects: CostObjects(c.CostObjects)),
        // CR 608.2d: the colour of "add one mana of any color" is announced with the activation,
        // so a scenario that activates such an ability carries the colour on its step and the
        // runner hands it to the command rather than leaving the choice unmade.
        ActivateStep a => new ActivateAbility(
            Player(a.Player), Object(a.Card), a.AbilityIndex, TargetObjects(a.Targets), TargetPlayers(a.Targets),
            null, a.X, a.CostChoices is null ? null : [.. a.CostChoices], CostObjects(a.CostObjects), a.ManaColors),
        // CR 605.3: tapping a permanent for mana is activating a mana ability of it, so the step
        // becomes the same command an ActivateStep would have built — with the mana ability the
        // step names turned into the index among all abilities that the command wants.
        TapForManaStep t => new ActivateAbility(Player(t.Player), Object(t.Card), ManaAbilityIndex(t), ManaColors: t.ManaColors),
        PlayLandStep p => new PlayLand(Player(p.Player), Object(p.Card)),
        AttackStep a => new DeclareAttackers(Player(a.Player), [.. a.Attacks.Select(at => new AttackDeclaration(Object(at.Attacker), Defender(at.Defender)))]),
        BlockStep b => new DeclareBlockers(Player(b.Player), [.. b.Blocks.Select(bl => new BlockDeclaration(Object(bl.Blocker), Object(bl.Attacker)))]),
        PassStep p => new PassPriority(Player(p.Player)),
        AnswerStep a => new AnswerChoice(
            Player(a.Player),
            _state.Pending?.Id ?? throw new ScenarioFailure("The scenario answered a question the game had not asked."),
            [.. a.Answer]),
        _ => throw new ScenarioFailure($"The runner has no command for {step.GetType().Name}."),
    };

    private void Expect(ExpectRefusedStep step)
    {
        var outer = _expectingRefusal;
        _refusal = null;
        _expectingRefusal = true;
        try
        {
            Step(step.Attempt);
        }
        finally
        {
            _expectingRefusal = outer;
        }

        if (_refusal is null)
            throw new ScenarioFailure($"The engine allowed {step.Attempt.GetType().Name}, which the scenario expected it to refuse.");
        if (step.ReasonContains is { Length: > 0 } wanted && !_refusal.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            throw new ScenarioFailure($"Refused for another reason: {_refusal}");
    }

    /// <summary>CR 117.4: everyone passes in succession until the stack is empty.</summary>
    [RuleRef("117.4")]
    private void ResolveAll()
    {
        var stack = ZoneKey.Shared(Zone.Stack);
        for (var guard = 0; guard < 500; guard++)
        {
            if (_state.Pending is not null && !Answer())
                return;
            if (_state.In(stack).IsEmpty || _state.Pending is not null || _state.Outcome is not null)
                return;
            var holder = _state.Priority.Holder ?? _state.Turn.ActivePlayer;
            if (_state.Priority.Holder is null)
                Emit(new PriorityGranted(holder));
            Command(new PassPriority(holder));
        }

        throw new ScenarioFailure("The stack never emptied.");
    }

    /// <summary>CR 500: turn-based actions run, so advancing is the turn system's and not an event.</summary>
    [RuleRef("500")]
    private void AdvanceTo(AdvanceToStep step)
    {
        for (var guard = 0; guard < 60; guard++)
        {
            if (_state.Turn.Step == step.Step && (step.Turn is null || _state.Turn.Number == step.Turn))
                return;
            if (_state.Pending is not null || _state.Outcome is not null)
                return;
            var advanced = game.Engine.Turn.Advance(_state);
            Merge(advanced);
            Merge(game.Settle(_state, advanced.Events));
        }

        throw new ScenarioFailure($"The game never reached {step.Step}.");
    }

    private void Place(PlayerId owner, Zone zone, IReadOnlyList<CardRef>? cards)
    {
        foreach (var card in cards ?? [])
        {
            var definition = game.Store.ByName(card.Name)
                ?? ScenarioFiller.For(card.Name)
                ?? throw new ScenarioFailure($"No definition for '{card.Name}'.");
            var placed = new GameObject(
                new ObjectId(_state.NextObjectId), definition, 0, owner, owner,
                Zones.ZoneSystem.KeyFor(zone, owner), _state.NextTimestamp)
            {
                Tapped = card.Tapped,
                FaceDown = card.FaceDown,
                Damage = card.Damage,
                SummoningSick = card.SummoningSick,
                Counters = card.Counters is null
                    ? ImmutableDictionary<string, int>.Empty
                    : card.Counters.ToImmutableDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal),
            };
            Name(card.Alias ?? card.Name, placed.Id);
            Placement(placed);
        }
    }

    /// <summary>
    /// A placed card is named by the scenario — its alias when it has one, its printed name when
    /// it does not — so the placement does not go through <see cref="Track"/>, whose naming of a
    /// created object after its definition would let an aliased library copy claim the plain name
    /// the scenario meant for the card in hand (seam-requests/P7.md § 2). Tokens and copies the
    /// game itself makes still go through <see cref="Track"/>, which is what it is for.
    /// </summary>
    private void Placement(GameObject placed)
    {
        var created = new ObjectCreated(placed, -1);
        _state = created.Apply(_state);
        _log.Add(created);
    }

    /// <summary>CR 301.5: an Equipment or Aura placed by a scenario is already attached.</summary>
    [RuleRef("301.5")]
    private void Attach(ScenarioSetup setup)
    {
        foreach (var card in setup.Players.SelectMany(AllCards).Where(c => c.AttachedTo is { Length: > 0 }))
            Emit(new ObjectChanged(Object(card.Alias ?? card.Name), AttachedTo: Object(card.AttachedTo!)));

        static IEnumerable<CardRef> AllCards(PlayerSetup p) =>
        [
            .. p.Battlefield ?? [], .. p.Hand ?? [], .. p.Library ?? [],
            .. p.Graveyard ?? [], .. p.Exile ?? [], .. p.Command ?? [],
        ];
    }

    private void Name(string key, ObjectId id)
    {
        if (!_objects.ContainsKey(key))
            _objects[key] = id;
    }

    private ImmutableList<ObjectId>? TargetObjects(IReadOnlyList<string>? targets)
    {
        var chosen = (targets ?? []).Where(t => !IsPlayer(t)).Select(Object).ToImmutableList();
        return chosen.IsEmpty ? null : chosen;
    }

    /// <summary>
    /// CR 601.2c: the objects a printed cost eats, chosen as the spell is cast and paid for at
    /// CR 601.2h. Named by alias first and card name second, exactly as a target is, and resolved
    /// through the same table, so an object the scenario placed is still found after it has
    /// changed zones (CR 400.7).
    /// </summary>
    [RuleRef("601.2c")]
    private ImmutableList<ObjectId>? CostObjects(IReadOnlyList<string>? objects)
    {
        var chosen = (objects ?? []).Select(Object).ToImmutableList();
        return chosen.IsEmpty ? null : chosen;
    }

    private ImmutableList<PlayerId>? TargetPlayers(IReadOnlyList<string>? targets)
    {
        var chosen = (targets ?? []).Where(IsPlayer).Select(Player).ToImmutableList();
        return chosen.IsEmpty ? null : chosen;
    }

    /// <summary>CR 506.1: an attacker attacks a player, or a planeswalker or battle they control.</summary>
    [RuleRef("506.1")]
    private CombatTarget Defender(string defender) =>
        IsPlayer(defender) ? new CombatTarget(Player(defender), null) : new CombatTarget(null, Object(defender));

    private static KeywordKind? Keyword(string? name) =>
        name is { Length: > 0 } && Enum.TryParse<KeywordKind>(name, ignoreCase: true, out var kind) ? kind : null;

    private void Merge(Outcome outcome)
    {
        _state = outcome.State;
        Record(outcome.Events);
    }

    private void Emit(GameEvent e)
    {
        _state = e.Apply(_state);
        Record([e]);
    }

    private void Record(IReadOnlyList<GameEvent> events)
    {
        _log.AddRange(events);
        Track(events);
    }

    /// <summary>
    /// CR 400.7: a zone change makes a new object, so the name a scenario used has to follow it.
    /// A token or a copy the game itself made is named after its definition, if that name is free.
    /// </summary>
    [RuleRef("400.7")]
    private void Track(IReadOnlyList<GameEvent> events)
    {
        foreach (var e in events)
        {
            switch (e)
            {
                case ObjectMoved moved:
                    foreach (var key in _objects.Where(kv => kv.Value == moved.From).Select(kv => kv.Key).ToList())
                        _objects[key] = moved.To.Id;
                    break;
                case ObjectCreated created:
                    Name(created.Object.Definition.Name, created.Object.Id);
                    break;
                default:
                    break;
            }
        }
    }

    private static string Describe(GameEvent e) => $"{e.GetType().Name} {e}";
}
