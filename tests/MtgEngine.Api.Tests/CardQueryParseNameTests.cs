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
}
