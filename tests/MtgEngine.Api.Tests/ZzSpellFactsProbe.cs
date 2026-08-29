using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

/// <summary>TEMPORARY PROBE - delete before commit.</summary>
public sealed class ZzSpellFactsProbe(ITestOutputHelper output)
{
    private static readonly string Out =
        Environment.GetEnvironmentVariable("PROBE_OUT") ?? "spellfacts-complete.txt";

    /// <summary>Writes the name of every fully compiled card, so two runs can be set-diffed.</summary>
    [Fact]
    public void Dump_the_set_of_complete_cards()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("no corpus");
            return;
        }

        var complete = corpus
            .Where(c => CardCompiler.Compile(c).IsComplete)
            .Select(c => c.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        File.WriteAllLines(Out, complete);
        output.WriteLine($"wrote {complete.Count} names to {Out}");
    }

    /// <summary>Which sentence of each near-miss card defeats the rider.</summary>
    [Fact]
    public void Debug_near_misses()
    {
        void Check(string what, string text, MtgEngine.Domain.Enums.CardType type)
        {
            var card = new MtgEngine.Domain.Models.CardDefinition
            {
                OracleId = Guid.NewGuid().ToString("N"),
                Name = "Probe " + what,
                OracleText = text,
                CardTypes = type,
            };

            var compiled = CardCompiler.Compile(card);
            output.WriteLine(
                $"{(compiled.IsComplete ? "READS " : "unread")} {what}: "
                + string.Join(" | ", compiled.Unhandled));
        }

        var instant = MtgEngine.Domain.Enums.CardType.Instant;
        var sorcery = MtgEngine.Domain.Enums.CardType.Sorcery;

        Check("helicarrier-inner", "Probe helicarrier-inner deals 2 damage to target attacking or blocking creature. It deals 4 damage to that creature.", instant);
        Check("helicarrier-full", "Teamwork 2\nProbe helicarrier-full deals 2 damage to target attacking or blocking creature. If this spell was cast using teamwork, it deals 4 damage to that creature instead.", instant);
        Check("crossover-treasure", "Create a Treasure token.", sorcery);
        Check("crossover-full", "Teamwork 2\nExile the top two cards of your library. Until the end of your next turn, you may play those cards. If this spell was cast using teamwork, create a Treasure token.", sorcery);
        Check("torch-tail", "Probe torch-tail deals 3 damage to that permanent and you scry 1.\nProbe torch-tail deals 2 damage to target creature or planeswalker.", instant);
    }

    /// <summary>Whether each candidate inner sentence already parses, measured not guessed.</summary>
    [Fact]
    public void Spot_check_inner_sentences()
    {
        var rows = new List<string>();

        void Check(string what, string text, MtgEngine.Domain.Enums.CardType type)
        {
            var card = new MtgEngine.Domain.Models.CardDefinition
            {
                OracleId = Guid.NewGuid().ToString("N"),
                Name = "Probe " + what,
                OracleText = text,
                CardTypes = type,
                Power = type.HasFlag(MtgEngine.Domain.Enums.CardType.Creature) ? 2 : null,
                Toughness = type.HasFlag(MtgEngine.Domain.Enums.CardType.Creature) ? 2 : null,
            };

            var compiled = CardCompiler.Compile(card);
            rows.Add(
                $"{(compiled.IsComplete ? "READS " : "unread")}\t{what}\t"
                + string.Join(" | ", compiled.Unhandled));
        }

        var instant = MtgEngine.Domain.Enums.CardType.Instant;
        var creature = MtgEngine.Domain.Enums.CardType.Creature;
        var sorcery = MtgEngine.Domain.Enums.CardType.Sorcery;

        Check("destroy-that", "Probe destroy-that deals 3 damage to target attacking or blocking creature. Destroy that creature.", instant);
        Check("minus-that", "Target creature gets -3/-3 until end of turn. That creature gets -5/-5 until end of turn.", instant);
        Check("dmg-and-scry", "Probe dmg-and-scry deals 2 damage to target creature or planeswalker. It deals 3 damage to that permanent and you scry 1.", instant);
        Check("gains-that", "Target creature gets +2/+2 until end of turn. That creature gains flying and lifelink until end of turn.", instant);
        Check("that-controller", "Probe that-controller deals 5 damage to target creature. It deals 2 damage to that creature's controller.", instant);
        Check("fights-up-to-one", "When this creature enters, it fights up to one target creature you don't control.", creature);
        Check("four-mana-combo", "When this creature enters, add four mana in any combination of colors.", creature);
        Check("role-attached", "When this creature enters, create a Cursed Role token attached to target creature an opponent controls.", creature);
        Check("as-enters-mill", "As this creature enters, mill three cards.", creature);
        Check("noregen-two-sentence", "When this creature enters, destroy target nonblack creature. That creature can't be regenerated.", creature);
        Check("discard-two", "When this creature enters, target player discards two cards.", creature);
        Check("return-up-to-two", "When this creature enters, return up to two target nonblack creatures to their owners' hands.", creature);
        Check("impulse-two", "Exile the top two cards of your library. Until the end of your next turn, you may play those cards.", sorcery);
        Check("counter-on-that", "Target creature gets +2/+2 and gains trample until end of turn. Put a +1/+1 counter on that creature.", instant);
        Check("draw-then-discard", "Draw three cards. Then discard a card.", sorcery);
        Check("land-animate-perm", "Target land you control becomes a 3/3 Elemental creature with haste that's still a land.", sorcery);
        Check("robot-hero-token", "When this creature enters, create a 1/1 colorless Robot Hero artifact creature token with flying.", creature);
        Check("tap-power-controller", "When this creature enters, tap target untapped creature and that creature deals damage equal to its power to its controller.", creature);
        Check("exile-art-ench", "When you cast this spell, exile target artifact or enchantment an opponent controls.", creature);
        Check("counter-unless-4", "Counter target spell unless its controller pays {4}.", instant);
        Check("kicked-counters-plain", "Kicker {3}\nIf this spell was kicked, it enters with two +1/+1 counters on it.", creature);
        Check("gain-control-power", "When this creature enters, gain control of target creature with power less than this creature's power for as long as you control this creature.", creature);

        File.WriteAllLines(Out + ".spot", rows);
        output.WriteLine($"wrote {rows.Count} rows to {Out}.spot");
    }

    /// <summary>Per-card unread lines for the three cast-fact families this round is about.</summary>
    [Fact]
    public void Dump_the_blocking_lines_of_the_fact_families()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("no corpus");
            return;
        }

        var interesting = corpus.Where(c =>
            c.OracleText.Contains("bargain", StringComparison.OrdinalIgnoreCase)
            || c.OracleText.Contains("teamwork", StringComparison.OrdinalIgnoreCase)
            || c.OracleText.Contains("kicked with", StringComparison.OrdinalIgnoreCase)
            || c.OracleText.Contains("and/or", StringComparison.OrdinalIgnoreCase)
            || c.OracleText.Contains("was kicked, choose", StringComparison.OrdinalIgnoreCase)
            || c.OracleText.Contains("Choose up to", StringComparison.Ordinal));

        var lines = new List<string>();
        foreach (var card in interesting.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.IsComplete)
            {
                lines.Add($"COMPLETE\t{card.Name}");
                continue;
            }

            foreach (var line in compiled.Unhandled)
                lines.Add($"UNREAD\t{card.Name}\t{line}");
        }

        File.WriteAllLines(Out + ".families", lines);
        output.WriteLine($"wrote {lines.Count} rows to {Out}.families");
    }
}
