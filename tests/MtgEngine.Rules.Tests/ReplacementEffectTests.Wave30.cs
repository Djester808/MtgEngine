using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Query;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 30: a damage replacement written about a recipient reads the recipient (CR 614.1), and a
/// permanent that enters prepared gains the designation as it enters (CR 722.3a with 614.1c).
/// Every definition here is invented; no card name reaches the engine or its tests.
/// </summary>
public sealed partial class ReplacementEffectTests
{
    /// <summary>
    /// CR 614.1: "if a source you control would deal damage to a creature" — the object half of the
    /// recipient. Damage to a creature it admits is the damage the effect replaces.
    /// </summary>
    [Fact]
    public void A_damage_replacement_that_names_an_object_recipient_reads_that_object()
    {
        var board = new Board();
        var burner = board.Put(Board.Creature(3, 3, "wave30 burner"));
        var creature = board.Put(Board.Creature(9, 9, "wave30 struck thing"), owner: 1);
        board.Put(Wave30Doubler(new WouldDealDamage(ObjectFilter.Creature, To: ObjectFilter.Creature)));

        var outcome = System().Replace(board.State, Damage(burner, creature, 3), new PlayerId(0));

        Assert.Equal(6, Assert.IsType<DamageDealt>(Assert.Single(outcome.Events)).Amount);
    }

    /// <summary>
    /// CR 614.1: the same effect is not written about a player, so damage to one is not its event
    /// and lands as it was. Without the recipient read this doubled too.
    /// </summary>
    [Fact]
    public void A_damage_replacement_that_names_an_object_recipient_passes_over_a_player()
    {
        var board = new Board();
        var burner = board.Put(Board.Creature(3, 3, "wave30 burner"));
        board.Put(Wave30Doubler(new WouldDealDamage(ObjectFilter.Creature, To: ObjectFilter.Creature)));

        var outcome = System().Replace(board.State, DamageToPlayer(burner, 1, 3), new PlayerId(1));

        Assert.Equal(3, Assert.IsType<DamageDealt>(Assert.Single(outcome.Events)).Amount);
    }

    /// <summary>
    /// CR 614.1 with 102.1: the player half. "To an opponent" is read from the controller of the
    /// ability (CR 109.5), so the opponent's damage is the effect's event.
    /// </summary>
    [Fact]
    public void A_damage_replacement_that_names_a_player_recipient_reads_that_player()
    {
        var board = new Board();
        var burner = board.Put(Board.Creature(3, 3, "wave30 burner"));
        board.Put(Wave30Doubler(new WouldDealDamage(ObjectFilter.Creature, ToPlayer: PlayerFilter.Opponents)));

        var outcome = System().Replace(board.State, DamageToPlayer(burner, 1, 3), new PlayerId(1));

        Assert.Equal(6, Assert.IsType<DamageDealt>(Assert.Single(outcome.Events)).Amount);
    }

    /// <summary>
    /// CR 102.1: the controller of the ability is not their own opponent, so the same effect leaves
    /// damage to them alone. The filter is a real filter and not a spelling of "any player".
    /// </summary>
    [Fact]
    public void A_damage_replacement_that_names_an_opponent_passes_over_its_own_controller()
    {
        var board = new Board();
        var burner = board.Put(Board.Creature(3, 3, "wave30 burner"));
        board.Put(Wave30Doubler(new WouldDealDamage(ObjectFilter.Creature, ToPlayer: PlayerFilter.Opponents)));

        var outcome = System().Replace(board.State, DamageToPlayer(burner, 0, 3), new PlayerId(0));

        Assert.Equal(3, Assert.IsType<DamageDealt>(Assert.Single(outcome.Events)).Amount);
    }

    /// <summary>
    /// CR 614.1 with 614.5: "to an opponent or a permanent an opponent controls" is one replacement
    /// effect with both halves, and a damage event has one recipient — so the two riders are read as
    /// the printed "or". An intersection would match neither half and the card would do nothing.
    /// </summary>
    [Fact]
    public void A_damage_replacement_naming_both_halves_reads_either_of_them()
    {
        var board = new Board();
        var burner = board.Put(Board.Creature(3, 3, "wave30 burner"));
        var creature = board.Put(Board.Creature(9, 9, "wave30 struck thing"), owner: 1);
        var both = new WouldDealDamage(ObjectFilter.Creature, To: ObjectFilter.Creature, ToPlayer: PlayerFilter.Opponents);
        board.Put(Wave30Doubler(both));

        var atObject = System().Replace(board.State, Damage(burner, creature, 3), new PlayerId(1));
        var atPlayer = System().Replace(board.State, DamageToPlayer(burner, 1, 3), new PlayerId(1));

        Assert.Equal(6, Assert.IsType<DamageDealt>(Assert.Single(atObject.Events)).Amount);
        Assert.Equal(6, Assert.IsType<DamageDealt>(Assert.Single(atPlayer.Events)).Amount);
    }

