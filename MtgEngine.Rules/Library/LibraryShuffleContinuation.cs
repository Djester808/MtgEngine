using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Library;

/// <summary>Finishes owed shuffles only after the preceding operation's choices finish.</summary>
internal static class LibraryShuffleContinuation
{
    public static Outcome Complete(Outcome outcome, ImmutableList<PlayerId> players, IZoneSystem zones)
    {
        for (var index = 0; index < players.Count; index++)
        {
            if (outcome.State.Pending is { } pending)
            {
                var deferred = new ChoiceRequested(pending with
                {
                    Context = pending.Context with
                    {
                        DeferredShuffles = pending.Context.DeferredShuffles.AddRange(players.Skip(index)),
                    },
                });
                return outcome with { State = deferred.Apply(outcome.State), Events = outcome.Events.Add(deferred) };
            }
            var player = players[index];
            outcome = outcome.Then(state => zones.Shuffle(state, player));
        }
        return outcome;
    }
}
