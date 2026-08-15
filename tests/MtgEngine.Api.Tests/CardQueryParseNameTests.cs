using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// ParseName decides whether a query is a plain name search or structured syntax.
/// Getting this wrong in the permissive direction once made "foo:bar" match the
/// entire corpus: any colon suppressed the name filter, but no structured filter
/// was produced either.
/// </summary>
public class CardQueryParseNameTests
{
    [Theory]
    [InlineData("goblin", "goblin")]
    [InlineData("  Sol Ring  ", "Sol Ring")]
    public void PlainText_IsANameFilter(string query, string expected) =>
        Assert.Equal(expected, CardQuery.ParseName(query));

    [Theory]
    [InlineData("t:creature")]
    [InlineData("o:draw")]
    [InlineData("goblins t:creature")]
    [InlineData("cmc<=3")]
    [InlineData("r:mythic s:m21")]
    [InlineData("c:rg")]
    public void RecognisedSyntax_SuppressesTheNameFilter(string query) =>
        Assert.Null(CardQuery.ParseName(query));

    [Theory]
    [InlineData("foo:bar")]       // unknown token — but "foo:" contains "o:"
    [InlineData("burrito:x")]     // ditto, mid-word "o:"
    [InlineData("act:something")] // mid-word "t:"
    [InlineData("cmcguffin")]     // "cmc" with no operator is just a word
    public void UnrecognisedColonToken_IsStillANameFilter(string query) =>
        Assert.Equal(query, CardQuery.ParseName(query));

    [Fact]
    public void MidWordToken_ProducesNoStructuredFilters()
    {
        // SearchAsync ORs the name and oracle filters, so a phantom oracle filter
        // from mid-word "o:" would quietly widen an unrecognised query's results.
        Assert.Null(CardQuery.ParseOracleText("foo:bar"));
        Assert.Equal(Domain.Enums.CardType.None, CardQuery.ParseTypes("act:creature"));
        Assert.Null(CardQuery.ParseSet("les:m21"));
        Assert.Empty(CardQuery.ParseRarities("centaur:mythic"));
        Assert.False(CardQuery.ParseColors("epic:rg").HasFilter);
        Assert.Null(CardQuery.ParseCmc("acmc<=3").Op);
    }

    [Fact]
    public void BoundaryToken_StillParses()
    {
        Assert.Equal("draw", CardQuery.ParseOracleText("goblin o:draw"));
        Assert.Equal(Domain.Enums.CardType.Creature, CardQuery.ParseTypes("t:creature"));
        Assert.Equal("<=", CardQuery.ParseCmc("cmc<=3").Op);
    }

    [Fact]
    public void QuotedNameToken_ExtractsTheQuotedText() =>
        Assert.Equal("Krenko, Mob Boss", CardQuery.ParseName("name:\"Krenko, Mob Boss\" t:creature"));

    /// <summary>
    /// The shape the client actually sends for every text search. A token right after an
    /// opening bracket is at a word boundary; treating only whitespace as one dropped the
    /// name filter here and silently degraded the search to oracle text alone — so a card
    /// whose rules text does not repeat its own name (Llanowar Elves: "{T}: Add {G}")
    /// could not be found by name at all.
    /// </summary>
    [Fact]
    public void BracketedClause_KeepsBothTheNameAndOracleFilters()
    {
        const string q = "(name:\"llanowar elves\" or o:\"llanowar elves\")";
        Assert.Equal("llanowar elves", CardQuery.ParseName(q));
        Assert.Equal("llanowar elves", CardQuery.ParseOracleText(q));
    }

    [Fact]
    public void BracketedClause_WithTrailingFilters_StillParsesTheName()
    {
        const string q = "(name:\"sol ring\" or o:\"sol ring\") t:artifact";
        Assert.Equal("sol ring", CardQuery.ParseName(q));
        Assert.Equal(Domain.Enums.CardType.Artifact, CardQuery.ParseTypes(q));
    }

    [Fact]
    public void BracketIsNotAnExcuseForMidWordTokens() =>
        // The boundary widened to brackets, not to everything: "(foo:bar" still has its
        // "o:" mid-word and must not become an oracle filter.
        Assert.Null(CardQuery.ParseOracleText("(foo:bar)"));

    // ---- regex name matching (ReDoS-guarded) ----

    [Fact]
    public void CompileNameRegex_ReturnsNullForInvalidPattern() =>
        Assert.Null(CardQuery.CompileNameRegex("(unclosed", matchCase: false));

    [Fact]
    public void MatchesName_InvalidRegex_IsNoMatch() =>
        // useRegex with a pattern that failed to compile (null) must match nothing, not
        // fall through to a substring match.
        Assert.False(CardQuery.MatchesName("Anything", "(unclosed", false, false, true, null));

    [Fact]
    public void MatchesName_CompiledRegex_MatchesAndRespectsCase()
    {
        var ci = CardQuery.CompileNameRegex("go+blin", matchCase: false);
        Assert.True(CardQuery.MatchesName("GOOBLIN", "go+blin", false, false, true, ci));

        var cs = CardQuery.CompileNameRegex("go+blin", matchCase: true);
        Assert.False(CardQuery.MatchesName("GOOBLIN", "go+blin", true, false, true, cs));
    }

    [Fact]
    public void MatchesName_CatastrophicPattern_TimesOutToNoMatch()
    {
        // (a+)+$ against a long non-matching run backtracks catastrophically; the compiled
        // regex's match timeout turns that into a bounded no-match instead of a hang.
        var rx = CardQuery.CompileNameRegex("(a+)+$", matchCase: false);
        Assert.NotNull(rx);
        var evil = new string('a', 40) + "!";
        Assert.False(CardQuery.MatchesName(evil, "(a+)+$", false, false, true, rx));
    }
}
