using System.Globalization;
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

    [GeneratedRegex(
        @"\bmana value (?<n>\d+) or (?<dir>less|greater|more|fewer)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ManaValueBoundPhrase();

    [GeneratedRegex(
        @"\b(?<what>power|toughness|mana value)(?<verb> is| are)? (?<cmp>less than or equal to|greater than or equal to|less than|greater than) (?<n>\d+)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ComparisonPhrase();

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

    private static string AsPower(string body) => ManaValueBoundPhrase().Replace(
        body, m => "power " + m.Groups["n"].Value + " or " + m.Groups["dir"].Value);

    private static string Spelled(string body) => ComparisonPhrase().Replace(body, Spell);

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
        var subManaValue = 0;
        var subComparison = 0;
        var subEither = 0;

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

            var touchesMv = Bodies(card).Any(
                b => !string.Equals(AsPower(b), b, StringComparison.Ordinal));
            var mv = touchesMv && Completes(card, AsPower);
            if (mv)
                subManaValue++;

            var touchesCmp = Bodies(card).Any(
                b => !string.Equals(Spelled(b), b, StringComparison.Ordinal));
            var cmp = touchesCmp && Completes(card, Spelled);
            if (cmp)
                subComparison++;

            if ((touchesMv || touchesCmp) && Completes(card, b => AsPower(Spelled(b))))
            {
                subEither++;
                subNames.Add(
                    card.Name
                    + (mv ? " [mv]" : string.Empty)
                    + (cmp ? " [cmp]" : string.Empty));
            }
        }

        excisionNames.Sort(StringComparer.Ordinal);
        subNames.Sort(StringComparer.Ordinal);
        controlNames.Sort(StringComparer.Ordinal);

        File.WriteAllLines(Out("measure.txt"), new[]
        {
            "CORPUS: " + corpus.Count,
            "COMPLETE (baseline): " + complete,
            "POPULATION (prints a comparison filter/characteristic): " + population,
            "INCOMPLETE AND IN FAMILY: " + incompleteInFamily,
            "RUN 1 EXCISION (drop the family lines): " + excision,
            "RUN 2 LINE CONTROL (drop a different unread line, same cards): "
                + lineControl + " of " + lineControlEligible + " eligible",
            "RUN 3 SUBSTITUTION mana-value bound to power bound: " + subManaValue,
            "RUN 3 SUBSTITUTION comparison spelling to N-or-less: " + subComparison,
            "RUN 3 SUBSTITUTION both: " + subEither,
            string.Empty,
            "-- excision completions --",
        }.Concat(excisionNames)
         .Concat(new[] { string.Empty, "-- substitution completions --" })
         .Concat(subNames)
         .Concat(new[] { string.Empty, "-- line-control completions --" })
         .Concat(controlNames));
    }

    private static string Spell(Match m)
    {
        var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        var head = m.Groups["what"].Value + m.Groups["verb"].Value + " ";

        return m.Groups["cmp"].Value.ToLowerInvariant() switch
        {
            "less than or equal to" =>
                head + n.ToString(CultureInfo.InvariantCulture) + " or less",
            "greater than or equal to" =>
                head + n.ToString(CultureInfo.InvariantCulture) + " or greater",
            "less than" when n >= 1 =>
                head + (n - 1).ToString(CultureInfo.InvariantCulture) + " or less",
            "greater than" =>
                head + (n + 1).ToString(CultureInfo.InvariantCulture) + " or greater",
            _ => m.Value,
        };
    }
}
