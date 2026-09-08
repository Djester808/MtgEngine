using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 29: X reaches the replacement that reads it (CR 601.2b with 107.3b), a prevention says what
/// it took off the event so the rest of the same effect can read it (CR 615), and a card an effect
/// forbids does not enter the battlefield at all (CR 613.11 with 111.5). Every definition here is
/// invented; no card name reaches the engine or its tests.
/// </summary>
public sealed partial class ReplacementEffectTests
{
    /// <summary>
    /// CR 601.2b with 107.3b: the permanent enters with as many counters as X was paid for. Read as
    /// none, the amount is 0 (CR 107.3b), the creature is a 0/0 and state-based actions bury it
    /// (CR 704.5f) — the failure this closes for every "enters with X counters" card.
    /// </summary>
    [Fact]
    public void A_permanent_enters_with_the_X_its_spell_was_cast_for()
    {
        var board = new Board();
        var spell = board.Put(
            Board.Definition("wave29 counting engine", CardType.Creature, 0, 0, abilities:
                [new ReplacementAbility(new WouldEnterBattlefield(ObjectFilter.Creature), new EntersWithCounters("+1/+1", new X()), SelfOnly: true)]),
            Zone.Stack,
            stack: Wave29Cast(x: 3));

        var outcome = System().Replace(board.State, Wave29Resolving(board.State, spell), new PlayerId(0));

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.Equal(3, moved.To.Counters["+1/+1"]);
    }

