using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.Replacement;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 28: the prevention says how much it prevented (CR 615.13), damage that can't be prevented
/// loses none of itself and spends no shield (CR 615.12), and the two riders V15 and V16 added to
/// the replaced events are read (CR 605.1a with 106.12, CR 121.1). Every definition here is
/// invented; no card name reaches the engine or its tests.
/// </summary>
public sealed partial class ReplacementEffectTests
{
    /// <summary>CR 615.13: a shield that meets 3 of 5 says it prevented 3 — not 5, and not the 2 dealt.</summary>
    [Fact]
    public void A_shield_that_prevents_some_damage_says_how_much()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(4, 4, "wave28 shielded thing"));
        var burner = board.Put(Board.Creature(5, 5, "wave28 burner"), owner: 1);
        var ward = board.Put(Board.Definition("wave28 ward", CardType.Enchantment));

        var outcome = System().Replace(Wave28Shield(board.State, creature, new Fixed(3), ward), Damage(burner, creature, 5), new PlayerId(0));

        var said = Wave28Prevention(outcome);
        Assert.Equal(3, said.Prevented);
        Assert.Equal(burner, said.Source);
        Assert.Equal(creature, said.ToObject);
        Assert.Null(said.ToPlayer);
        Assert.Equal(ward, said.PreventedBy);
        Assert.Equal(2, Assert.IsType<DamageDealt>(outcome.Events[^1]).Amount);
    }

    /// <summary>
    /// CR 615.13 with 615.1: a prevention that takes all of it leaves no damage event at all, which
    /// is exactly the case the log could not speak about before — the whole reason for the event.
    /// </summary>
    [Fact]
    public void A_total_prevention_still_records_that_it_happened()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(4, 4, "wave28 warded thing"));
        var burner = board.Put(Board.Creature(5, 5, "wave28 burner"), owner: 1);

        var outcome = System().Replace(Wave28Shield(board.State, creature, null, null), Damage(burner, creature, 5), new PlayerId(0));

        Assert.DoesNotContain(outcome.Events, e => e is DamageDealt);
        Assert.Equal(5, Wave28Prevention(outcome).Prevented);
        Assert.Null(Wave28Prevention(outcome).PreventedBy);
    }

    /// <summary>
    /// CR 615.13: a <c>PreventThatDamage</c> on the replacement road records what it took off, and
    /// the amount is what came off the event rather than what the effect offered — an effect that
    /// offers to prevent 9 of 5 prevented 5.
    /// </summary>
    [Fact]
    public void A_prevent_that_damage_records_what_came_off_the_event()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(9, 9, "wave28 tough thing"));
        var burner = board.Put(Board.Creature(5, 5, "wave28 burner"), owner: 1);
        var watcher = board.Put(WithReplacement("wave28 shielding aura", CardType.Enchantment, Wave28DamageToCreature, new PreventThatDamage(new Fixed(9))));

        var outcome = System().Replace(board.State, Damage(burner, creature, 5), new PlayerId(0));

        var said = Wave28Prevention(outcome);
        Assert.Equal(5, said.Prevented);
        Assert.Equal(watcher, said.PreventedBy);
    }

    /// <summary>
    /// CR 615.12: an applicable prevention effect is still applied to unpreventable damage, prevents
    /// none of it, and — CR 615.12's last sentence — the existing shield is not reduced by it. So
    /// the whole 5 is dealt, the shield of 3 is still in force for the next damage, and nothing is
    /// recorded as prevented, which is what keeps a CR 615.13 card from firing on it.
    /// </summary>
    [Fact]
    public void Unpreventable_damage_prevents_nothing_and_spends_no_shield()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(9, 9, "wave28 shielded thing"));
        var burner = board.Put(Board.Creature(5, 5, "wave28 burner"), owner: 1);
        var state = Wave28Unpreventable(Wave28Shield(board.State, creature, new Fixed(3), null));

        var outcome = System().Replace(state, Damage(burner, creature, 5), new PlayerId(0));

        Assert.Equal(5, Assert.IsType<DamageDealt>(Assert.Single(outcome.Events)).Amount);
        Assert.Equal(5, outcome.State.Objects[creature].Damage);
        Assert.Equal(new PreventDamage(new Fixed(3)), Assert.Single(outcome.State.ActiveEffects, e => e.Effect is PreventDamage).Effect);
        Assert.DoesNotContain(outcome.Events, e => e is ActiveEffectChanged);
    }

    /// <summary>
    /// CR 615.12: "those effects won't prevent any damage, but any additional effects they have will
    /// take place". The prevention is one step of a sequence and the step beside it still runs, so
    /// the damage arrives modified and unprevented. CR 615.12a's "just once" is the used set: the
    /// effect is offered the event a single time and the loop ends rather than trying again.
    /// </summary>
    [Fact]
    public void Unpreventable_damage_still_gets_the_rest_of_the_effect()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(9, 9, "wave28 tough thing"));
        var burner = board.Put(Board.Creature(5, 5, "wave28 burner"), owner: 1);
        board.Put(WithReplacement(
            "wave28 hardened skin",
            CardType.Enchantment,
            Wave28DamageToCreature,
            new Sequence([new PreventThatDamage(new Fixed(2)), new ModifyAmount(Add: 1)])));

        var outcome = System().Replace(Wave28Unpreventable(board.State), Damage(burner, creature, 5), new PlayerId(0));

        Assert.Equal(6, Assert.IsType<DamageDealt>(Assert.Single(outcome.Events)).Amount);
        Assert.DoesNotContain(outcome.Events, e => e is ReplacementSystem.DamagePrevented);
    }

    /// <summary>
    /// CR 615.12: the effect names the damage it makes unpreventable and not who takes it, so a
    /// <c>From</c> it does not admit leaves the prevention working normally.
    /// </summary>
    [Fact]
    public void Damage_from_a_source_the_effect_does_not_name_is_still_preventable()
    {
        var board = new Board();
        var creature = board.Put(Board.Creature(9, 9, "wave28 shielded thing"));
        var burner = board.Put(Board.Creature(5, 5, "wave28 burner"), owner: 1);
        var state = Wave28Unpreventable(Wave28Shield(board.State, creature, new Fixed(3), null), new ObjectFilter(Types: CardType.Enchantment));

        var outcome = System().Replace(state, Damage(burner, creature, 5), new PlayerId(0));

        Assert.Equal(3, Wave28Prevention(outcome).Prevented);
        Assert.Equal(2, outcome.State.Objects[creature].Damage);
    }

    /// <summary>
    /// CR 605.1a with 106.12: the rider replaces mana a mana ability added for a permanent that was
    /// tapped to pay for it, which the pool's provenance names (CR 106.6).
    /// </summary>
    [Fact]
    public void Mana_added_by_a_permanent_tapped_for_it_is_replaced()
    {
        var board = new Board();
        var land = board.Put(Board.Definition("wave28 quiet field", CardType.Land));
        board.Put(WithReplacement("wave28 doubler", CardType.Enchantment, new WouldAddMana(new You(), TappedFor: true), new ReplaceWith(new Untap(new Self()))));

        var outcome = System().Replace(Wave28Tapped(board.State, land), Wave28Mana(land), new PlayerId(0));

        Assert.Empty(outcome.Events);
    }

    /// <summary>
    /// CR 106.12: to tap a permanent for mana is to activate a mana ability of it that includes
    /// {T}. Mana from a source that is not tapped was not tapped for, so the rider does not admit
    /// it and the mana is added unchanged — the reading a bare <c>FromSource</c> could not make.
    /// </summary>
    [Fact]
    public void Mana_from_a_source_that_is_not_tapped_is_not_replaced()
    {
        var board = new Board();
        var land = board.Put(Board.Definition("wave28 quiet field", CardType.Land));
        board.Put(WithReplacement("wave28 doubler", CardType.Enchantment, new WouldAddMana(new You(), TappedFor: true), new ReplaceWith(new Untap(new Self()))));

        var outcome = System().Replace(board.State, Wave28Mana(land), new PlayerId(0));

        Assert.IsType<ManaChanged>(Assert.Single(outcome.Events));
    }

    /// <summary>
    /// CR 121.1: "except the first one they draw in each of their draw steps". The draw step's own
    /// first draw is spared, so the replacement does not apply to it and the card is drawn.
    /// </summary>
    [Fact]
    public void The_draw_steps_first_draw_is_spared()
    {
        var board = new Board();
        var card = board.Put(Board.Definition("wave28 drawn card", CardType.Instant), Zone.Library);
        board.Put(WithReplacement("wave28 tax", CardType.Enchantment, new WouldDraw(new You(), ExceptFirstInDrawStep: true), new ReplaceWith(new Untap(new Self()))));

        var outcome = System().Replace(Wave28DrawStep(board.State, drawnAlready: 0), new ObjectDrawn(new PlayerId(0), card), new PlayerId(0));

        Assert.IsType<ObjectDrawn>(Assert.Single(outcome.Events));
    }

    /// <summary>
    /// CR 121.1: every draw but that one is replaced, so the second draw of the same draw step is.
    /// The count is the step's and not the turn's, which is what keeps an upkeep draw out of it.
    /// </summary>
    [Fact]
    public void A_later_draw_in_the_same_draw_step_is_replaced()
    {
        var board = new Board();
        var card = board.Put(Board.Definition("wave28 drawn card", CardType.Instant), Zone.Library);
        board.Put(WithReplacement("wave28 tax", CardType.Enchantment, new WouldDraw(new You(), ExceptFirstInDrawStep: true), new ReplaceWith(new Untap(new Self()))));

        var outcome = System().Replace(Wave28DrawStep(board.State, drawnAlready: 1), new ObjectDrawn(new PlayerId(0), card), new PlayerId(0));

        Assert.Empty(outcome.Events);
    }

    /// <summary>The one prevention the outcome recorded, CR 615.13.</summary>
    private static ReplacementSystem.DamagePrevented Wave28Prevention(MtgEngine.Rules.Engine.Outcome outcome) =>
        Assert.IsType<ReplacementSystem.DamagePrevented>(Assert.Single(outcome.Events, e => e is ReplacementSystem.DamagePrevented));

    /// <summary>Damage to any creature, the shape a prevention replacement watches for (CR 615.1).</summary>
    private static WouldBeDealtDamage Wave28DamageToCreature { get; } = new(ObjectFilter.Creature);

    /// <summary>A shield that knows which object put it there, so "this way" has something to name (CR 615.13).</summary>
    private static GameState Wave28Shield(GameState state, ObjectId what, Amount? amount, ObjectId? source) =>
        state with { ActiveEffects = [new ActiveEffect(1, new PreventDamage(amount), source, new PlayerId(0), [what], [], new Permanently(), 1, FromStaticAbility: false)] };

    /// <summary>CR 615.12: the effect names the damage, so it is created against the players and narrows no recipient (E9v, CR 102.1).</summary>
    private static GameState Wave28Unpreventable(GameState state, ObjectFilter? from = null) =>
        state with
        {
            ActiveEffects = state.ActiveEffects.Add(
                new ActiveEffect(2, new DamageCantBePrevented(from), null, new PlayerId(0), [], [new PlayerId(0), new PlayerId(1)], new Permanently(), 2, FromStaticAbility: false)),
        };

    private static GameState Wave28Tapped(GameState state, ObjectId what) =>
        state with { Objects = state.Objects.SetItem(what, state.Objects[what] with { Tapped = true }) };

    /// <summary>One green mana remembering the permanent that produced it, CR 106.6.</summary>
    private static ManaChanged Wave28Mana(ObjectId source) =>
        new(new PlayerId(0), ManaPool.Empty with { Green = 1, Provenance = [new ManaProvenance(Color.Green, 1, source)] }, "mana ability");

    /// <summary>The active player's own draw step, with however many draws already taken in it (CR 500.1).</summary>
    private static GameState Wave28DrawStep(GameState state, int drawnAlready) =>
        state with
        {
            Turn = state.Turn with { Step = Step.Draw, ActivePlayer = new PlayerId(0) },
            Players = state.Players.SetItem(0, state.Player(new PlayerId(0)) with { ObjectsDrawnThisStep = drawnAlready }),
        };
}
