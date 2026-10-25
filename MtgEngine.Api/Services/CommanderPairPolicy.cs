using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Services;

/// <summary>Compatibility of the two designated commanders under CR 702.124.</summary>
public static class CommanderPairPolicy
{
    public static bool CanPair(CardDefinition first, CardDefinition second)
    {
        if (first.OracleId == second.OracleId || !CommanderRules.IsLegalInCommander(first)
            || !CommanderRules.IsLegalInCommander(second)
            || !first.Supertypes.Contains("Legendary") || !second.Supertypes.Contains("Legendary"))
            return false;
        var left = Abilities(first);
        var right = Abilities(second);
        if (left.Contains("Partner") && right.Contains("Partner"))
            return true;
        if (left.Any(ability => ability.StartsWith("Partner—", StringComparison.OrdinalIgnoreCase) && right.Contains(ability)))
            return true;
        if (left.Contains($"Partner with {second.Name}") && right.Contains($"Partner with {first.Name}"))
            return true;
        return Background(first, left, second) || Background(second, right, first)
            || Doctor(first, left, second) || Doctor(second, right, first);
    }

    private static bool Background(CardDefinition leader, HashSet<string> abilities, CardDefinition background) =>
        CommanderRules.IsCommanderEligible(leader) && abilities.Contains("Choose a Background")
        && background.IsEnchantment && background.Subtypes.Contains("Background");

    private static bool Doctor(CardDefinition companion, HashSet<string> abilities, CardDefinition doctor) =>
        companion.IsCreature && doctor.IsCreature && abilities.Contains("Doctor's companion")
        && doctor.Subtypes.Count == 2 && doctor.Subtypes.Contains("Time Lord") && doctor.Subtypes.Contains("Doctor");

    private static HashSet<string> Abilities(CardDefinition card) => card.OracleText
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split('(')[0].Trim().TrimEnd('.').Replace('’', '\''))
        .Select(line => string.Equals(line, "Friends forever", StringComparison.OrdinalIgnoreCase) ? "Partner—Friends forever" : line)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
