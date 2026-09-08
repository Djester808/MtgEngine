namespace MtgEngine.Cards;

/// <summary>
/// The wave-26 records, and the places their halves can contradict each other or say nothing: an
/// amount read from a prevention that nothing here watches, an amount read from a cast nothing here
/// watches, a draw replacement that both keeps and refuses the draw step's own draw, an ordering for
/// a zone nothing is being put into, two counts on one counter move, and a "second spell each turn"
/// that counts from nowhere. Each check names the rule it reads, as <c>Linter.cs</c> does.
/// </summary>
public static partial class Linter
{
    /// <summary>Prevention-shaped triggers: the events <see cref="TriggeringPreventedDamage"/> can read an amount from.</summary>
    private static bool IsPreventionTrigger(object node) => node is DamagePrevented;

    /// <summary>Cast-shaped triggers: the events <see cref="ManaSpentOnTriggering"/> can read a payment from.</summary>
    private static bool IsSpellCastTrigger(object node) => node is SpellCast;

    private static void CheckWave26(Ability ability, IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        // CR 615.13 with 603.10: "that much" is the amount the prevention prevented, so the ability
        // — or a delayed trigger it creates — has to watch a prevention being applied.
        if (nodes.OfType<TriggeringPreventedDamage>().Any() && !TriggersOn(ability, nodes, IsPreventionTrigger))
            findings.Add(new(LintSeverity.Error, "L043", "TriggeringPreventedDamage reads the damage a prevention effect prevented and nothing here triggers on damage being prevented (CR 615.13).", path));

        // CR 400.7d: the mana spent on a spell is the triggering spell's own information, so there
        // has to be a triggering spell.
        if (nodes.OfType<ManaSpentOnTriggering>().Any() && !TriggersOn(ability, nodes, IsSpellCastTrigger))
            findings.Add(new(LintSeverity.Error, "L043", "ManaSpentOnTriggering reads what was paid for the spell that triggered this ability and nothing here triggers on a spell being cast (CR 400.7d).", path));

        // CR 121.1: one admits the first draw of the turn alone and the other admits every draw but
        // the draw step's own, so a replacement that sets both replaces two different draws.
        foreach (var draw in nodes.OfType<WouldDraw>().Where(d => d.ExceptFirstInDrawStep && d.FirstEachTurn is not null))
            findings.Add(new(LintSeverity.Error, "L043", "WouldDraw sets FirstEachTurn and ExceptFirstInDrawStep; one names the turn's first draw and the other refuses the draw step's own (CR 121.1).", path));

        // CR 701.6a: the placement orders the zone the countered spell is put into, and a countered
        // spell with no instead-zone goes to its owner's graveyard with nothing to order.
        foreach (var counter in nodes.OfType<Counter>().Where(c => c.Where != Placement.Default && c.PutInto is null))
            findings.Add(new(LintSeverity.Error, "L043", "Counter names a Placement without a PutInto zone; there is nothing to order (CR 701.6a).", path));

        // CR 122.5: a printed count, "all of them" and "any number" are three different lines, and
        // two of them on one move say two different numbers of counters.
        foreach (var move in nodes.OfType<MoveCounters>().Where(m => CountedWays(m) > 1))
            findings.Add(new(LintSeverity.Error, "L044", "MoveCounters sets more than one of N, All and AnyNumber; a printed count, all of them and a free choice are three different lines (CR 122.5).", path));

        // CR 603.2: "their second spell each turn" is the Nth cast, counted from one.
        foreach (var cast in nodes.OfType<SpellCast>().Where(s => s.Nth is <= 0))
            findings.Add(new(LintSeverity.Error, "L044", "SpellCast counts an Nth spell of zero or less; the count is the spells cast this turn, from one (CR 603.2).", path));

        // CR 615.12 with 615.12a: unpreventable damage is a property of the damage event, and the
        // rider on one instruction is DealDamage.CantBePrevented. An ability that says both is
        // saying the same thing twice about its own damage and once about everybody else's, which no
        // printed card does — and the reader would have to decide which one scopes the effect.
        if (nodes.OfType<DamageCantBePrevented>().Any() && nodes.OfType<DealDamage>().Any(d => d.CantBePrevented))
            findings.Add(new(LintSeverity.Error, "L044", "One ability carries DamageCantBePrevented and a DealDamage that can't be prevented; the board-wide effect and the per-instruction rider are two different printed lines (CR 615.12).", path));
    }

    /// <summary>How many of a counter move's three mutually exclusive counts are set (CR 122.5).</summary>
    private static int CountedWays(MoveCounters move) =>
        (move.N is not null ? 1 : 0) + (move.All ? 1 : 0) + (move.AnyNumber ? 1 : 0);
}
