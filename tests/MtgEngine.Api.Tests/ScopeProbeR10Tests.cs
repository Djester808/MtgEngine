using System.Text;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Cards;
using Xunit.Abstractions;

namespace MtgEngine.Api.Tests;

// Scratch probe for the r10-scopes round. Deleted before commit.
public sealed class ScopeProbeR10Tests(ITestOutputHelper output)
{
    [Fact]
    public void Dump_complete_set()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("no corpus - skipping");
            return;
        }

        var sb = new StringBuilder();
        var complete = 0;

        foreach (var card in corpus)
        {
            var compiled = CardCompiler.Compile(card);
            if (compiled.IsComplete)
            {
                complete++;
                sb.Append(card.OracleId).Append('\t').Append(card.Name).Append('\n');
            }
        }

        var path = Environment.GetEnvironmentVariable("MTG_COMPLETE_SET_OUT");
        if (path is not null)
            File.WriteAllText(path, sb.ToString());

        output.WriteLine($"complete: {complete} of {corpus.Count}");
    }

    [Fact]
    public void Probe_families()
    {
        var corpus = CardCompilerCoverageTests.LoadCorpusOrSkip();
        if (corpus is null)
        {
            output.WriteLine("no corpus - skipping");
            return;
        }

        void Family(string label, Func<CardDefinition, bool> mentions, int cap = 40)
        {
            output.WriteLine($"==== {label} ====");
            var listed = 0;
            var total = 0;
            var wouldComplete = 0;

            foreach (var card in corpus)
            {
                if (!mentions(card))
                    continue;

                total++;
                var compiled = CardCompiler.Compile(card);
                if (compiled.IsComplete)
                {
                    if (listed++ < cap)
                        output.WriteLine($"  COMPLETE  {card.Name}");
                    continue;
                }

                var blocking = compiled.Unhandled;
                var allMention = blocking.All(l => mentions(new CardDefinition
                {
                    OracleId = card.OracleId,
                    Name = card.Name,
                    OracleText = l,
                }));
                if (allMention)
                    wouldComplete++;

                if (listed++ < cap)
                {
                    output.WriteLine($"  {card.Name}  ({blocking.Count} blocked)");
                    foreach (var line in blocking.Take(5))
                        output.WriteLine($"      XX {line}");
                }
            }

            output.WriteLine($"  -- total {total}, sole-blocked-by-family {wouldComplete}");
            output.WriteLine(string.Empty);
        }

        Family(
            "enchanted player controls (static group)",
            c => (c.OracleText).Contains(
                "enchanted player controls", StringComparison.OrdinalIgnoreCase));

        Family(
            "gets an additional (attached-condition statics)",
            c => (c.OracleText).Contains(
                "an additional +", StringComparison.OrdinalIgnoreCase));

        Family(
            "each opponent attacking (does the same)",
            c => (c.OracleText).Contains(
                "attacking that player", StringComparison.OrdinalIgnoreCase));

        Family(
            "goad",
            c => (c.OracleText).Contains("goad", StringComparison.OrdinalIgnoreCase),
            60);

        Family(
            "granted ward (has/have ward)",
            c => (c.OracleText).Contains("ward {", StringComparison.OrdinalIgnoreCase)
                && ((c.OracleText).Contains("has ward", StringComparison.OrdinalIgnoreCase)
                    || (c.OracleText).Contains("have ward", StringComparison.OrdinalIgnoreCase)),
            40);
    }

    [Fact]
    public void Probe_sentences()
    {
        (string, CardType)[] probes =
        [
            ("Enchant player\nCreatures enchanted player controls get -1/-1.", CardType.Enchantment),
            ("Enchant player\nCreatures enchanted player controls get -1/-1 and have no abilities.", CardType.Enchantment),
            ("Creatures you control get -1/-1.", CardType.Enchantment),
            ("Equipped creature gets +2/+0.\nAs long as equipped creature is a Human, it gets an additional +1/+0.", CardType.Artifact),
            ("Equipped creature gets +3/+0.\nAs long as equipped creature is a Human, it has lifelink.", CardType.Artifact),
            ("As long as enchanted creature is a Human, it gets +1/+1.", CardType.Enchantment),
            ("Enchant creature\nEnchanted creature has ward {2}.", CardType.Enchantment),
            ("Whenever a creature attacks enchanted player, that creature's controller sacrifices a permanent.", CardType.Enchantment),
            ("Other creatures you control have ward {2}.", CardType.Creature),
            ("Legendary creatures you control get +2/+1 and have ward {1}.", CardType.Enchantment),
            ("Each other Human you control gets +1/+0 and has ward {1}.", CardType.Creature),
            ("Each creature you control with a counter on it has ward {1}.", CardType.Creature),
            ("Equip {2}\nEquipped creature gets +2/+1 and has ward {2}.", CardType.Artifact),
            ("Equip {1}\nEquipped creature has ward {2}, is an Assassin in addition to its other types, and can't be blocked.", CardType.Artifact),
            ("Enchant creature\nEnchanted creature is goaded.", CardType.Enchantment),
            ("Enchant creature\nEnchanted creature gets +2/+2 and is goaded.", CardType.Enchantment),
            ("Enchant player\nWhenever enchanted player is attacked, you gain 2 life. Each opponent attacking that player does the same.", CardType.Enchantment),
        ];

        foreach (var (text, kind) in probes)
        {
            var card = new CardDefinition
            {
                OracleId = "probe-" + Guid.NewGuid().ToString("N"),
                Name = "Probe " + Guid.NewGuid().ToString("N")[..6],
                CardTypes = kind,
                OracleText = text,
            };

            var compiled = CardCompiler.Compile(card);
            var verdict = compiled.IsComplete
                ? "ok "
                : "XX ";
            output.WriteLine(verdict + text.Replace('\n', ' '));
            foreach (var line in compiled.Unhandled)
                output.WriteLine("      unread: " + line);
        }
    }
}
