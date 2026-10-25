using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Library;

public sealed record PendingDrawBatch(PlayerId Player, int Count)
{
    public ImmutableHashSet<string>? UsedReplacements { get; init; }
}

/// <summary>Executes individual draws through replacement effects and commits their movement exactly once.</summary>
[RuleRef("121")]
public sealed class LibraryDrawCoordinator(IReplacementSystem? replacements)
{
    public Outcome Draw(GameState state, PlayerId player, int count, IEnumerable<string>? usedReplacements = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var used = usedReplacements?.ToImmutableHashSet(StringComparer.Ordinal);
        var outcome = Outcome.Unchanged(state);
        for (var index = 0; index < count && outcome.State.Pending is null; index++)
        {
            var current = outcome.State;
            GameEvent proposed = current.In(ZoneKey.Of(Zone.Library, player)).IsEmpty
                ? new DrawFromEmptyLibraryAttempted(player)
                : new ObjectDrawn(player, new ObjectId(current.NextObjectId)) { AwaitingCardMovement = true };
            var offered = replacements?.Replace(current, proposed, player, used)
                ?? new Outcome(current, [proposed]);
            // Normalize the event stream as well as real replacements: a pass-through seam may
            // return the original proposal. Committed draws have no marker and cannot move twice.
            var events = ImmutableList.CreateBuilder<GameEvent>();
            for (var offeredIndex = 0; offeredIndex < offered.Events.Count; offeredIndex++)
            {
                var completed = Complete(current, offered.Events[offeredIndex]);
                current = completed.State;
                events.AddRange(completed.Events);
            }
            outcome = new Outcome(current, outcome.Events.AddRange(events));
            if (current.Pending is not null && index + 1 < count)
                outcome = Defer(outcome, [new PendingDrawBatch(player, count - index - 1) { UsedReplacements = used }]);
        }
        return outcome;
    }

    internal static Outcome Resume(Outcome outcome, ImmutableList<PendingDrawBatch> remaining, IZoneSystem zones)
    {
        for (var index = 0; index < remaining.Count; index++)
        {
            if (outcome.State.Pending is not null)
                return Defer(outcome, remaining.Skip(index).ToImmutableList());
            var batch = remaining[index];
            outcome = outcome.Then(state => zones.Draw(state, batch.Player, batch.Count, batch.UsedReplacements));
        }
        return outcome;
    }

    private static Outcome Defer(Outcome outcome, ImmutableList<PendingDrawBatch> remaining)
    {
        var pending = outcome.State.Pending!;
        var marked = new ChoiceRequested(pending with
        {
            Context = pending.Context with { DeferredDraws = pending.Context.DeferredDraws.AddRange(remaining) },
        });
        return outcome with { State = marked.Apply(outcome.State), Events = outcome.Events.Add(marked) };
    }

    /// <summary>Shared terminal path for immediate and resumed draws, after replacement selection finishes.</summary>
    internal static Outcome Complete(GameState state, GameEvent item)
    {
        if (item is not ObjectDrawn { AwaitingCardMovement: true } draw)
            return new Outcome(item.Apply(state), [item]);

        // Resolve against current state after any external replacement decisions.
        return LibraryDraw.Apply(state, draw.Player);
    }
}
