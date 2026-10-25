using MtgEngine.Cards;

namespace MtgEngine.Rules.Library;

/// <summary>CR 701.23: quantity bounds and the hidden-zone exception to finding cards.</summary>
public static class LibrarySearchPolicy
{
    public static (int Minimum, int Maximum) Bounds(Search search, int requested,
        IEnumerable<(Zone Zone, int Matches)> zones)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(zones);
        var matches = zones.ToList();
        var maximum = Math.Max(0, requested);
        if (search.UpTo is not null)
            return (0, maximum);

        // Location describes where to search, not a quality of the cards being sought.
        var qualified = search.For with { In = null, InAnyOf = null } != ObjectFilter.Any;
        var mandatory = matches.Where(zone => !qualified || zone.Zone is not (Zone.Library or Zone.Hand))
            .Sum(zone => zone.Matches);
        return (Math.Min(mandatory, maximum), maximum);
    }
}
