using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// CR 616.1 from inside a resolution: offering an event to the replacement layer, stopping the
/// walk when the layer has to ask which effect applies first, and picking the walk up again at
/// the very proposal that asked. Split out of EffectInterpreter.cs when that file reached the
/// 500-line ceiling tools/check-file-size.js enforces.
/// </summary>
[RuleRef("616.1")]
public sealed partial class EffectInterpreter
{
    /// <summary>
    /// Offer an event to the replacement layer, CR 616.1, saying which effects have already applied
    /// to it. A walk that is itself a replacement's "instead" carries its own key (CR 614.5), so the
    /// event it makes is never replaced by the effect that made it.
    /// <para>
    /// CR 616.1's order question stops the walk, because the proposed event has NOT happened when
    /// it is asked and everything written after it would otherwise run first. Halted was written
    /// in exactly one place — Ask — so a replacement question did not stop the walk at all, and
    /// every halted-guard after a Propose was dead code. Halting alone is not enough either: the
    /// layer's own question names no effect tree (its context carries no source, ability or path),
    /// so an answer to it finished the event and then had nowhere to go, and every instruction
    /// after the proposal was dropped. The question is therefore asked from HERE, through the same
    /// Ask every other question uses, so it carries this walk's coordinates and its memory and the
    /// answer comes back to this node (CR 101.4).
    /// </para>
    /// </summary>
    [RuleRef("614.5")]
    private EffectRun Propose(EffectRun run, GameEvent proposed, PlayerId affected)
        => ProposeThrough(run, current => replacements.Replace(current.State, proposed, affected, current.Context.UsedReplacements));

    private EffectRun ProposeThrough(EffectRun run, Func<EffectRun, Outcome> perform)
    {
        // Which proposal of this node this is. The node that stopped runs again from its start
        // when the walk resumes (CR 608.2), so the events it already offered are counted off
        // rather than offered a second time.
        var ordinal = string.Equals(run.ProposalsAt, run.Path, StringComparison.Ordinal) ? run.Proposals : 0;
        run = run with { ProposalsAt = run.Path, Proposals = ordinal + 1 };

        Outcome? resumed = null;
        if (Ordered(run) is { } answered && run.Answer is { } answer)
        {
            // Already offered, already applied, already in the log before the walk stopped.
            if (ordinal < answered)
                return run;

            // The answer is about this proposal: the layer applies the effect the player chose and
            // finishes the event, and the walk carries on with the instructions that follow it.
            if (ordinal == answered)
                resumed = answer.Context.ReplacementContinuation is { } inner
                    ? Resume(run.State, answer with { Context = inner })
                    : Zones.ZoneMoveSnapshot.Resume(replacements, run.State, answer);
        }

        var outcome = resumed ?? perform(run);
        if (outcome.Events.OfType<ChoiceRequested>().LastOrDefault() is not { } asked)
            return Merge(run, outcome);

        // Keep what the layer did before it asked — the replacements that applied on their own —
        // and drop the question it raised, so the same question can be asked again from here.
        var kept = outcome.Events.Where(e => e is not ChoiceRequested).ToImmutableList();
        var before = Merge(run, new Outcome(kept.Aggregate(run.State, (state, e) => e.Apply(state)), kept));

        // A replacement-order choice can belong to a suspended interpreter walk.
        // Preserve its continuation before resuming the outer operation.
        var suspendedWalk = asked.Choice.Context.Extra.ContainsKey(ProposalKey);
        var innerContext = string.Equals(asked.Choice.Kind, ChoiceKinds.ReplacementOrder, StringComparison.Ordinal)
            && !suspendedWalk
            ? null : asked.Choice.Context;
        return Ask(
            before,
            asked.Choice.Player,
            asked.Choice.Kind,
            asked.Choice.Prompt,
            asked.Choice.Options,
            asked.Choice.Min,
            asked.Choice.Max,
            shape: context => context with
            {
                // The event the question is about and the effects already spent on it are the
                // layer's to read back (CR 616.1); this walk's own extras are laid over them.
                Event = asked.Choice.Context.Event,
                ReplacementContinuation = innerContext,
                ZoneMoveSnapshot = asked.Choice.Context.ZoneMoveSnapshot ?? run.Answer?.Context.ZoneMoveSnapshot,
                Extra = asked.Choice.Context.Extra
                    // The nested replacement keeps its history in innerContext. Only this
                    // walk's inherited history belongs on the outer continuation; otherwise
                    // the next independent draw would incorrectly skip that replacement.
                    .RemoveRange(asked.Choice.Context.Extra.Keys.Where(key => key.StartsWith(UsedPrefix, StringComparison.Ordinal)))
                    .SetItems(context.Extra)
                    .SetItem(ProposalKey, ordinal.ToString(CultureInfo.InvariantCulture)),
            });
    }

    /// <summary>
    /// Which proposal of this node the CR 616.1 answer in hand is about, or null when the walk is
    /// not resuming from one. The ordinal rides on the question because the node, not the walk, is
    /// what runs again.
    /// </summary>
    [RuleRef("616.1")]
    private static int? Ordered(EffectRun run)
    {
        if (run.Answer is not { } answer
            || (!string.Equals(answer.Kind, ChoiceKinds.ReplacementOrder, StringComparison.Ordinal)
                && answer.Context.ReplacementContinuation is null)
            || !string.Equals(run.AnswerPath, run.Path, StringComparison.Ordinal))
        {
            return null;
        }

        return answer.Context.Extra.TryGetValue(ProposalKey, out var which)
            && int.TryParse(which, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal)
                ? ordinal
                : null;
    }

    /// <summary>
    /// Which proposal of its node raised a CR 616.1 question, as it rides on that question. It is
    /// also what tells a walk's question from the replacement layer's own on the way back, so it
    /// is written by Propose and nowhere else.
    /// </summary>
    internal const string ProposalKey = "proposal";
}
