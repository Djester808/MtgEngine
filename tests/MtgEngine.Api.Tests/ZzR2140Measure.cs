using System.Text.RegularExpressions;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;

namespace MtgEngine.Api.Tests;

public sealed partial class ZzR2140Measure
{
    private const char Nl = (char)10;

    private static string Out(string name)
    {
        var dir = Environment.GetEnvironmentVariable("R2140_OUT") ?? ".";
        return Path.Combine(dir, name);
    }

    [GeneratedRegex(
        @"\b(power|toughness|mana value)\b[^.]{0,8}\b((\d+|one|two|three|four|five|six|seven|eight|nine|ten|X) or (less|greater|more|fewer)|(less|greater) than( or equal to)?)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex FamilyLine();

    /// <summary>The comparison, up to the first character of the quantity it measures.</summary>
    [GeneratedRegex(
        @"(?<what>power|toughness|mana value)(?<verb> is| are)? (?<dir>less|greater) than or equal to (?=\S)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ComparisonHead();

    private static bool InFamily(string line) => FamilyLine().IsMatch(line);

    private static CardDefinition With(CardDefinition card, Func<string, string> rewrite) =>
        new()
        {
            OracleId = card.OracleId,
            Name = card.Name,
            ManaCost = card.ManaCost,
            ManaCostRaw = card.ManaCostRaw,
            Cmc = card.Cmc,
            CardTypes = card.CardTypes,
            Subtypes = card.Subtypes,
            Supertypes = card.Supertypes,
            OracleText = rewrite(card.OracleText),
            Power = card.Power,
            Toughness = card.Toughness,
            StartingLoyalty = card.StartingLoyalty,
            Defense = card.Defense,
            Keywords = card.Keywords,
            ColorIdentity = card.ColorIdentity,
            Colors = card.Colors,
            Faces = [.. card.Faces.Select(f => f with { OracleText = rewrite(f.OracleText) })],
            Legalities = card.Legalities,
        };

    private static bool Completes(CardDefinition card, Func<string, string> rewrite) =>
        CardCompiler.Compile(With(card, rewrite)).IsComplete;

    private static IEnumerable<string> Bodies(CardDefinition card)
    {
        yield return card.OracleText;
        foreach (var face in card.Faces)
            yield return face.OracleText;
    }

    private static Func<string, string> Dropping(string line) =>
        body => string.Join(
            Nl,
            body.Split(Nl).Where(l => !string.Equals(l, line, StringComparison.Ordinal)));

    private static Func<string, string> DroppingFamily() =>
        body => string.Join(Nl, body.Split(Nl).Where(l => !InFamily(l)));

    /// <summary>
    /// The card with one comparison rewritten as a printed bound, cutting the quantity it
    /// measures at <paramref name="take"/> characters.
    /// </summary>
    /// <remarks>
    /// Where the counted phrase ends is exactly what the compiler has to work out, so the control
    /// tries every cut and asks whether <em>any</em> of them completes the card. That is the
    /// honest ceiling for this family: it says the comparison is the only thing in the way,
    /// without the measurement having to guess the boundary the reader is for.
    /// </remarks>
    private static Func<string, string> AsPrintedBound(int take) => body =>
    {
        var m = ComparisonHead().Match(body);
        if (!m.Success)
            return body;

        var rest = body[(m.Index + m.Length)..];
        if (take > rest.Length)
            return body;

        var direction = m.Groups["dir"].Value.Equals("greater", StringComparison.OrdinalIgnoreCase)
            ? " 3 or greater"
            : " 3 or less";

        return body[..m.Index]
            + m.Groups["what"].Value
            + m.Groups["verb"].Value
            + direction
            + rest[take..];
    };

    [Fact]
    public void Excision_and_two_controls()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        Assert.NotNull(corpus);

        var complete = 0;
        var population = 0;
        var incompleteInFamily = 0;
        var excision = 0;
        var lineControlEligible = 0;
        var lineControl = 0;
        var substitutionEligible = 0;
        var substitution = 0;

        var excisionNames = new List<string>();
        var subNames = new List<string>();
        var controlNames = new List<string>();

        foreach (var card in corpus)
        {
            var lines = Bodies(card).SelectMany(b => b.Split(Nl)).ToList();
            var familyLines = lines.Where(InFamily).ToList();

            if (familyLines.Count > 0)
                population++;

            if (CardCompiler.Compile(card).IsComplete)
            {
                complete++;
                continue;
            }

            if (familyLines.Count == 0)
                continue;

            incompleteInFamily++;

            if (Completes(card, DroppingFamily()))
            {
                excision++;
                excisionNames.Add(card.Name);
            }

            // A line counts as unread when dropping it takes an entry out of Unhandled.
            var baseline = CardCompiler.Compile(card).Unhandled.Count;
            string? unreadOther = null;

            foreach (var other in lines.Where(l => !InFamily(l) && l.Trim().Length > 0))
            {
                if (CardCompiler.Compile(With(card, Dropping(other))).Unhandled.Count < baseline)
                {
                    unreadOther = other;
                    break;
                }
            }

            if (unreadOther is not null)
            {
                lineControlEligible++;

                if (Completes(card, Dropping(unreadOther)))
                {
                    lineControl++;
                    controlNames.Add(card.Name);
                }
            }

            if (!Bodies(card).Any(b => ComparisonHead().IsMatch(b)))
                continue;

            substitutionEligible++;

            var longest = Bodies(card).Max(b => b.Length);
            for (var take = 1; take <= longest; take++)
            {
                if (!Completes(card, AsPrintedBound(take)))
                    continue;

                substitution++;
                subNames.Add(card.Name + " [cut " + take + "]");
                break;
            }
        }

        excisionNames.Sort(StringComparer.Ordinal);
        subNames.Sort(StringComparer.Ordinal);
        controlNames.Sort(StringComparer.Ordinal);

        File.WriteAllLines(Out("measure.txt"), new[]
        {
            "CORPUS: " + corpus.Count,
            "COMPLETE (this binary): " + complete,
            "POPULATION (prints a comparison filter/characteristic): " + population,
            "INCOMPLETE AND IN FAMILY: " + incompleteInFamily,
            "RUN 1 EXCISION (drop the family lines): " + excision,
            "RUN 2 LINE CONTROL (drop a different unread line, same cards): "
                + lineControl + " of " + lineControlEligible + " eligible",
            "RUN 3 SUBSTITUTION (comparison rewritten as a printed bound, any cut): "
                + substitution + " of " + substitutionEligible + " eligible",
            string.Empty,
            "-- excision completions --",
        }.Concat(excisionNames)
         .Concat(new[] { string.Empty, "-- substitution completions --" })
         .Concat(subNames)
         .Concat(new[] { string.Empty, "-- line-control completions --" })
         .Concat(controlNames));
    }
}
