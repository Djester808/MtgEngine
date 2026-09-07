using MtgEngine.Api.Services;
using MtgEngine.Cards;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The three admissions, and the one thing the gate is allowed to read.
/// </summary>
/// <remarks>
/// The fourth rule of the card engine plan: nothing half-implemented reaches a table. A card is
/// enforced, badged honour-system, or refused, and there is no fourth state. These tests exist
/// because the failure they prevent is silent — a card that is only half translated does not
/// crash, it plays a game slightly wrong and nobody at the table can tell which half was real.
/// </remarks>
public sealed class DeckGateTests
{
    /// <summary>A pool with made-up cards in it, so no test depends on the corpus being present.</summary>
    private sealed class FakePool : ICardFilePool
    {
        private readonly Dictionary<string, CardStatus> _cards = new(StringComparer.OrdinalIgnoreCase);

        public int Count => _cards.Count;

        public FakePool With(string oracleId, string name, ImplementationStatus status, string? reason = null)
        {
            _cards[oracleId] = new CardStatus(oracleId, name, status, reason);
            return this;
        }

        public CardStatus StatusOf(string oracleId) =>
            _cards.TryGetValue(oracleId, out var found) ? found : CardStatus.Unknown(oracleId);

        public CardDefinition? ByName(string name) => null;

        public CardDefinition? ByOracleId(string oracleId) => null;
    }

    private static FakePool Pool() => new FakePool()
        .With("o-verified", "Beast of Testing", ImplementationStatus.Verified)
        .With("o-translated", "Wurm of Reading", ImplementationStatus.Translated)
        .With("o-disputed", "Sphinx of Two Minds", ImplementationStatus.Disputed)
        .With("o-untranslated", "Thing Unread", ImplementationStatus.Untranslated)
        .With("o-excluded", "Shahrazad Stand-in", ImplementationStatus.Excluded, "starts a subgame");

    [Fact]
    public void A_verified_card_is_admitted_and_enforced()
    {
        var admission = new DeckGate(Pool()).Admit(["o-verified"], allowHonourSystem: false);

        var card = Assert.Single(admission.Cards);
        Assert.True(card.Enforced);
        Assert.Equal(ImplementationStatus.Verified, card.Status);
        Assert.True(admission.AllEnforced);
        Assert.Contains("o-verified", admission.EnforcedOracleIds);
    }

    [Theory]
    [InlineData("o-translated")]
    [InlineData("o-disputed")]
    [InlineData("o-untranslated")]
    public void An_unverified_card_is_refused_where_the_table_enforces_everything(string oracleId)
    {
        var gate = new DeckGate(Pool());

        var refusal = Assert.Throws<InvalidRequestException>(() => gate.Admit([oracleId], allowHonourSystem: false));

        // The refusal has to be actionable: a hundred-card deck told only "not allowed" cannot
        // be fixed by the person who wrote it.
        Assert.Contains(Pool().StatusOf(oracleId).Name, refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("o-translated")]
    [InlineData("o-disputed")]
    [InlineData("o-untranslated")]
    public void An_unverified_card_is_admitted_unenforced_where_the_table_allows_it(string oracleId)
    {
        var admission = new DeckGate(Pool()).Admit([oracleId], allowHonourSystem: true);

        var card = Assert.Single(admission.Cards);
        Assert.False(card.Enforced);
        Assert.False(admission.AllEnforced);
        Assert.Empty(admission.EnforcedOracleIds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_excluded_card_is_refused_at_every_table_with_its_reason(bool allowHonourSystem)
    {
        // No table setting makes a card the vocabulary cannot represent playable, so the
        // honour-system arm must not quietly sweep this one up with the unverified ones.
        var refusal = Assert.Throws<InvalidRequestException>(
            () => new DeckGate(Pool()).Admit(["o-excluded"], allowHonourSystem));

        Assert.Contains("Shahrazad Stand-in", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("subgame", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_card_no_file_covers_is_untranslated_rather_than_a_crash()
    {
        var gate = new DeckGate(Pool());

        Assert.Throws<InvalidRequestException>(() => gate.Admit(["o-nobody-has-heard-of"], allowHonourSystem: false));

        var admission = gate.Admit(["o-nobody-has-heard-of"], allowHonourSystem: true);
        Assert.False(Assert.Single(admission.Cards).Enforced);
    }

    [Fact]
    public void A_refusal_names_every_card_it_turned_away_and_counts_the_rest()
    {
        var pool = Pool();
        for (var i = 0; i < 9; i++)
            pool.With($"o-extra-{i}", $"Extra Card {i}", ImplementationStatus.Translated);

        var deck = new List<string> { "o-verified" };
        deck.AddRange(Enumerable.Range(0, 9).Select(i => $"o-extra-{i}"));

        var refusal = Assert.Throws<InvalidRequestException>(() => new DeckGate(pool).Admit(deck, allowHonourSystem: false));

        Assert.StartsWith("9 card(s) cannot be played here", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Extra Card 0", refusal.Message, StringComparison.Ordinal);
        // Capped so the detail still reads inside a sheet rather than running to a hundred names.
        Assert.Contains($"and {9 - DeckGate.NamedInRefusal} more", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Extra Card 8", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deck_is_admitted_once_per_card_however_many_copies_it_holds()
    {
        var admission = new DeckGate(Pool()).Admit(["o-verified", "o-verified", "o-verified"], allowHonourSystem: false);

        Assert.Single(admission.Cards);
    }

    [Fact]
    public void A_mixed_deck_is_refused_as_a_whole_where_the_table_enforces_everything()
    {
        // Half a deck admitted is not a state a table can be in: the refusal is the deck's.
        var refusal = Assert.Throws<InvalidRequestException>(
            () => new DeckGate(Pool()).Admit(["o-verified", "o-translated"], allowHonourSystem: false));

        Assert.Contains("Wurm of Reading", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Beast of Testing", refusal.Message, StringComparison.Ordinal);
    }
}
