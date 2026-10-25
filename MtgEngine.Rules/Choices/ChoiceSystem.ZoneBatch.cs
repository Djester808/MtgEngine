using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Choices;

public sealed partial class ChoiceSystem
{
    private Outcome FinishZoneBatch(Outcome resumed, PendingChoice choice)
    {
        var finished = Zones.ZoneBatchContinuation.Complete(resumed, choice.Context, zones);
        return choice.Context.AdvanceAfterZoneBatch ? AdvanceAfterBatch(finished) : finished;
    }

    private Outcome AdvanceAfterBatch(Outcome outcome)
    {
        if (outcome.State.Pending is not { } pending)
            return outcome.Then(turn.Advance);
        var marked = new ChoiceRequested(pending with
        {
            Context = pending.Context with { AdvanceAfterZoneBatch = true },
        });
        return outcome with { State = marked.Apply(outcome.State), Events = outcome.Events.Add(marked) };
    }
}
