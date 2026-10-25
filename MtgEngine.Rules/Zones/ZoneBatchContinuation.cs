using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Zones;

/// <summary>Completes or retains deferred movements without advancing the turn.</summary>
internal static class ZoneBatchContinuation
{
    public static Outcome Complete(Outcome resumed, ChoiceContext previous, IZoneSystem zones)
    {
        var remaining = previous.DeferredZoneMoves;
        if (remaining.IsEmpty && previous.ZoneMoveSnapshot is null && previous.DeferredMoveHistory.IsEmpty)
            return resumed;
        if (resumed.State.Pending is not { } pending)
            return remaining.IsEmpty ? resumed : resumed.Then(state => zones.ResumeMoveBatch(state, remaining,
                previous.ZoneMoveSnapshot, previous.DeferredMoveHistory));
        var marked = new ChoiceRequested(pending with
        {
            Context = pending.Context with
            {
                DeferredZoneMoves = pending.Context.DeferredZoneMoves.AddRange(remaining),
                DeferredMoveHistory = pending.Context.DeferredMoveHistory.SetItems(previous.DeferredMoveHistory),
                ZoneMoveSnapshot = pending.Context.ZoneMoveSnapshot ?? previous.ZoneMoveSnapshot,
            },
        });
        return resumed with { State = marked.Apply(resumed.State), Events = resumed.Events.Add(marked) };
    }
}
