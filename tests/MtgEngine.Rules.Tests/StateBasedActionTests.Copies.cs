using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The two state-based actions that take an object out of the game rather than moving it:
/// CR 704.5d for a token and CR 704.5e for a copy. They share one path in the rule, so they
/// share one file here, and each test says which zone set the rule gives its object — that is
/// the whole content of both rules and the only thing that tells them apart.
/// <para>
/// A copy is marked the only way the state marks one, <c>StackEntry.IsCopy</c>: the copy a
/// CR 707.12 copy-in-place makes carries it in the zone the original was in, and a copy of a
/// spell carries it on the stack. No test here asserts that a real card is a copy of anything —
/// a card in a graveyard with no such mark is the control, and it must be left alone.
/// </para>
/// </summary>
public sealed partial class StateBasedActionTests
{
    /// <summary>The mark the state puts on a copy, with nothing else on it: the flag is the whole point.</summary>
    private static StackEntry Copy => new(null, null, [], [], [], null, IsCopy: true);

    [Fact]
    [RuleRef("704.5d")]
    public void A_token_outside_the_battlefield_ceases_to_exist()
    {
        var board = new Board();
        var id = board.Put(Board.Creature(1, 1, "sba token"), Zone.Graveyard, token: true);

        var outcome = sba.Check(board.State);

        Assert.Equal("ceased", Assert.Single(outcome.Events.OfType<ObjectLeftBy>()).Verb);
        Assert.Equal(id, Assert.Single(outcome.Events.OfType<ObjectCeasedToExist>()).Id);
        Assert.DoesNotContain(id, outcome.State.Objects.Keys);
    }

    [Fact]
    [RuleRef("704.5d")]
    public void A_token_on_the_battlefield_stays()
    {
        var board = new Board();
        board.Put(Board.Creature(1, 1, "sba token"), token: true);

        Assert.Empty(sba.Check(board.State).Events);
    }

    /// <summary>
    /// CR 704.5e, first sentence: a copy of a spell is a stack object and nothing else, so
    /// anywhere but the stack it ceases. Exile is the zone a countered or exiled copy would
    /// reach.
    /// </summary>
    [Fact]
    [RuleRef("704.5e")]
    public void A_copy_of_a_spell_off_the_stack_ceases_to_exist()
    {
        var board = new Board();
        var id = board.Put(Board.Definition("sba copy", CardType.Instant), Zone.Exile, stack: Copy);

        var outcome = sba.Check(board.State);

        Assert.Equal(id, Assert.Single(outcome.Events.OfType<ObjectCeasedToExist>()).Id);
    }

    [Fact]
    [RuleRef("704.5e")]
    public void A_copy_of_a_spell_on_the_stack_stays()
    {
        var board = new Board();
        board.Put(Board.Definition("sba copy", CardType.Instant), Zone.Stack, stack: Copy);

        Assert.Empty(sba.Check(board.State).Events);
    }

    /// <summary>
    /// CR 704.5e, second sentence: a copy of a card in a graveyard ceases to exist. This is the
    /// copy CR 707.12 makes in place for a "you may cast a copy" — declined, or cast and put
    /// into the graveyard afterwards, it must not linger there as a second card.
    /// </summary>
    [Fact]
    [RuleRef("704.5e")]
    public void A_copy_of_a_card_in_a_graveyard_ceases_to_exist()
    {
        var board = new Board();
        var id = board.Put(Board.Definition("sba copy", CardType.Instant), Zone.Graveyard, stack: Copy);

        var outcome = sba.Check(board.State);

        Assert.Equal("ceased", Assert.Single(outcome.Events.OfType<ObjectLeftBy>()).Verb);
        Assert.Equal(id, Assert.Single(outcome.Events.OfType<ObjectCeasedToExist>()).Id);
        Assert.DoesNotContain(id, outcome.State.Objects.Keys);

        // Out of the game, not into a graveyard: nothing moved anywhere.
        Assert.Empty(outcome.Events.OfType<ObjectMoved>());
    }

    /// <summary>
    /// The rest of that sentence, and the half 704.5d does not have: the battlefield is a zone a
    /// copy of a card may be in. A copy of a permanent card that was cast and resolved is a
    /// permanent like any other, and a rule that read only "not the stack" would remove it from
    /// the table the moment it arrived.
    /// </summary>
    [Fact]
    [RuleRef("704.5e")]
    public void A_copy_of_a_card_on_the_battlefield_stays()
    {
        var board = new Board();
        var id = board.Put(Board.Creature(2, 2, "sba copy"), stack: Copy);

        var outcome = sba.Check(board.State);

        Assert.Empty(outcome.Events);
        Assert.Contains(id, outcome.State.Objects.Keys);
    }

    /// <summary>
    /// The control for both sentences: a card is a card in every zone, and neither 704.5d nor
    /// 704.5e has anything to say about it. Without this the arm could be "everything in a
    /// graveyard ceases" and every test above would still pass.
    /// </summary>
    [Fact]
    [RuleRef("704.5e")]
    public void A_card_in_a_graveyard_is_untouched()
    {
        var board = new Board();
        var id = board.Put(Board.Definition("sba card", CardType.Instant), Zone.Graveyard);

        var outcome = sba.Check(board.State);

        Assert.Empty(outcome.Events);
        Assert.Contains(id, outcome.State.Objects.Keys);
    }

    /// <summary>
    /// 704.3: both rules are performed in the same check, by the same path, with the same verb —
    /// which is why they are one arm in the rule and one arm in the code.
    /// </summary>
    [Fact]
    [RuleRef("704.3")]
    public void A_token_and_a_copy_cease_together_in_one_check()
    {
        var board = new Board();
        var token = board.Put(Board.Creature(1, 1, "sba token"), Zone.Graveyard, token: true);
        var copy = board.Put(Board.Definition("sba copy", CardType.Instant), Zone.Graveyard, stack: Copy);

        var outcome = sba.Check(board.State);

        Assert.Equal(new[] { token, copy }, outcome.Events.OfType<ObjectCeasedToExist>().Select(e => e.Id));
        Assert.All(outcome.Events.OfType<ObjectLeftBy>(), left => Assert.Equal("ceased", left.Verb));
    }
}
