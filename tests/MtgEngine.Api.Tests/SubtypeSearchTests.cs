using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// Players search creature types in the plural. Every candidate singular is offered and
/// the caller keeps whichever is a real type, because English does not pluralise
/// consistently enough to pick one rule.
/// </summary>
public class SubtypeSearchTests
{
    private static void AssertYields(string plural, string expectedSingular)
    {
        var forms = CardQuery.SingularCandidates(plural);
        Assert.Contains(expectedSingular, forms, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("wolves", "wolf")]
    [InlineData("elves", "elf")]
    [InlineData("dwarves", "dwarf")]
    [InlineData("goblins", "goblin")]
    [InlineData("zombies", "zombie")]
    [InlineData("faeries", "faerie")]
    [InlineData("allies", "ally")]
    [InlineData("foxes", "fox")]
    public void SingularCandidates_CoversTheCommonPlurals(string plural, string singular)
        => AssertYields(plural, singular);

    [Fact]
    public void SingularCandidates_AlwaysIncludesTheWordAsTyped()
    {
        // Singular searches and types that are already plural-looking ("Merfolk") must
        // still match themselves.
        Assert.Contains("wolf", CardQuery.SingularCandidates("wolf"), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("merfolk", CardQuery.SingularCandidates("merfolk"), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "wolves" must offer "wolf" without also insisting on "wolve"; the caller filters
    /// candidates against the real subtype list, so extra forms are harmless but the
    /// correct one has to be present.
    /// </summary>
    [Fact]
    public void SingularCandidates_OffersTheFWhenTheWordEndsInVes()
    {
        var forms = CardQuery.SingularCandidates("wolves");
        Assert.Contains("wolf", forms, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("wolves", forms, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SingularCandidates_LeavesVeryShortWordsAlone()
    {
        Assert.Equal(["ox"], CardQuery.SingularCandidates("ox"));
    }

    [Fact]
    public void SingularCandidates_DoesNotRepeatAForm()
    {
        var forms = CardQuery.SingularCandidates("wolves");
        Assert.Equal(forms.Length, forms.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
