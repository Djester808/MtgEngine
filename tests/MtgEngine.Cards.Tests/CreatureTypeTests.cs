using System.Diagnostics;

namespace MtgEngine.Cards.Tests;

/// <summary>
/// The creature types of CR 205.3m are generated from the rulebook by tools/gen-vocabulary.js,
/// the way the keyword enums are, so the engine holds rules data and no card data. These tests
/// hold two lines: the committed set says what the rule says, and re-running the generator
/// changes no byte of what is committed.
/// </summary>
public sealed class CreatureTypeTests
{
    [Fact]
    public void The_list_holds_the_one_two_word_creature_type_and_an_ordinary_one()
    {
        // CR 205.3m: "One creature type is two words long: Time Lord."
        Assert.Contains("Time Lord", CreatureTypes.All.AsEnumerable());
        Assert.Contains("Goblin", CreatureTypes.All.AsEnumerable());
        Assert.True(CreatureTypes.IsCreatureType("Time Lord"));
        Assert.True(CreatureTypes.IsCreatureType("Goblin"));
    }

    [Fact]
    public void A_subtype_that_is_not_a_creature_type_is_not_in_the_list()
    {
        // Aura is an enchantment subtype (CR 205.3h) and Equipment an artifact one (CR 205.3g).
        Assert.DoesNotContain("Aura", CreatureTypes.All.AsEnumerable());
        Assert.False(CreatureTypes.IsCreatureType("Aura"));
        Assert.False(CreatureTypes.IsCreatureType("Equipment"));
        Assert.False(CreatureTypes.IsCreatureType("Island"));
        Assert.False(CreatureTypes.IsCreatureType(string.Empty));
    }

    [Fact]
    public void Creature_types_are_matched_without_regard_to_case()
    {
        Assert.True(CreatureTypes.IsCreatureType("goblin"));
        Assert.True(CreatureTypes.IsCreatureType("TIME LORD"));
    }

    [Fact]
    public void The_generated_set_is_the_list_CR_205_3m_gives()
    {
        var fromRules = FromTheRulebook();

        Assert.Equal(fromRules.Count, fromRules.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Empty(fromRules.Except(CreatureTypes.All, StringComparer.Ordinal));
        Assert.Empty(CreatureTypes.All.Except(fromRules, StringComparer.Ordinal));
        // The rule ends "... and Zubera.", so the last name is read without its conjunction.
        Assert.Equal("Zubera", fromRules[^1]);
    }

    [Fact]
    public void A_creature_type_list_shares_only_creature_types()
    {
        Assert.True(CreatureTypes.AnyIn(["Equipment", "Goblin"]));
        Assert.False(CreatureTypes.AnyIn(["Equipment", "Aura"]));
        Assert.True(CreatureTypes.Shared(["Goblin", "Rogue"], ["rogue"]));
        // Two Equipment share a subtype, but not a creature type.
        Assert.False(CreatureTypes.Shared(["Equipment"], ["Equipment"]));
        Assert.False(CreatureTypes.Shared(["Goblin"], ["Elf"]));
    }

    [Fact]
    public void Regenerating_the_vocabulary_changes_nothing()
    {
        var committed = Repo.Path("MtgEngine.Cards", "Generated");
        var temp = Path.Combine(Repo.Path(".agent", "tmp"), "gen-vocabulary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Regenerate(temp);

            var written = Names(Directory.GetFiles(temp));
            Assert.Contains("CreatureTypes.g.cs", written);
            // CR 205.3i's basic land types are generated the same way and by the same run (V7).
            Assert.Contains("LandTypes.g.cs", written);
            Assert.Equal(Names(Directory.GetFiles(committed, "*.g.cs")), written);
            foreach (var name in written)
            {
                Assert.Equal(
                    File.ReadAllBytes(Path.Combine(committed, name)),
                    File.ReadAllBytes(Path.Combine(temp, name)));
            }
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static List<string> Names(IEnumerable<string> paths) =>
        [.. paths.Select(p => Path.GetFileName(p) ?? p).Order(StringComparer.Ordinal)];

    private static void Regenerate(string into)
    {
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = Repo.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine("tools", "gen-vocabulary.js"));
        start.Environment["VOCAB_OUT_DIR"] = into;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("node did not start; the generator cannot be re-run.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "tools/gen-vocabulary.js did not finish inside a minute.");
        Assert.True(process.ExitCode == 0, $"tools/gen-vocabulary.js exited {process.ExitCode}: {output}");
    }

    /// <summary>
    /// CR 205.3m in the rulebook's own words: each "... long:" clause up to its full stop is a
    /// list of creature types. Read here the way the generator reads it, so the two agree by
    /// construction rather than by a second copy of the list.
    /// </summary>
    private static List<string> FromTheRulebook()
    {
        var line = File.ReadLines(Repo.Path("MtgEngine.Api", "Knowledge", "comprehensive-rules.txt"))
            .Select(l => l.Trim())
            .First(l => l.StartsWith("205.3m", StringComparison.Ordinal));

        var types = new List<string>();
        foreach (var clause in line.Split("long:", StringSplitOptions.None).Skip(1))
        {
            foreach (var part in clause.Split('.')[0].Split(','))
            {
                var name = part.Trim();
                if (name.StartsWith("and ", StringComparison.OrdinalIgnoreCase))
                    name = name[4..].Trim();
                if (name.Length > 0)
                    types.Add(name);
            }
        }

        return types;
    }
}
