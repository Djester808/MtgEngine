using System.Globalization;
using MtgEngine.Rules.Cards;

namespace MtgEngine.Api.Tests;

/// <summary>Temporary measurement harness for round twenty-one's static-shield fix.</summary>
public sealed partial class ZzR2130Census
{
    [Fact]
    public void Census()
    {
        var into = Environment.GetEnvironmentVariable("R2130_CENSUS");
        if (string.IsNullOrWhiteSpace(into))
            return;

        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
            return;

        var lines = new List<string>();
        int shields = 0, filtered = 0, scoped = 0, bloodthirst = 0, entersUnless = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (!compiled.IsComplete)
                continue;

            var ids = compiled.Replacements.Select(r => r.Id).ToList();
            var mine = ids.Where(i => i.StartsWith("static-prevention:", StringComparison.Ordinal))
                .ToList();

            if (mine.Count > 0)
            {
                shields++;

                var anyFilter = mine.Any(HasFilter);
                var anyScope = mine.Any(i => i.Contains(':', StringComparison.Ordinal)
                    && (i.Contains("=self:", StringComparison.Ordinal)
                        || Scoped(i)));

                if (anyFilter)
                    filtered++;
                if (anyScope)
                    scoped++;

                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"SHIELD\t{card.Name}\tfilter={anyFilter}\tscope={anyScope}\t{string.Join(" ; ", mine)}"));
            }

            if (ids.Contains("bloodthirst", StringComparer.Ordinal))
            {
                bloodthirst++;
                lines.Add($"BLOODTHIRST\t{card.Name}");
            }

            if (ids.Contains("enters-tapped", StringComparer.Ordinal))
            {
                var self = card.OracleText.Replace(card.Name, "~", StringComparison.Ordinal);

                foreach (var raw in self.Split('\n'))
                {
                    var hit = EntersTappedUnless().Match(raw.Trim());
                    if (!hit.Success || !hit.Groups["unless"].Success)
                        continue;

                    entersUnless++;
                    lines.Add(
                        $"ENTERS-UNLESS\t{card.Name}\t{hit.Groups["unless"].Value}");
                }
            }
        }

        lines.Sort(StringComparer.Ordinal);
        lines.Insert(0, string.Create(
            CultureInfo.InvariantCulture,
            $"#shields={shields} filtered={filtered} scoped={scoped} bloodthirst={bloodthirst} entersUnless={entersUnless}"));
        File.WriteAllLines(into, lines);
    }

    /// <summary>Whether either half of the id names a SearchFilters id rather than an anchor.</summary>
    private static bool HasFilter(string id)
    {
        foreach (var part in id.Split(':'))
        {
            // Anchors and the two "everything" words are not filters.
            if (part is "self" or "host" or "all" or "any" or "static-prevention"
                or "combat" or "noncombat" or "any-damage")
            {
                continue;
            }

            if (part.StartsWith("to=", StringComparison.Ordinal)
                || part.StartsWith("by=", StringComparison.Ordinal))
            {
                var value = part[3..];
                if (value is not ("self" or "host" or "all" or "any"))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Whether the id carries a player scope, which only ControllerOf can answer.</summary>
    private static bool Scoped(string id) =>
        id.Contains(":You", StringComparison.Ordinal)
        || id.Contains(":Opponents", StringComparison.Ordinal)
        || id.Contains(":Each", StringComparison.Ordinal)
        || id.Contains(":Controller", StringComparison.Ordinal)
        || id.Contains(":Owner", StringComparison.Ordinal);

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^~ enters tapped( unless (?<unless>.+?))?\.?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex EntersTappedUnless();

    [Fact]
    public void Probe()
    {
        if (Environment.GetEnvironmentVariable("R2130_PROBE") is null)
            return;

        (string Name, string Text, Domain.Enums.CardType Types, int? P, int? T)[] cards =
        [
            ("P1", "Prevent all damage that would be dealt to ~ by creatures with first strike.", Domain.Enums.CardType.Creature, 2, 2),
            ("P2", "Prevent all damage that would be dealt to ~ by creatures with power 3 or greater.", Domain.Enums.CardType.Creature, 2, 2),
            ("P3", "Prevent all noncombat damage that would be dealt to creatures you control.", Domain.Enums.CardType.Creature, 2, 2),
            ("P4", "Creatures with flying get +1/+1.", Domain.Enums.CardType.Enchantment, null, null),
            ("P5", "Creatures you control with flying get +1/+1.", Domain.Enums.CardType.Enchantment, null, null),
            ("P6", "Enchant creature@Enchanted creature has first strike.", Domain.Enums.CardType.Enchantment, null, null),
            ("P7", "Gain control of target creature until end of turn. Untap that creature. It gains haste until end of turn.", Domain.Enums.CardType.Sorcery, null, null),
            ("P8", "Prevent all damage that would be dealt to ~ by creatures with power 4 or greater.", Domain.Enums.CardType.Creature, 0, 6),
            ("P9", "You control enchanted creature.", Domain.Enums.CardType.Enchantment, null, null),
            ("P10", "Enchant creature@You control enchanted creature.", Domain.Enums.CardType.Enchantment, null, null),
        ];

        foreach (var (name, raw, types, p, t) in cards)
        {
            var card = new Domain.Models.CardDefinition
            {
                OracleId = "probe-" + name,
                Name = name,
                OracleText = raw.Replace((char)64, (char)10),
                CardTypes = types,
                Power = p,
                Toughness = t,
                Subtypes = raw.Contains("Enchant creature", StringComparison.Ordinal) ? ["Aura"] : [],
            };

            var compiled = CardCompiler.Compile(card);
            Console.WriteLine(
                name + " complete=" + compiled.IsComplete
                + " unhandled=[" + string.Join(" | ", compiled.Unhandled) + "]"
                + " reps=[" + string.Join(" ; ", compiled.Replacements.Select(r => r.Id)) + "]"
                + " statics=[" + string.Join(" ; ", compiled.Statics.Select(s => s.Id)) + "]");
        }
    }
}
