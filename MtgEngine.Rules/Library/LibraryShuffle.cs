using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Library;

/// <summary>Randomizes an existing library and records its new order using the shared RNG.</summary>
public static class LibraryShuffle
{
    public static Outcome Apply(GameState state, ZoneKey library)
    {
        var (order, next) = Rng.Shuffle(state.Rng, state.In(library));
        var events = ImmutableList.CreateBuilder<GameEvent>();
        if (next != state.Rng)
        {
            var advanced = new RngAdvanced(next);
            state = advanced.Apply(state);
            events.Add(advanced);
        }
        var shuffled = new ZoneReordered(library, order, Shuffle: true);
        events.Add(shuffled);
        return new Outcome(shuffled.Apply(state), events.ToImmutable());
    }
}
