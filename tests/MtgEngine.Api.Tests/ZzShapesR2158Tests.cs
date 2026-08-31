using System.Text.RegularExpressions;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>Three measurements per shape: excision, line control, substitution.</summary>
public sealed class ZzShapesR2158Tests(ITestOutputHelper output)
{
    private static readonly (string Name, string Match, Func<string, string> Substitute)[] Shapes =
    [
        ("cast-ban",
            @"^(You|Your opponents|Players|Each player) can't cast [^.]*\.$",
            _ => "Your opponents can't cast spells with the chosen name."),

        ("cast-ban-window",
            @"^During your turn, your opponents can't cast [^.]*\.$",
            _ => "Your opponents can't cast spells with the chosen name."),

        ("play-lands-from-graveyard",
            @"^You may play lands from your graveyard\.$",
            _ => "You may play lands from the top of your library."),

        ("extra-land-scoped",
            @"^Each player may play an additional land on each of their turns\.$",
            _ => "You may play an additional land on each of your turns."),

        ("cant-play-lands",
            @"^(Players|You) can't play lands\.$|^Target player can't play lands this turn\.$",
            _ => "Your opponents can't cast spells with the chosen name."),

        ("cost-reduce-condition",
            @"^~ costs \{\d+\} less to cast if (?!it targets)[^.]*\.$",
            l => Regex.Replace(l, @"if [^.]*\.", "if you control a creature.")),

        ("cost-reduce-leading-if",
            @"^If [^,]+, ~ costs \{\d+\} less to cast\.$",
            l => Regex.Replace(
                l,
                @"^If [^,]+, (~ costs \{\d+\} less to cast)\.$",
                "$1 if you control a creature.")),

        ("cost-reduce-for-each",
            @"^~ costs \{\d+\} less to cast for each [^.]*\.$",
            l => Regex.Replace(l, @"for each [^.]*\.", "for each creature you control.")),

        ("cost-reduce-ordinal",
            @"^The (first|second|third) [a-z ]*spell you cast each turn costs \{\d+\} less to cast\.$",
            _ => "Creature spells you cast cost {1} less to cast."),

        ("cost-modifier-keyword-ability",
            @"^[A-Z][a-z]+ abilities you activate[^.]* cost \{\d+\} (less|more) to activate\.$",
            _ => "Activated abilities of creatures you control cost {1} less to activate."),

        ("self-flash-rider",
            @"^You may cast ~ as though it had flash\. If you cast it any time [^$]*$",
            l => l[..(l.IndexOf(". If you cast it", StringComparison.Ordinal) + 1)]),

        ("self-flash-conditional",
            @"^(If|As long as) [^,]+, you may cast ~ as though it had flash\.$",
            _ => "You may cast ~ as though it had flash."),

        ("spirit-guide",
            @"^Exile ~ from your hand: Add \{[^}]+\}\.$",
            l => Regex.Replace(l, @"^Exile ~ from your hand:", "{T}:")),

        ("restricted-mana-filter",
            @"Spend this mana only to cast [^.]*\.",
            l => Regex.Replace(
                l,
                @"Spend this mana only to cast [^.]*\.",
                "Spend this mana only to cast creature spells.")),

        ("dont-untap-filter",
            @"^[A-Z][^.]* don't untap during their controllers' untap steps\.$",
            _ => "Creatures your opponents control enter tapped."),
    ];

    [Fact]
    public void Probe()
    {
        string[] probes =
        [
            "You may play lands from the top of your library.",
            "You may play an additional land on each of your turns.",
            "Your opponents can't cast spells with the chosen name.",
            "~ costs {1} less to cast if you control a creature.",
            "~ costs {1} less to cast for each creature you control.",
            "Creature spells you cast cost {1} less to cast.",
            "Activated abilities of creatures you control cost {1} less to activate.",
            "You may cast ~ as though it had flash.",
            "{T}: Add {R}.",
            "{T}: Add {R}. Spend this mana only to cast creature spells.",
            "Creatures your opponents control enter tapped.",
            "You can't cast creature spells.",
            "You can't cast noncreature spells.",
            "Your opponents can't cast blue creature spells.",
            "Your opponents can't cast spells during your turn.",
            "Players can't cast spells during combat.",
            "During your turn, your opponents can't cast spells or activate abilities of artifacts or creatures or enchantments.",
            "Your opponents can't cast spells with the chosen name.",
            "The second spell you cast each turn costs {1} less to cast.",
            "The first creature spell you cast each turn costs {2} less to cast.",
            "The first instant or sorcery spell you cast each turn costs {3} less to cast.",
            "The first legendary creature spell you cast each turn costs {2} less to cast.",
        ];

        foreach (var probe in probes)
        {
            var card = Card("Probe " + probe.GetHashCode(StringComparison.Ordinal), probe);
            var c = CardCompiler.Compile(card);
            output.WriteLine(
                $"{(c.IsComplete ? "READ  " : "UNREAD")}  {probe}"
                    + (c.IsComplete ? string.Empty : "   << " + string.Join(" | ", c.Unhandled)));
        }
    }

