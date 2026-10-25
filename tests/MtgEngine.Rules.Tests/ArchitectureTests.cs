using System.Text.RegularExpressions;
using MtgEngine.Rules.Engine;
using NetArchTest.Rules;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The plan's first rule, as a build failure: a card is data or it does not exist. The engine
/// may depend on the vocabulary and nothing else, and no card's name may appear in it.
/// </summary>
public sealed class ArchitectureTests
{
    [Fact]
    public void The_engine_depends_on_the_vocabulary_and_nothing_else()
    {
        var result = Types.InAssembly(typeof(Game).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny("MtgEngine.Api", "MtgEngine.Domain", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "System.Net.Http")
            .GetResult();

        Assert.True(result.IsSuccessful, "Engine types with forbidden dependencies: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void No_card_name_appears_in_the_engine_source()
    {
        // Single words are also card names ("Fear", "Counterspell", "Sacrifice"), so the check
        // is on names of two or more words, which no rule or comment has reason to spell out.
        var names = File.ReadLines(Repo.Path("tools", "card-names.txt"))
            .Where(n => n.Contains(' ', StringComparison.Ordinal) && n.Length >= 8)
            .ToHashSet(StringComparer.Ordinal);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Repo.Path("MtgEngine.Rules"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, "\"([^\"\\\\]{8,})\""))
            {
                if (names.Contains(m.Groups[1].Value))
                    offenders.Add($"{Path.GetRelativePath(Repo.Root, file)}: \"{m.Groups[1].Value}\"");
            }
        }

        Assert.True(offenders.Count == 0, "Card names in engine source:\n" + string.Join("\n", offenders));
    }

}