    /// <summary>
    /// CR 107.3b: a spell cast with no X chosen leaves X at zero, and a replacement that reads it
    /// adds nothing — the honest zero, which the same road must still give.
    /// </summary>
    [Fact]
    public void A_permanent_whose_spell_chose_no_X_enters_with_none()
    {
        var board = new Board();
        var spell = board.Put(
            Board.Definition("wave29 counting engine", CardType.Creature, 1, 1, abilities:
                [new ReplacementAbility(new WouldEnterBattlefield(ObjectFilter.Creature), new EntersWithCounters("+1/+1", new X()), SelfOnly: true)]),
            Zone.Stack,
            stack: Wave29Cast(x: null));

        var outcome = System().Replace(board.State, Wave29Resolving(board.State, spell), new PlayerId(0));

        Assert.Empty(Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events)).To.Counters);
    }

    /// <summary>
    /// CR 615: "for each 1 damage prevented this way" — the rest of the effect runs with what this
    /// application actually took off the event, which is 3 of the 5 offered and not the 5 dealt.
    /// </summary>
    [Fact]
    public void The_rest_of_a_prevention_runs_with_what_it_prevented()
    {
        var interpreter = new ReplacementEffectFakeInterpreter();
        var board = new Board();
        var creature = board.Put(Board.Creature(9, 9, "wave29 tough thing"));
        var burner = board.Put(Board.Creature(5, 5, "wave29 burner"), owner: 1);
        board.Put(WithReplacement(
            "wave29 shielding aura",
            CardType.Enchantment,
            new WouldBeDealtDamage(ObjectFilter.Creature),
            new Sequence([new PreventThatDamage(new Fixed(3)), new Untap(new Self())])));

        System(interpreter).Replace(board.State, Damage(burner, creature, 5), new PlayerId(0));

        Assert.Equal("3", Assert.Single(interpreter.Contexts).Chosen[MtgEngine.Rules.Query.Query.PreventedDamageAmount]);
    }

    /// <summary>
    /// CR 615.12 with 615: a prevention applied to damage that can't be prevented prevented nothing,
    /// and nothing is written as 0. The reader refuses a missing key, so a zero that is never written
    /// and a zero that is are two different answers and only one of them is this one.
    /// </summary>
    [Fact]
    public void A_prevention_of_unpreventable_damage_carries_a_zero()
    {
        var interpreter = new ReplacementEffectFakeInterpreter();
        var board = new Board();
        var creature = board.Put(Board.Creature(9, 9, "wave29 tough thing"));
        var burner = board.Put(Board.Creature(5, 5, "wave29 burner"), owner: 1);
        board.Put(WithReplacement(
            "wave29 shielding aura",
            CardType.Enchantment,
            new WouldBeDealtDamage(ObjectFilter.Creature),
            new Sequence([new PreventThatDamage(new Fixed(3)), new Untap(new Self())])));

        System(interpreter).Replace(Wave28Unpreventable(board.State), Damage(burner, creature, 5), new PlayerId(0));

        Assert.Equal("0", Assert.Single(interpreter.Contexts).Chosen[MtgEngine.Rules.Query.Query.PreventedDamageAmount]);
    }

    /// <summary>
    /// CR 615 with 614.1a: a prevention that takes the whole event leaves nothing to modify, and the
    /// rest of the effect still takes place (CR 615.12's last clause is the same reading). Before
    /// this the sequence stopped at the vanished event and the clause beside the prevention was
    /// silently dropped.
    /// </summary>
    [Fact]
    public void A_total_prevention_still_runs_the_rest_of_its_effect()
    {
        var interpreter = new ReplacementEffectFakeInterpreter();
        var board = new Board();
        var creature = board.Put(Board.Creature(9, 9, "wave29 tough thing"));
        var burner = board.Put(Board.Creature(5, 5, "wave29 burner"), owner: 1);
        board.Put(WithReplacement(
            "wave29 shielding aura",
            CardType.Enchantment,
            new WouldBeDealtDamage(ObjectFilter.Creature),
            new Sequence([new PreventThatDamage(new Fixed(9)), new Untap(new Self())])));

        System(interpreter).Replace(board.State, Damage(burner, creature, 5), new PlayerId(0));

        Assert.IsType<Untap>(Assert.Single(interpreter.Ran));
        Assert.Equal("5", Assert.Single(interpreter.Contexts).Chosen[MtgEngine.Rules.Query.Query.PreventedDamageAmount]);
    }

    /// <summary>
    /// An effect with no prevention in it carries no key at all, so the reader can tell "nothing was
    /// prevented" from "no prevention has run" (seam-requests/E5l.md § 1).
    /// </summary>
    [Fact]
    public void An_effect_with_no_prevention_carries_no_prevented_amount()
    {
        var interpreter = new ReplacementEffectFakeInterpreter();
        var board = new Board();
        var creature = board.Put(Board.Creature(9, 9, "wave29 tough thing"));
        var burner = board.Put(Board.Creature(5, 5, "wave29 burner"), owner: 1);
        board.Put(WithReplacement("wave29 shroud", CardType.Enchantment, new WouldBeDealtDamage(ObjectFilter.Creature), new ReplaceWith(new Untap(new Self()))));

        System(interpreter).Replace(board.State, Damage(burner, creature, 5), new PlayerId(0));

        Assert.DoesNotContain(MtgEngine.Rules.Query.Query.PreventedDamageAmount, Assert.Single(interpreter.Contexts).Chosen.Keys);
    }

    /// <summary>
    /// CR 613.11 with 111.5: "creature cards in graveyards and libraries can't enter the
    /// battlefield". The card is admitted where it stands, so the move does not happen and no
    /// replacement is offered it either.
    /// </summary>
    [Fact]
    public void A_card_an_effect_forbids_does_not_enter_the_battlefield()
    {
        var board = new Board();
        var card = board.Put(Board.Creature(2, 2, "wave29 buried thing"), Zone.Graveyard);
        var state = Wave29CantEnter(board.State, new ObjectFilter(Types: CardType.Creature, InAnyOf: [Zone.Graveyard, Zone.Library]));

        var outcome = System().Replace(state, Wave29Returning(state, card), new PlayerId(0));

        Assert.Empty(outcome.Events);
        Assert.Equal(Zone.Graveyard, outcome.State.Objects[card].Zone.Zone);
    }

    /// <summary>
    /// CR 400.1: the plural rider names the zones the restriction reaches and no others, so a card in
    /// a zone it does not name enters as it always did. Without the zone reading every entry would be
    /// forbidden, which is why the two halves are one test each.
    /// </summary>
    [Fact]
    public void A_card_in_a_zone_the_effect_does_not_name_still_enters()
    {
        var board = new Board();
        var card = board.Put(Board.Creature(2, 2, "wave29 held thing"), Zone.Hand);
        var state = Wave29CantEnter(board.State, new ObjectFilter(Types: CardType.Creature, InAnyOf: [Zone.Graveyard, Zone.Library]));

        var outcome = System().Replace(state, Wave29Returning(state, card), new PlayerId(0));

        Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
    }

    /// <summary>
    /// CR 111.5: a token that would be created with a characteristic an effect forbids is never
    /// created. The filter names no zone, so it describes the permanent it would be (CR 110.1) and is
    /// read against the object as it would exist on the battlefield (CR 614.12).
    /// </summary>
    [Fact]
    public void A_token_an_effect_forbids_is_never_created()
    {
        var board = new Board();
        var state = Wave29CantEnter(board.State, ObjectFilter.Creature);
        var token = new GameObject(new ObjectId(state.NextObjectId), Board.Creature(1, 1, "wave29 spirit"), 0, new PlayerId(0), new PlayerId(0), ZoneKey.Shared(Zone.Battlefield), state.NextTimestamp) { IsToken = true };

        var outcome = System().Replace(state, new ObjectCreated(token, -1), new PlayerId(0));

        Assert.Empty(outcome.Events);
        Assert.DoesNotContain(token.Id, outcome.State.Objects.Keys);
    }

    /// <summary>A spell on the stack that chose a value for X as it was cast, CR 601.2b.</summary>
    private static StackEntry Wave29Cast(int? x) =>
        new(null, null, [], [], [], x, IsCopy: false);

    /// <summary>
    /// The move a resolving permanent spell makes, CR 608.3: the spell itself is the departed object,
    /// which is where the value chosen for X still lives.
    /// </summary>
    private static ObjectMoved Wave29Resolving(GameState state, ObjectId spell)
    {
        var obj = state.Objects[spell];
        return new ObjectMoved(
            spell,
            obj with { Id = new ObjectId(state.NextObjectId), Zone = ZoneKey.Shared(Zone.Battlefield), Stack = null, Timestamp = state.NextTimestamp },
            -1,
            Departed: obj);
    }

    /// <summary>A card put onto the battlefield from wherever it is, CR 400.1, carrying what it left.</summary>
    private static ObjectMoved Wave29Returning(GameState state, ObjectId card)
    {
        var obj = state.Objects[card];
        return new ObjectMoved(
            card,
            obj with { Id = new ObjectId(state.NextObjectId), Zone = ZoneKey.Shared(Zone.Battlefield), Timestamp = state.NextTimestamp },
            -1,
            Departed: obj);
    }

    /// <summary>CR 613.11: the rules-modifying effect in force, as a static ability leaves it.</summary>
    private static GameState Wave29CantEnter(GameState state, ObjectFilter what) =>
        state with
        {
            ActiveEffects = state.ActiveEffects.Add(
                new ActiveEffect(9, new CantEnterBattlefield(what), null, new PlayerId(0), [], [], new Permanently(), 9, FromStaticAbility: true)),
        };
}
