using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A replacement system that records what combat offered it and hands back whatever the test
/// says happens. Combat's side of CR 614.1a is that it proposes each event of the batch and takes
/// the answer; which effects apply, in what order, and what each does to the event are the
/// replacement system's, and are tested there.
/// </summary>
internal sealed class CombatReplacementFake : IReplacementSystem
{
    public string Name => "fake replacements";

    public List<GameEvent> Offered { get; } = [];

    public List<PlayerId> Affected { get; } = [];

    /// <summary>What happens instead. Null means the event stands exactly as it was proposed.</summary>
    public Func<GameState, GameEvent, Outcome>? Instead { get; init; }

    public Outcome Replace(GameState state, GameEvent proposed, PlayerId affected)
    {
        Offered.Add(proposed);
        Affected.Add(affected);
        return Instead is { } instead
            ? instead(state, proposed)
            : new Outcome(proposed.Apply(state), [proposed]);
    }

    /// <summary>The event as proposed, applied. What "nothing replaces it" looks like.</summary>
    public static Outcome Stands(GameState state, GameEvent proposed) =>
        new(proposed.Apply(state), [proposed]);

    /// <summary>CR 615.1 — a shield that prevents the damage outright: nothing happens at all.</summary>
    public static Outcome Nothing(GameState state, GameEvent proposed) => Outcome.Unchanged(state);

    /// <summary>CR 614.1b — a multiplier: twice as much damage is dealt instead.</summary>
    public static Outcome Doubled(GameState state, GameEvent proposed)
    {
        if (proposed is not DamageDealt damage)
            return Stands(state, proposed);

        var twice = damage with { Amount = damage.Amount * 2 };
        return new Outcome(twice.Apply(state), [twice]);
    }

    /// <summary>
    /// CR 616.1 — two effects apply and the affected player has to say which goes first, so the
    /// game stops and the proposed event does not happen until they answer (CR 101.4).
    /// </summary>
    public static Outcome Asks(GameState state, GameEvent proposed)
    {
        var choice = new ChoiceRequested(new PendingChoice(
            new ChoiceId(state.NextChoiceId),
            new PlayerId(0),
            ChoiceKinds.ReplacementOrder,
            "Choose which replacement applies first",
            [new ChoiceOption("a", "one"), new ChoiceOption("b", "the other")],
            1,
            1,
            new ChoiceContext(null, null, "replacement", ImmutableDictionary<string, string>.Empty, proposed)));

        return new Outcome(choice.Apply(state), [choice]);
    }
}

/// <summary>
/// The interpreter is E9's. Nothing in these tests runs a replacement's own effect, so the real
/// replacement system is handed one that refuses rather than one that quietly does nothing.
/// </summary>
internal sealed class CombatReplacementNoInterpreter : IInterpreter
{
    public string Name => "no interpreter";

    public Outcome Resolve(GameState state, ObjectId stackObject) => throw new NotSupportedException();

    public Outcome Run(GameState state, Effect effect, EffectContext context) => throw new NotSupportedException();
}
