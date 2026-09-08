namespace MtgEngine.Cards;

/// <summary>
/// The wave-23 records, and the four places their halves can contradict each other: a secret number
/// choice and the extremes read back from it, a copy of a printed card whose name is the only data
/// in this vocabulary, a spell described by what it targets, and a cast-from-zone restriction on a
/// spell filter. Each check names the rule it reads, as <c>Linter.cs</c> does.
/// </summary>
public static partial class Linter
{
    private static void CheckWave23(IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        CheckSecretNumbers(nodes, path, findings);

        // CR 707.9 with 613.1a: the copiable values come from the card the name names, so an empty
        // name copies nothing at all. The name is data, and this is the one check that can be made
        // here — that it is a real card is the pipeline's, against the corpus.
        foreach (var copy in nodes.OfType<BecomesCopyOfNamed>().Where(c => string.IsNullOrWhiteSpace(c.Name)))
            findings.Add(new(LintSeverity.Error, "L036", "BecomesCopyOfNamed has an empty Name; the copiable values come from the card the name names (CR 707.9, 613.1a).", path));

        CheckTargetingSpells(nodes, path, findings);
        CheckCastFromZones(nodes, path, findings);
    }

    /// <summary>
    /// A secret simultaneous choice, CR 700.2 with 101.4. The step that asks for the numbers and the
    /// amounts that read them back are the two halves of one printed line: an extreme read from a key
    /// nothing asked for reads nothing, and numbers nobody reads back are a dropped sentence.
    /// </summary>
    private static void CheckSecretNumbers(IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        var asked = nodes.OfType<ChooseNumberSecretly>().Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var read = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var key = node switch
            {
                HighestChosen h => h.Key,
                LowestChosen l => l.Key,
                PlayerWhoChose p => p.Key,
                _ => null,
            };
            if (key is null)
                continue;
            read.Add(key);
            if (!asked.Contains(key))
                findings.Add(new(LintSeverity.Error, "L035", $"'{key}' is read as a chosen number but no ChooseNumberSecretly on this ability asks for it (CR 700.2, 101.4).", path));
        }

        foreach (var choice in nodes.OfType<ChooseNumberSecretly>().Where(c => !read.Contains(c.Key)))
            findings.Add(new(LintSeverity.Error, "L035", $"ChooseNumberSecretly asks for '{choice.Key}' and nothing reads it back; the numbers are revealed to be used (CR 700.2).", path));
    }

    /// <summary>
    /// "Target spell that targets a permanent you control", CR 115.1. Only an object on the stack
    /// chooses targets, so a filter that says this of a permanent describes nothing at all.
    /// </summary>
    private static void CheckTargetingSpells(IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        foreach (var filter in nodes.OfType<ObjectFilter>())
        {
            if (!Mentions(filter.Also, p => p is Targets))
                continue;
            if (filter.Spell == false || filter.Permanent == true || filter.In is Zone.Battlefield)
                findings.Add(new(LintSeverity.Error, "L037", "A filter carries a Targets predicate and describes a permanent; only a spell or ability on the stack has chosen targets (CR 115.1, 601.2c).", path));
        }
    }

    /// <summary>
    /// A per-spell cast-from-zone restriction, CR 601.3. The zone a card allows and the zone it
    /// forbids are the two readings of one printed line, and a filter that sets both admits nothing.
    /// </summary>
    private static void CheckCastFromZones(IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        foreach (var filter in nodes.OfType<ObjectFilter>())
        {
            if (filter.CastFrom is not null && filter.NotCastFrom is not null)
                findings.Add(new(LintSeverity.Error, "L038", "A filter sets CastFrom and NotCastFrom together; a card names the zone it allows or the one it forbids, and both together admits no spell (CR 601.3).", path));
            if ((filter.CastFrom is not null || filter.NotCastFrom is not null) && (filter.Spell == false || filter.Permanent == true))
                findings.Add(new(LintSeverity.Error, "L038", "A filter restricts the zone a spell was cast from and describes a permanent; the zone a spell was cast from is a property of the casting (CR 601.3).", path));
        }
    }

    /// <summary>Whether a predicate tree holds one the test admits, through the AllOf, AnyOf and Not wrappers.</summary>
    private static bool Mentions(Predicate? predicate, Func<Predicate, bool> test) => predicate switch
    {
        null => false,
        AllOf all => all.Of.Any(p => Mentions(p, test)),
        AnyOf any => any.Of.Any(p => Mentions(p, test)),
        NotPredicate not => Mentions(not.Of, test),
        _ => test(predicate),
    };
}
