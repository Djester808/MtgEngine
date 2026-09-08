namespace MtgEngine.Cards;

/// <summary>
/// The wave-30 checks: the places where the records slice 010 asked for can be written two ways or
/// with two halves that contradict each other. Each names the rule it reads, as <c>Linter.cs</c> does.
/// </summary>
public static partial class Linter
{
    private static void CheckWave30(IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        // CR 400.2: the amount is read once per player, so it has to name the player being read.
        // EachPlayer is that binding; an amount that names a fixed player compares one player's
        // count with itself for every other player and answers the same thing every time.
        foreach (var more in nodes.OfType<MoreThanEachOtherPlayer>())
        {
            if (!Walk(more.Of).OfType<EachPlayer>().Any())
                findings.Add(new(LintSeverity.Error, "L050", "MoreThanEachOtherPlayer reads Of once for each player, so the amount must name EachPlayer as the player it is about (CR 400.2).", path));
        }

        // CR 119.3 with 119.1: "can't lose life" allows gaining, so the two are different
        // prohibitions and a card that prints one has not printed the other.
        foreach (var cant in nodes.OfType<PlayerCant>())
        {
            if (cant.What.HasFlag(PlayerRestriction.LoseLife) && cant.What.HasFlag(PlayerRestriction.LifeTotalChanges))
                findings.Add(new(LintSeverity.Error, "L050", "PlayerCant sets both LoseLife and LifeTotalChanges; LifeTotalChanges already forbids every change, and a card that prints 'can't lose life' still allows life gain (CR 119.3).", path));
        }
    }

    /// <summary>
    /// A face's printed alternative costs again, this time the free cast: CR 118.9 names "you may
    /// cast this spell without paying its mana cost" as an alternative cost, and CR 118.9c says an
    /// alternative cost never changes the spell's mana cost — so the free cast is the flag and never
    /// a cost of zero.
    /// </summary>
    private static void CheckWave30Faces(Face face, string path, List<LintFinding> findings)
    {
        var alternatives = face.AlternativeCosts ?? [];
        for (var i = 0; i < alternatives.Count; i++)
        {
            var alternative = alternatives[i];
            var where = $"{path}.alternativeCosts[{i}]";

            if (alternative.WithoutPayingManaCost && alternative.Cost is not Costs { All.Count: 0 })
                findings.Add(new(LintSeverity.Error, "L050", "An AlternativeCost with WithoutPayingManaCost asks for nothing else; its cost is an empty Costs and nothing more (CR 118.9).", where));

            if (!alternative.WithoutPayingManaCost && IsFreeManaCost(alternative.Cost))
                findings.Add(new(LintSeverity.Error, "L050", "An AlternativeCost of {0} is the free cast written as a cost that is paid; set WithoutPayingManaCost instead, because an alternative cost never changes the spell's mana cost (CR 118.9c).", where));
        }
    }

    /// <summary>A printed mana cost of nothing, in either of the two ways it can be written.</summary>
    private static bool IsFreeManaCost(Cost cost) => cost switch
    {
        ManaCost { Mana: "{0}" or "" } => true,
        Costs { All.Count: 1 } all => IsFreeManaCost(all.All[0]),
        _ => false,
    };
}