    /// <summary>
    /// The default the stored corpus depends on (seam-requests/V18.md row 9): a
    /// <see cref="WouldDealDamage"/> that names no recipient is about damage from its source
    /// wherever it lands, so both halves are still its event.
    /// </summary>
    [Fact]
    public void A_damage_replacement_with_no_recipient_named_reads_every_recipient()
    {
        var board = new Board();
        var burner = board.Put(Board.Creature(3, 3, "wave30 burner"));
        var creature = board.Put(Board.Creature(9, 9, "wave30 struck thing"), owner: 1);
        board.Put(Wave30Doubler(new WouldDealDamage(ObjectFilter.Creature)));

        var atObject = System().Replace(board.State, Damage(burner, creature, 3), new PlayerId(1));
        var atPlayer = System().Replace(board.State, DamageToPlayer(burner, 1, 3), new PlayerId(1));

        Assert.Equal(6, Assert.IsType<DamageDealt>(Assert.Single(atObject.Events)).Amount);
        Assert.Equal(6, Assert.IsType<DamageDealt>(Assert.Single(atPlayer.Events)).Amount);
    }

    /// <summary>
    /// CR 722.3a with 614.1c: the permanent gains the prepared designation as it enters, so the
    /// designation rides on the object the move carries and is in the state the move folds into —
    /// the same shape as entering tapped, and not a later event that something could respond to.
    /// </summary>
    [Fact]
    public void A_permanent_with_a_prepare_spell_enters_prepared()
    {
        var board = new Board();
        var card = board.Put(Wave30Preparation("wave30 studious thing", inset: true), Zone.Hand);

        var outcome = System().Replace(board.State, Entering(board.State, card), new PlayerId(0));

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.Contains(Designations.Prepared, moved.To.Designations);
        Assert.Contains(Designations.Prepared, outcome.State.Objects[moved.To.Id].Designations);
    }

    /// <summary>
    /// CR 722.3a: "a permanent can't gain this designation unless it has a prepare spell." The
    /// permanent still enters — the entry is not refused, only the designation is — so a card whose
    /// inset frame the definition does not carry plays as an ordinary permanent.
    /// </summary>
    [Fact]
    public void A_permanent_with_no_prepare_spell_cannot_enter_prepared()
    {
        var board = new Board();
        var card = board.Put(Wave30Preparation("wave30 plain thing", inset: false), Zone.Hand);

        var outcome = System().Replace(board.State, Entering(board.State, card), new PlayerId(0));

        var moved = Assert.IsType<ObjectMoved>(Assert.Single(outcome.Events));
        Assert.Empty(moved.To.Designations);
        Assert.Equal(Zone.Battlefield, outcome.State.Objects[moved.To.Id].Zone.Zone);
    }

    /// <summary>An enchantment that doubles whatever damage its one replacement is written about.</summary>
    private static CardDefinition Wave30Doubler(WouldDealDamage instead) =>
        Board.Definition(
            "wave30 doubling engine",
            CardType.Enchantment,
            abilities: [new ReplacementAbility(instead, new ModifyAmount(Multiply: 2)) { Text = "wave30 doubling engine" }]);

    /// <summary>
    /// A creature that says it enters prepared, with and without the inset frame of CR 722.2a —
    /// the prepare-spell face that decides whether the designation may be gained at all.
    /// </summary>
    private static CardDefinition Wave30Preparation(string name, bool inset)
    {
        var main = new Face(
            name,
            FaceRole.Main,
            new TypeLine(CardType.Creature, Supertype.None, ["Beast"]),
            Power: new PrintedStat("2"),
            Toughness: new PrintedStat("2"),
            Abilities: [new ReplacementAbility(new WouldEnterBattlefield(ObjectFilter.Creature), new EntersPrepared(), SelfOnly: true) { Text = name }]);

        var prepare = new Face(name, FaceRole.PrepareSpell, new TypeLine(CardType.Sorcery));

        return new CardDefinition(name, name, inset ? Layout.Prepare : Layout.Normal, inset ? [main, prepare] : [main]);
    }
}
