using FluentAssertions;
using MtgEngine.Api.Services;
using Xunit;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Unit tests for BulkDataService.MatchesName — the name-filter logic
/// that backs match-case, match-word, and regex search flags.
/// </summary>
public sealed class BulkDataSearchTests
{
    // Helper so tests read as plain English
    private static bool Matches(
        string name, string filter,
        bool matchCase = false, bool matchWord = false, bool useRegex = false)
        => BulkDataService.MatchesName(name, filter, matchCase, matchWord, useRegex);

    // ---- Default: case-insensitive contains ---------------------

    [Theory]
    [InlineData("Angrath, Minotaur Pirate", "rat")]
    [InlineData("Rat Colony",               "rat")]
    [InlineData("Gratroth, Beast",          "rat")]
    [InlineData("RAT COLONY",               "rat")]   // case-insensitive by default
    [InlineData("Angrath",                  "RAT")]   // upper filter, lower in name
    public void DefaultContains_MatchesSubstring(string name, string filter)
        => Matches(name, filter).Should().BeTrue();

    [Theory]
    [InlineData("Lightning Bolt",  "rat")]
    [InlineData("Counterspell",    "rat")]
    public void DefaultContains_NoMatch_ReturnsFalse(string name, string filter)
        => Matches(name, filter).Should().BeFalse();

    // ---- Match case: case-sensitive contains --------------------

    [Theory]
    [InlineData("Rat Colony",  "Rat")]
    [InlineData("Angrath",     "grath")]
    public void MatchCase_CaseSensitiveHit_ReturnsTrue(string name, string filter)
        => Matches(name, filter, matchCase: true).Should().BeTrue();

    [Theory]
    [InlineData("Rat Colony",  "rat")]   // correct word, wrong case
    [InlineData("Rat Colony",  "RAT")]
    [InlineData("Angrath",     "RAT")]
    public void MatchCase_WrongCase_ReturnsFalse(string name, string filter)
        => Matches(name, filter, matchCase: true).Should().BeFalse();

    // ---- Match word: whole-word contains (case-insensitive) -----

    [Theory]
    [InlineData("Rat Colony",    "rat")]    // "Rat" is a whole word
    [InlineData("Rat Colony",    "Colony")] // second word
    [InlineData("Pack Rat",      "rat")]    // word at end
    [InlineData("Rat",           "rat")]    // single-word name
    [InlineData("Rat, the Pest", "rat")]    // word before comma
    public void MatchWord_WholeWord_ReturnsTrue(string name, string filter)
        => Matches(name, filter, matchWord: true).Should().BeTrue();

    [Theory]
    [InlineData("Angrath", "rat")]          // "rat" is inside "Angrath", not standalone
    [InlineData("Brazen Borrower", "rat")]  // no "rat" word at all
    [InlineData("Grateful Dead", "grat")]   // partial word
    public void MatchWord_PartialWordInName_ReturnsFalse(string name, string filter)
        => Matches(name, filter, matchWord: true).Should().BeFalse();

    // ---- Match word is case-insensitive by default --------------

    [Theory]
    [InlineData("Rat Colony", "RAT")]
    [InlineData("Rat Colony", "COLONY")]
    public void MatchWord_DefaultCaseInsensitive_ReturnsTrue(string name, string filter)
        => Matches(name, filter, matchWord: true).Should().BeTrue();

    // ---- Match word + match case: both constraints apply --------

    [Fact]
    public void MatchWordAndCase_CorrectCaseWholeWord_ReturnsTrue()
        => Matches("Rat Colony", "Rat", matchCase: true, matchWord: true).Should().BeTrue();

    [Fact]
    public void MatchWordAndCase_WrongCase_ReturnsFalse()
        => Matches("Rat Colony", "rat", matchCase: true, matchWord: true).Should().BeFalse();

    [Fact]
    public void MatchWordAndCase_PartialWord_ReturnsFalse()
        => Matches("Angrath", "rath", matchCase: true, matchWord: true).Should().BeFalse();

    // ---- Regex --------------------------------------------------

    [Theory]
    [InlineData("Angrath, Minotaur Pirate", @"^Ang")]       // anchored start
    [InlineData("Pack Rat",                 @"Rat$")]         // anchored end
    [InlineData("Lightning Bolt",           @"L.+g")]        // wildcard
    [InlineData("Counterspell",             @"counter")]      // case-insensitive by default
    [InlineData("Thoughtseize",             @"(thought|seize)")] // alternation
    public void UseRegex_ValidPattern_MatchesExpected(string name, string pattern)
        => Matches(name, pattern, useRegex: true).Should().BeTrue();

    [Theory]
    [InlineData("Rat Colony",    @"^Bolt")]
    [InlineData("Lightning Bolt", @"rat")]
    public void UseRegex_NoMatch_ReturnsFalse(string name, string pattern)
        => Matches(name, pattern, useRegex: true).Should().BeFalse();

    [Fact]
    public void UseRegex_DefaultCaseInsensitive_MatchesLowerPattern()
        => Matches("Counterspell", "counter", useRegex: true).Should().BeTrue();

    [Fact]
    public void UseRegex_WithMatchCase_PatternIsCaseSensitive()
    {
        Matches("Counterspell", "Counter", useRegex: true, matchCase: true).Should().BeTrue();
        Matches("Counterspell", "counter", useRegex: true, matchCase: true).Should().BeFalse();
    }

    [Theory]
    [InlineData("[invalid")]   // unclosed bracket
    [InlineData("(unmatched")] // unclosed group
    [InlineData(@"\p{Boom}")]  // unknown Unicode category
    public void UseRegex_InvalidPattern_ReturnsFalseWithoutThrowing(string badPattern)
    {
        var act = () => Matches("Any Card Name", badPattern, useRegex: true);
        act.Should().NotThrow();
        act().Should().BeFalse();
    }

    // ---- Word separators ----------------------------------------

    [Theory]
    [InlineData("Sword of Fire and Ice", "Fire")]   // space-delimited
    [InlineData("Turn // Burn",           "Burn")]   // slash-delimited
    [InlineData("Gideon's Lawkeeper",     "Lawkeeper")] // apostrophe-delimited
    [InlineData("Soul-Scar Mage",         "Scar")]   // hyphen-delimited
    [InlineData("Hazoret (God)",          "God")]    // paren-delimited
    public void MatchWord_VariousSeparators_RecognizedAsWordBoundaries(string name, string filter)
        => Matches(name, filter, matchWord: true).Should().BeTrue();

    // ---- Edge cases ---------------------------------------------

    [Fact]
    public void EmptyFilter_AlwaysMatchesWithContains()
        => Matches("Any Name", "", matchCase: false).Should().BeTrue();

    [Fact]
    public void ExactNameMatch_ReturnsTrue()
        => Matches("Counterspell", "Counterspell").Should().BeTrue();

    [Fact]
    public void ExactNameMatch_WithMatchCase_ReturnsTrue()
        => Matches("Counterspell", "Counterspell", matchCase: true).Should().BeTrue();
}