    [Fact]
    public void Measure()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var compiled = corpus
            .Select(card => (Card: card, Result: CardCompiler.Compile(card)))
            .ToList();

        output.WriteLine($"corpus {corpus.Count}, complete {compiled.Count(c => c.Result.IsComplete)}");
        output.WriteLine(string.Empty);

        foreach (var (name, pattern, substitute) in Shapes)
        {
            var re = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
            var carriers = compiled
                .Where(c => !c.Result.IsComplete && c.Result.Unhandled.Any(l => re.IsMatch(l)))
                .ToList();

            var printed = compiled.Count(c => CardCompiler.Lines(c.Card).Any(l => re.IsMatch(l)));
            var already = compiled.Count(
                c => c.Result.IsComplete && CardCompiler.Lines(c.Card).Any(l => re.IsMatch(l)));

            var excised = 0;
            var substituted = new List<string>();
            var controlPool = 0;
            var controlWon = 0;

            foreach (var (card, result) in carriers)
            {
                var mine = result.Unhandled.Where(l => re.IsMatch(l)).ToList();
                var whole = CardCompiler.Lines(card).ToList();

                var cut = string.Join(
                    "\n", whole.Where(l => !mine.Contains(l, StringComparer.Ordinal)));
                if (CardCompiler.Compile(Rewritten(card, cut)).IsComplete)
                    excised++;

                var swapped = string.Join(
                    "\n",
                    whole.Select(l => mine.Contains(l, StringComparer.Ordinal) ? substitute(l) : l));
                if (CardCompiler.Compile(Rewritten(card, swapped)).IsComplete)
                    substituted.Add(card.Name);

                var others = result.Unhandled
                    .Where(l => !mine.Contains(l, StringComparer.Ordinal))
                    .ToList();

                if (others.Count == 0)
                    continue;

                controlPool++;
                foreach (var other in others)
                {
                    var dropped = string.Join(
                        "\n", whole.Where(l => !string.Equals(l, other, StringComparison.Ordinal)));

                    if (CardCompiler.Compile(Rewritten(card, dropped)).IsComplete)
                    {
                        controlWon++;
                        break;
                    }
                }
            }

            output.WriteLine(
                $"== {name}: printed {printed}, already complete {already}, carriers {carriers.Count}");
            output.WriteLine($"   EXCISED      {excised}");
            output.WriteLine($"   SUBSTITUTED  {substituted.Count}  [{string.Join(", ", substituted)}]");
            output.WriteLine($"   LINE CONTROL {controlWon} of {controlPool}");
        }
    }

    [Fact]
    public void Detail()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("no corpus");
            return;
        }

        var compiled = corpus
            .Select(card => (Card: card, Result: CardCompiler.Compile(card)))
            .ToList();

        foreach (var (name, pattern, substitute) in Shapes)
        {
            var re = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
            output.WriteLine($"===== {name}");

            foreach (var (card, result) in compiled.Where(
                c => !c.Result.IsComplete && c.Result.Unhandled.Any(l => re.IsMatch(l))))
            {
                var mine = result.Unhandled.Where(l => re.IsMatch(l)).ToList();
                var whole = CardCompiler.Lines(card).ToList();
                var swapped = string.Join(
                    "\n",
                    whole.Select(l => mine.Contains(l, StringComparer.Ordinal) ? substitute(l) : l));

                if (!CardCompiler.Compile(Rewritten(card, swapped)).IsComplete)
                    continue;

                foreach (var line in mine)
                    output.WriteLine($"   [{card.Name}] {line}");
            }

            output.WriteLine(string.Empty);
        }
    }

    private static CardDefinition Card(string name, string text) => new()
    {
        OracleId = "probe-" + name,
        Name = name,
        OracleText = text,
        ManaCostRaw = "{2}",
        Cmc = 2,
        CardTypes = MtgEngine.Domain.Enums.CardType.Artifact,
    };

    private static CardDefinition Rewritten(CardDefinition card, string text) => new()
    {
        OracleId = card.OracleId + "#c-" + text.GetHashCode(StringComparison.Ordinal),
        Name = card.Name,
        OracleText = text,
        ManaCostRaw = card.ManaCostRaw,
        Cmc = card.Cmc,
        CardTypes = card.CardTypes,
        Subtypes = card.Subtypes,
        Supertypes = card.Supertypes,
        Keywords = card.Keywords,
        Colors = card.Colors,
        ColorIdentity = card.ColorIdentity,
        Power = card.Power,
        Toughness = card.Toughness,
        Defense = card.Defense,
        AttractionLights = card.AttractionLights,
        Faces = card.Faces,
    };
}
