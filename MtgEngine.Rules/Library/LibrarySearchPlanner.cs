using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Library;

public sealed record LibrarySearchPlan(ImmutableList<Zone> Zones, ImmutableList<ObjectId> Inspected,
    ImmutableList<ObjectId> Candidates, int Minimum, int Maximum);

public sealed record LibrarySearchParticipant(PlayerId Searcher, PlayerId LibraryOwner, LibrarySearchPlan Plan);

public sealed record LibrarySearchMove(ObjectId Card, Zone To, Placement Where, bool Tapped, bool WithinLibrary);

/// <summary>
/// Plans permitted search zones, candidates, selection bounds and destinations without changing
/// game state. The interpreter handles prompts and continuation; the zone system performs moves.
/// </summary>
[RuleRef("701.23")]
public sealed class LibrarySearchPlanner(IQuery query, ICharacteristics characteristics)
{
    public static ImmutableList<Zone> RequestedZones(Search search) =>
        search.Zones is { Count: > 0 } zones ? [.. zones.Distinct()] : [search.From];

    public ImmutableList<Zone> AccessibleZones(GameState state, Search search, PlayerId searcher)
    {
        var blocked = characteristics.PlayerRestrictions(state, searcher).CantSearchLibraries;
        return [.. RequestedZones(search).Where(zone => zone != Zone.Library || !blocked)];
    }

    public LibrarySearchPlan Plan(GameState state, Search search, EffectContext context, PlayerId searcher, PlayerId libraryOwner)
    {
        var accessible = AccessibleZones(state, search, searcher);
        if (accessible.IsEmpty)
            return new LibrarySearchPlan([], [], [], 0, 0);
        var top = search.Top is null ? (int?)null : Math.Max(0, query.Evaluate(state, search.Top, context));
        var byZone = accessible.Select(zone =>
        {
            var filter = search.For.In is null ? search.For with { In = zone } : search.For;
            var owner = zone == Zone.Library ? libraryOwner : searcher;
            var cards = LibrarySearch.Find(state.In(ZoneSystem.KeyFor(zone, owner)),
                id => query.Matches(state, filter, id, context), zone == Zone.Library ? top : null);
            return (Zone: zone, cards.Inspected, cards.Matching);
        }).ToList();
        var requested = search.UpTo is null ? 1 : query.Evaluate(state, search.UpTo, context);
        var (minimum, maximum) = LibrarySearchPolicy.Bounds(search, requested,
            byZone.Select(zone => (zone.Zone, zone.Matching.Count)));
        return new LibrarySearchPlan(accessible, [.. byZone.SelectMany(zone => zone.Inspected)],
            [.. byZone.SelectMany(zone => zone.Matching)], minimum, maximum);
    }

    /// <summary>Freeze destinations before any selected card moves, including across replacement prompts.</summary>
    public ImmutableList<LibrarySearchMove> Moves(GameState state, Search search, EffectContext context, ImmutableList<ObjectId> found) =>
        [.. found.Select((id, index) =>
        {
            var (to, where, tapped) = Destination(state, search, context, id, index == 0);
            return new LibrarySearchMove(id, to, where, tapped,
                to == Zone.Library && state.Objects.TryGetValue(id, out var card) && card.Zone.Zone == Zone.Library);
        })];

    /// <summary>Conditional destinations match the card in its current zone; later finds can use OthersTo.</summary>
    public (Zone To, Placement Where, bool Tapped) Destination(GameState state, Search search, EffectContext context, ObjectId id, bool first)
    {
        if (search.Instead is { Count: > 0 } instead && state.Objects.TryGetValue(id, out var card))
        {
            foreach (var entry in instead)
                if (query.Matches(state, entry.Matching with { In = card.Zone.Zone }, id, context))
                    return (entry.To, Placement.Default, entry.Tapped);
        }
        return first || search.OthersTo is not { } others
            ? (search.To, search.Where, search.Tapped)
            : (others, Placement.Default, search.OthersTapped);
    }
}
