using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Library;

public sealed record LibraryRevealPlan(ImmutableList<ObjectId> Revealed, ObjectId? Hit, ImmutableList<ObjectId> Rest);

/// <summary>Read a library from the top through its first match, without changing its contents or order.</summary>
[RuleRef("701.20")]
public sealed class LibraryRevealPlanner(IQuery query)
{
    public LibraryRevealPlan Plan(GameState state, PlayerId owner, ObjectFilter until, int? maximum, EffectContext context)
    {
        var library = state.In(ZoneKey.Of(Zone.Library, owner));
        var limit = maximum is { } count ? Math.Max(0, count) : library.Count;
        var filter = until.In is null ? until with { In = Zone.Library } : until;
        var rest = ImmutableList<ObjectId>.Empty;
        foreach (var id in library.Take(limit))
        {
            if (query.Matches(state, filter, id, context))
                return new LibraryRevealPlan(rest.Add(id), id, rest);
            rest = rest.Add(id);
        }
        return new LibraryRevealPlan(rest, null, rest);
    }
}
