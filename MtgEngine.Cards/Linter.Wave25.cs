namespace MtgEngine.Cards;

/// <summary>
/// The wave-25 records, and the four places their halves can contradict each other or say nothing:
/// an amount read from a triggering event that never happened, two exceptions on one attack
/// restriction, a token attacking nobody, an existential opponent used as somebody to act on, and a
/// timing permission for spells granted over cards that are played rather than cast. Each check
/// names the rule it reads, as <c>Linter.cs</c> does.
/// </summary>
public static partial class Linter
{
    /// <summary>Life-gain-shaped triggers: the events <see cref="TriggeringLifeGained"/> can read an amount from.</summary>
    private static bool IsLifeGainTrigger(object node) => node is GainsLife;

    /// <summary>Life-loss-shaped triggers: the events <see cref="TriggeringLifeLost"/> can read an amount from.</summary>
    private static bool IsLifeLossTrigger(object node) => node is LosesLife;

    private static void CheckWave25(Ability ability, IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        CheckTriggeringLife(ability, nodes, path, findings);

        // CR 508.1h: the tax is a cost paid while attackers are declared and the board exception is
        // a description of the creature. A card prints one or the other; both on one restriction
        // would let an attack through by satisfying either, which no printed card says.
        foreach (var restriction in nodes.OfType<CantAttack>().Where(c => c.Unless is not null && c.UnlessPays is not null))
            findings.Add(new(LintSeverity.Error, "L040", "CantAttack sets both Unless and UnlessPays; the board exception and the attack tax are two different printed lines (CR 508.1h).", path));

        // CR 508.4: the defending player is chosen as the token enters attacking. A token that is
        // not attacking is attacking nobody, so the player names a choice that is never made.
        foreach (var token in nodes.OfType<CreateToken>().Where(t => t.AttackingWhom is not null && !t.Attacking))
            findings.Add(new(LintSeverity.Error, "L040", "CreateToken names AttackingWhom without Attacking; only a token put onto the battlefield attacking chooses a defending player (CR 508.4).", path));

        CheckExistentialOpponent(nodes, path, findings);

        // CR 305.1 with 702.8: a land is played, never cast, so a permission to cast "as though it
        // had flash" over a filter that admits only lands grants nothing at all.
        foreach (var flash in nodes.OfType<MayCastAsThoughFlash>().Where(f => f.What is { Types: CardType.Land }))
            findings.Add(new(LintSeverity.Error, "L042", "MayCastAsThoughFlash over a land-only filter grants nothing; lands are played and not cast (CR 305.1, 702.8).", path));
    }

    /// <summary>
    /// "You gain that much life", "that much": the amount is the triggering event's own information
    /// (CR 603.10), so the ability — or a delayed trigger it creates — has to trigger on the event
    /// that carries it. Life gained and life lost are two different events, and an amount written
    /// for the other one reads nothing.
    /// </summary>
    private static void CheckTriggeringLife(Ability ability, IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        if (nodes.OfType<TriggeringLifeGained>().Any() && !TriggersOn(ability, nodes, IsLifeGainTrigger))
            findings.Add(new(LintSeverity.Error, "L039", "TriggeringLifeGained reads the life of the event that triggered this ability and nothing here triggers on life being gained (CR 603.10).", path));

        if (nodes.OfType<TriggeringLifeLost>().Any() && !TriggersOn(ability, nodes, IsLifeLossTrigger))
            findings.Add(new(LintSeverity.Error, "L039", "TriggeringLifeLost reads the life of the event that triggered this ability and nothing here triggers on life being lost (CR 603.10).", path));
    }

    /// <summary>Whether this ability, or a delayed trigger it creates, watches an event of the given shape.</summary>
    private static bool TriggersOn(Ability ability, IReadOnlyList<object> nodes, Func<object, bool> shape) =>
        (ability is TriggeredAbility triggered && Walk(triggered.When).Any(shape))
        || nodes.OfType<DelayedTrigger>().Any(d => Walk(d.When).Any(shape));

    /// <summary>
    /// <see cref="AnOpponent"/>, CR 102.2, is "some opponent" — a reading of the game and not a
    /// player anything can be done to. It names nobody in particular, so an instruction aimed at it
    /// has no recipient and an amount read off it has no number; only a condition can ask whether
    /// some opponent satisfies something. A card that acts on one names them with ChoosePlayer or
    /// TargetPlayer, and one that acts on all of them says EachOpponent.
    /// </summary>
    private static void CheckExistentialOpponent(IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        foreach (var node in nodes)
        {
            if (node is Condition or AnOpponent)
                continue;

            foreach (var prop in node.GetType().GetProperties())
            {
                if (typeof(PlayerRef).IsAssignableFrom(prop.PropertyType) && prop.GetValue(node) is AnOpponent)
                    findings.Add(new(LintSeverity.Error, "L041", $"{node.GetType().Name}.{prop.Name} is AnOpponent, which names some opponent rather than one to act on (CR 102.2); use ChoosePlayer, TargetPlayer or EachOpponent, and keep AnOpponent inside a condition.", path));
            }
        }
    }
}
