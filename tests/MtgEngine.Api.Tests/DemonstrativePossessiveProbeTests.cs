using System.Text.RegularExpressions;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>
/// What "that spell's mana value" and its family would be worth, measured three ways.
/// </summary>
/// <remarks>
/// A diagnostic, not a guard: it asserts nothing, exactly as the rest of the work queue does.
/// The family is the demonstrative possessive — a quantity measured on an object the sentence
/// points at by type rather than by name — and the whole of its difficulty is <em>which</em>
/// object, so the three columns are arranged to separate the referent from everything else.
/// <para>
/// <b>Excision</b> cuts the family's lines and asks how many cards would then compile whole. It
/// is an upper bound and has over-counted by between 1.5x and 82x on the families measured this
/// round. <b>Substitution</b> is the one that answers the question: it replaces the demonstrative
/// with <c>~'s</c>, the one possessive the compiler already reads, and leaves every other word
/// standing — so it counts the cards for which the <em>referent</em> was the only thing missing.
/// The <b>line control</b> drops a different unread line on a carrier that has one, which asks
/// whether these particular cards are one line short of anything at all.
/// </para>
/// </remarks>
public sealed partial class DemonstrativePossessiveProbeTests(ITestOutputHelper output)
{
    /// <summary>"…that spell's mana value", "…that creature's power" — the family as a shape.</summary>
    [GeneratedRegex(
        @"\bthat (?<noun>spell|creature|artifact|permanent|card|token|land|enchantment)'s "
            + @"(?<stat>power|toughness|mana value)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Demonstrative();

    [Fact]
    public void What_the_demonstrative_possessive_family_would_be_worth()
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

        output.WriteLine(
            $"corpus {corpus.Count}, complete {compiled.Count(c => c.Result.IsComplete)}");

        foreach (var (label, shape) in Families())
        {
            var carriers = compiled
                .Where(c => !c.Result.IsComplete && c.Result.Unhandled.Any(shape))
                .ToList();

            var lines = carriers.Sum(c => c.Result.Unhandled.Count(shape));
            var excised = 0;
            var substituted = new List<string>();
            var controlPool = 0;
            var controlWon = 0;

            foreach (var (card, result) in carriers)
            {
                var mine = result.Unhandled.Where(shape).ToList();
                var whole = CardCompiler.Lines(card).ToList();

                var cut = string.Join(
                    "\n", whole.Where(l => !mine.Contains(l, StringComparer.Ordinal)));
                if (CardCompiler.Compile(Rewritten(card, cut)).IsComplete)
                    excised++;

                var swapped = string.Join(
                    "\n",
                    whole.Select(l => mine.Contains(l, StringComparer.Ordinal) ? Named(l) : l));
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

            output.WriteLine(string.Empty);
            output.WriteLine($"--- {label}: {carriers.Count} carriers, {lines} unread lines");
            output.WriteLine($"EXCISED      {excised}");
            output.WriteLine(
                $"SUBSTITUTED  {substituted.Count}  [{string.Join(", ", substituted)}]");
            output.WriteLine(
                $"LINE CONTROL {controlWon} of {controlPool} carriers with another unread line");
        }
    }

    /// <summary>The whole family, then the two subfamilies the referent question splits it into.</summary>
    private static IEnumerable<(string Label, Func<string, bool> Shape)> Families()
    {
        yield return ("whole family", line => Demonstrative().IsMatch(line));
        yield return (
            "that spell's",
            line => Demonstrative().Matches(line).Any(
                m => m.Groups["noun"].Value.Equals("spell", StringComparison.OrdinalIgnoreCase)));
        yield return (
            "that creature's",
            line => Demonstrative().Matches(line).Any(
                m => m.Groups["noun"].Value.Equals(
                    "creature", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The same sentence with the source's own name where the demonstrative stood.
    /// </summary>
    /// <remarks>
    /// <c>~'s power</c> is the one possessive of this shape the compiler already reads, and it
    /// reads it without having to work anything out — the tilde is the card naming itself. So a
    /// line that compiles once the demonstrative is swapped for it is a line whose <em>only</em>
    /// missing piece was which object the words point at, which is what this family is about.
    /// </remarks>
    private static string Named(string line) =>
        Demonstrative().Replace(line, m => "~'s " + m.Groups["stat"].Value);

    /// <summary>The same card with different words on it, for a control run.</summary>
    private static CardDefinition Rewritten(CardDefinition card, string text) => new()
    {
        OracleId = card.OracleId + "#probe-" + text.GetHashCode(StringComparison.Ordinal),
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

    /// <summary>
    /// Every complete card with a signature of what it compiled to, for a before/after diff.
    /// </summary>
    /// <remarks>
    /// The set alone says which cards were gained and lost; the signature is what catches a card
    /// that stayed complete and started doing something different, which a coverage number cannot
    /// see at all.
    /// </remarks>
    [Fact]
    public void Every_complete_card_is_written_out_with_its_shape()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("oracle_cards.json not present — skipping.");
            return;
        }

        var destination = Environment.GetEnvironmentVariable("MTG_COMPLETE_DUMP");
        if (string.IsNullOrWhiteSpace(destination))
        {
            output.WriteLine("MTG_COMPLETE_DUMP not set — skipping.");
            return;
        }

        var rows = new List<string>(20_000);
        foreach (var card in corpus)
        {
            var result = CardCompiler.Compile(card);
            if (!result.IsComplete)
                continue;

            rows.Add($"{card.OracleId}\t{card.Name}\t{Shape(result)}");
        }

        rows.Sort(StringComparer.Ordinal);
        File.WriteAllLines(destination, rows);
        output.WriteLine($"{rows.Count} complete cards written to {destination}");
    }

    private static string Shape(CompiledCard card)
    {
        var parts = new List<string>
        {
            "spell:" + Join(card.Spell?.Effects),
            "act:" + string.Join(";", card.Activated.Select(a => a.Id + "=" + Join(a.Effects))),
            "trg:" + string.Join(";", card.Triggers.Select(t => t.Id + "=" + Join(t.Effects))),
            "sta:" + card.Statics.Count,
            "rep:" + card.Replacements.Count,
        };

        return string.Join("|", parts);
    }

    private static string Join(IEnumerable<IEffect>? effects) =>
        effects is null
            ? string.Empty
            : string.Join(",", effects.Select(e => e.GetType().Name));
}
