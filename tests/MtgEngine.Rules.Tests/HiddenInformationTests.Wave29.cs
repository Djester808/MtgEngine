using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 29. The one card a hidden zone shows (CR 401.5 with 401.6), and the price the
/// legal-actions view puts on a taxed attacker (CR 508.1d).
/// </summary>
public sealed partial class HiddenInformationTests
{
    /// <summary>
    /// CR 401.5: "play with the top card of your library revealed" reveals it to every player,
    /// not to its owner. Both viewers are asserted, and the opponent's serialised view is asserted
    /// to carry the name — this is the one place a library card is allowed to appear in it.
    /// </summary>
    [Fact]
    [RuleRef("401.5")]
    public void A_revealed_top_card_is_shown_to_every_player()
    {
        var state = Revealing(Start(openingHand: 0), Me);

        foreach (var viewer in new[] { Me, Them })
        {
            var view = Project(state, viewer);

            Assert.NotNull(view.Players[0].RevealedTopCard);
            var revealed = view.Players[0].RevealedTopCard!;
            Assert.Equal(MineName, revealed.Name);
            Assert.Equal(state.In(ZoneKey.Of(Zone.Library, Me))[0], revealed.Id);

            // The library is still a count: one card is revealed, not the deck.
            Assert.Equal(10, view.Players[0].LibraryCount);
            Assert.Contains(MineName, JsonSerializer.Serialize(view), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The other half of CR 401.5: the effect names one player's library, so no other library
    /// reveals anything, and a game with no such effect reveals nothing at all — the behaviour
    /// every test written before this wave asserts.
    /// </summary>
    [Fact]
    [RuleRef("401.5")]
    public void A_library_with_no_such_effect_reveals_nothing()
    {
        var plain = Project(Start(openingHand: 0), Me);

        Assert.Null(plain.Players[0].RevealedTopCard);
        Assert.Null(plain.Players[1].RevealedTopCard);

        // With the effect on the first seat only, the second seat's library is still shut.
        var revealing = Project(Revealing(Start(openingHand: 0), Me), Them);

        Assert.NotNull(revealing.Players[0].RevealedTopCard);
        Assert.Null(revealing.Players[1].RevealedTopCard);
        Assert.DoesNotContain(TheirsName, JsonSerializer.Serialize(revealing), StringComparison.Ordinal);
    }

    /// <summary>
    /// The projected top updates after the previous top card leaves the library. This does not
    /// test CR 401.6: hiding and revealing the same physical card without changing its zone.
    /// </summary>
    [Fact]
    [RuleRef("401.5")]
    public void The_revealed_card_is_whichever_object_is_on_top_now()
    {
        var state = Revealing(Start(openingHand: 0), Me);
        var first = Project(state, Them).Players[0].RevealedTopCard;

        var top = state.In(ZoneKey.Of(Zone.Library, Me))[0];
        state = new ZoneSystem().Move(state, top, ZoneKey.Of(Zone.Graveyard, Me), controller: Me).State;

        var second = Project(state, Them).Players[0].RevealedTopCard;
        Assert.NotNull(second);
        Assert.NotNull(first);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(state.In(ZoneKey.Of(Zone.Library, Me))[0], second.Id);
    }

    /// <summary>CR 401.5 reveals a card, and an empty library has none to reveal.</summary>
    [Fact]
    [RuleRef("401.5")]
    public void An_empty_library_reveals_nothing()
    {
        var state = Revealing(Start(openingHand: 0), Me);
        var zones = new ZoneSystem();
        foreach (var card in state.In(ZoneKey.Of(Zone.Library, Me)))
            state = zones.Move(state, card, ZoneKey.Of(Zone.Graveyard, Me), controller: Me).State;

        var view = Project(state, Me);

        Assert.Equal(0, view.Players[0].LibraryCount);
        Assert.Null(view.Players[0].RevealedTopCard);
    }

    /// <summary>
    /// CR 508.1d: a taxed creature can attack, so it is offered, and the offer says what it would
    /// cost. The price comes from the subsystem that would charge it — the view never reads
    /// CR 508.1h itself — and the attacker on the offer is the one asked about, whatever the
    /// answer claims.
    /// </summary>
    [Fact]
    [RuleRef("508.1d")]
    public void Each_taxed_attacker_is_offered_with_its_price()
    {
        var state = Start(openingHand: 0);
        var taxed = new ObjectId(41);
        var free = new ObjectId(42);
        var unpayable = new ObjectId(43);
        var taxes = new E14cAttackTaxes
        {
            Answers =
            {
                [taxed] = new AttackTaxOffer(new ObjectId(-1), "{4}", Payable: true),
                [unpayable] = new AttackTaxOffer(new ObjectId(-1), "sacrifice a land", Payable: false),
            },
        };

        var offers = AttackTaxes.For(taxes, state, Me, [taxed, free, unpayable]);

        Assert.Equal(2, offers.Count);
        Assert.Equal(taxed, offers[0].Attacker);
        Assert.Equal("{4}", offers[0].Cost);
        Assert.True(offers[0].Payable);

        // CR 508.1h's refusal is an offer that cannot be paid, not a missing attacker: the player
        // is told the price exists and that this game cannot charge it.
        Assert.Equal(unpayable, offers[1].Attacker);
        Assert.False(offers[1].Payable);

        // Every eligible attacker was asked about exactly once, including the one owing nothing.
        Assert.Equal([taxed, free, unpayable], taxes.Asked);
    }

    /// <summary>
    /// An ordinary combat prices nothing, and a game composed without the seam says nothing rather
    /// than guessing at CR 508.1h — the bargain the view already makes without a combat system.
    /// </summary>
    [Fact]
    [RuleRef("508.1d")]
    public void No_tax_and_no_seam_both_offer_nothing()
    {
        var state = Start(openingHand: 0);

        Assert.Empty(AttackTaxes.For(new E14cAttackTaxes(), state, Me, [new ObjectId(41)]));
        Assert.Empty(AttackTaxes.For(null, state, Me, [new ObjectId(41)]));
        Assert.Empty(AttackTaxes.For(new E14cAttackTaxes(), state, Me, []));

        // The view's own default is the same answer, so a legal-actions record built by a game
        // that never heard of the tax carries an empty list and not a null.
        Assert.Empty(new LegalActions([], [], [], [], [], MayPass: true, MustAnswerChoice: false).AttackTaxes);
    }

    /// <summary>CR 401.5's effect as the interpreter creates it: against the player whose library it is.</summary>
    [RuleRef("401.5")]
    private static GameState Revealing(GameState state, PlayerId owner) =>
        state with
        {
            ActiveEffects = state.ActiveEffects.Add(new ActiveEffect(
                1,
                new PlayWithTopCardRevealed(new You()),
                null,
                owner,
                [],
                [owner],
                new UntilEndOfTurn(),
                state.NextTimestamp,
                FromStaticAbility: true)),
        };

    /// <summary>A stand-in for the subsystem that determines and pays CR 508.1h's total.</summary>
    private sealed class E14cAttackTaxes : IAttackTaxes
    {
        public Dictionary<ObjectId, AttackTaxOffer> Answers { get; } = [];

        public List<ObjectId> Asked { get; } = [];

        public AttackTaxOffer? TaxToAttack(GameState state, PlayerId player, ObjectId attacker)
        {
            Asked.Add(attacker);
            return Answers.GetValueOrDefault(attacker);
        }
    }
}
