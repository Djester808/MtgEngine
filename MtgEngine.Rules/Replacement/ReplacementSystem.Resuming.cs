using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Replacement;

/// <summary>
/// Answering the question of CR 616.1. The game halted with the proposed event beside the
/// question and the effects that had already applied recorded with it; the answer names one of
/// the effects that still applied, that effect applies to the event as it then stood, and the
/// result is offered to the ones that have not applied yet. Resumption is by kind and locator,
/// never a captured continuation, so a game rebuilt from its log resumes the same way (CR 101.4).
/// </summary>
public sealed partial class ReplacementSystem
{
    [RuleRef("616.1")]
    public Outcome Resume(GameState state, Resumption resumption)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(resumption);

        if (!string.Equals(resumption.Kind, ChoiceKinds.ReplacementOrder, StringComparison.Ordinal))
            throw new InvalidOperationException($"replacement resumption was handed the choice kind '{resumption.Kind}'.");

        // The question is about one event, and applying the chosen effect means applying it to
        // that event; without it there is nothing to resume.
        if (resumption.Context.Event is not { } proposed)
            throw new InvalidOperationException("a replacement-order answer arrived without the event it was about.");

        var used = Used(resumption.Context);
        var chosen = Chosen(state, proposed, used, resumption.Chosen);

        // An answer naming an effect that no longer applies applies nothing; the game looks again,
        // which either finishes the event or asks about the effects that are left (CR 616.1).
        if (chosen is null)
            return Continue(state, proposed, resumption.Player, used, []);

        // CR 616.1: no effect applies to the same event twice, whether it applied before the
        // question was raised or is applying because of the answer.
        used.Add(chosen.Key);
        var applied = Apply(state, proposed, chosen);
        return Continue(applied.State, applied.Event, resumption.Player, used, applied.Extra);
    }

    /// <summary>The effects that had already applied when the question was raised, as <c>Ask</c> recorded them.</summary>
    private static HashSet<string> Used(ChoiceContext context)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (!context.Extra.TryGetValue(UsedKey, out var keys))
            return used;

        foreach (var key in keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            used.Add(key);

        return used;
    }

    /// <summary>
    /// The effect the answer names, found again in the state as it now stands rather than kept
    /// across the halt: the keys are derived from the objects and active effects themselves, so a
    /// replayed log finds the same candidate.
    /// </summary>
    private Candidate? Chosen(GameState state, GameEvent proposed, HashSet<string> used, ImmutableList<ChoiceOption> answer) =>
        answer.Count == 0
            ? null
            : Applicable(state, proposed, used).Find(c => string.Equals(c.Key, answer[0].Id, StringComparison.Ordinal));
}
