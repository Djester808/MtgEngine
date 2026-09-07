using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The attachment arms of CR 704.5: 704.5m for an Aura attached to an illegal object, an
/// illegal player or to nothing, and 704.5n for an Equipment or Fortification on an illegal
/// host. Split out of <c>StateBasedActionTests.cs</c> when that file passed 500 lines; every
/// test here was there before, unchanged, except the ones this file adds for
/// <see cref="KeywordAbility.OfPlayer"/>.
/// </summary>
public sealed partial class StateBasedActionTests
{
    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_attached_to_nothing_is_put_into_its_owners_graveyard()
    {
        var board = new Board();
        board.Put(Aura());

        var outcome = sba.Check(board.State);

        Assert.Equal("put into graveyard", Assert.Single(outcome.Events.OfType<ObjectLeftBy>()).Verb);
        Assert.Equal(Zone.Graveyard, Zoned(outcome.State, "sba binding"));
    }

    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_on_an_illegal_object_is_put_into_its_owners_graveyard()
    {
        var board = new Board();
        var land = board.Put(Board.Definition("sba waste", CardType.Land));
        board.Put(Aura(), attachedTo: land);

        Assert.Equal(Zone.Graveyard, Zoned(sba.Check(board.State).State, "sba binding"));
    }

    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_on_a_legal_creature_stays()
    {
        var board = new Board();
        var host = board.Put(Board.Creature(2, 2));
        board.Put(Aura(), attachedTo: host);

        Assert.Empty(sba.Check(board.State).Events);
    }

    [Fact]
    [RuleRef("704.5n")]
    public void Equipment_attached_to_a_noncreature_becomes_unattached()
    {
        var board = new Board();
        var land = board.Put(Board.Definition("sba waste", CardType.Land));
        var equipment = board.Put(Board.Definition("sba blade", CardType.Artifact, subtypes: ["Equipment"]), attachedTo: land);

        var outcome = sba.Check(board.State);

        var changed = Assert.Single(outcome.Events.OfType<ObjectChanged>());
        Assert.Equal(equipment, changed.Id);
        Assert.True(changed.ClearAttachment);

        // It remains on the battlefield: 704.5n unattaches, it does not destroy.
        Assert.Equal(Zone.Battlefield, outcome.State.Objects[equipment].Zone.Zone);
        Assert.Null(outcome.State.Objects[equipment].AttachedTo);
    }

    [Fact]
    [RuleRef("704.5n")]
    public void Equipment_attached_to_a_creature_stays_attached()
    {
        var board = new Board();
        var host = board.Put(Board.Creature(2, 2));
        board.Put(Board.Definition("sba blade", CardType.Artifact, subtypes: ["Equipment"]), attachedTo: host);

        Assert.Empty(sba.Check(board.State).Events);
    }

    /// <summary>
    /// CR 704.5m with 205.3d: an Aura that enchants one creature type is legally attached to a
    /// changeling, which is every creature type (CR 702.72a) without carrying any of the names.
    /// The Equipment beside it is the other half of 205.3d: being every creature type is not
    /// being every subtype, so the changeling is a legal host for 704.5n and not an Aura itself.
    /// </summary>
    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_enchanting_one_creature_type_stays_on_a_changeling()
    {
        var board = new Board();
        var host = board.Put(Changeling());
        board.Put(Aura(new ObjectFilter(Subtypes: ["Goblin"])), attachedTo: host);
        board.Put(Board.Definition("sba blade", CardType.Artifact, subtypes: ["Equipment"]), attachedTo: host);

        Assert.Empty(sba.Check(board.State).Events);
    }

    /// <summary>
    /// CR 205.3m: "loses all creature types" takes exactly those away, so the changeling shares
    /// no creature type with anything and its Aura's enchant ability is no longer satisfied —
    /// 704.5m then buries the Aura like any other illegally attached one.
    /// </summary>
    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_enchanting_one_creature_type_is_buried_when_its_changeling_loses_them_all()
    {
        var board = new Board();
        var host = board.Put(ChangelingThatLostEveryCreatureType());
        board.Put(Aura(new ObjectFilter(Subtypes: ["Goblin"])), attachedTo: host);

        var outcome = sba.Check(board.State);

        Assert.Equal("put into graveyard", Assert.Single(outcome.Events.OfType<ObjectLeftBy>()).Verb);
        Assert.Equal(Zone.Graveyard, Zoned(outcome.State, "sba binding"));
        // The host itself is untouched: it is still a creature, just one with no creature type.
        Assert.Equal(Zone.Battlefield, outcome.State.Objects[host].Zone.Zone);
    }

    /// <summary>
    /// CR 303.4f: an Aura enters attached to the object <em>or player</em> it was cast at, and
    /// CR 704.5m asks the same question of both halves. An enchant ability that names neither an
    /// object filter nor a player filter is "enchant anything", so this one stays where an Aura
    /// that names a creature would be buried. That is E5c's reading, kept.
    /// </summary>
    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_that_enchants_a_player_stays_on_that_player()
    {
        var board = new Board();
        board.Put(PlayerAura(), attachedToPlayer: 1);

        Assert.Empty(sba.Check(board.State).Events);
    }

    /// <summary>
    /// CR 704.5m: the other direction. An enchant ability that names an object filter is a
    /// restriction to objects, so the player is not something it may enchant and the Aura goes.
    /// This is the arm that keeps the state field from making every Aura legal on anyone.
    /// </summary>
    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_that_enchants_a_creature_is_buried_when_it_is_attached_to_a_player()
    {
        var board = new Board();
        board.Put(Aura(), attachedToPlayer: 1);

        var outcome = sba.Check(board.State);

        Assert.Equal("put into graveyard", Assert.Single(outcome.Events.OfType<ObjectLeftBy>()).Verb);
        Assert.Equal(Zone.Graveyard, Zoned(outcome.State, "sba binding"));
    }

    /// <summary>
    /// CR 702.5b: "enchant opponent" on an opponent. The Aura's controller is seat 0 (the owner
    /// a board gives by default), it is attached to seat 1, and the filter is satisfied.
    /// </summary>
    [Fact]
    [RuleRef("702.5b")]
    public void An_aura_that_enchants_an_opponent_stays_on_an_opponent()
    {
        var board = new Board();
        board.Put(PlayerAura(PlayerFilter.Opponents), attachedToPlayer: 1);

        Assert.Empty(sba.Check(board.State).Events);
    }

    /// <summary>
    /// CR 702.5b with 704.5m: the same Aura on its own controller. "Enchant opponent" is not
    /// satisfied by the controller, so the attachment is illegal and the Aura is put into its
    /// owner's graveyard. Without <see cref="KeywordAbility.OfPlayer"/> being read this stays,
    /// because an enchant ability with no object filter admitted any player at all.
    /// </summary>
    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_that_enchants_an_opponent_is_buried_on_its_own_controller()
    {
        var board = new Board();
        board.Put(PlayerAura(PlayerFilter.Opponents), attachedToPlayer: 0);

        var outcome = sba.Check(board.State);

        Assert.Equal("put into graveyard", Assert.Single(outcome.Events.OfType<ObjectLeftBy>()).Verb);
        Assert.Equal(Zone.Graveyard, Zoned(outcome.State, "sba curse"));
    }

    /// <summary>
    /// CR 702.5b: "enchant player" restricts nothing about who, so it is legal on either seat.
    /// Both are checked in one board because 704.3 performs every action from one snapshot.
    /// </summary>
    [Fact]
    [RuleRef("702.5b")]
    public void An_aura_that_enchants_any_player_stays_on_either_of_them()
    {
        var board = new Board();
        board.Put(PlayerAura(PlayerFilter.Any), attachedToPlayer: 0);
        board.Put(PlayerAura(PlayerFilter.Any), attachedToPlayer: 1);

        Assert.Empty(sba.Check(board.State).Events);
    }

    /// <summary>
    /// CR 702.5b: "enchant you". The mirror of the opponent pair, so the <c>IsYou</c> half of the
    /// filter is read and not merely carried.
    /// </summary>
    [Fact]
    [RuleRef("702.5b")]
    public void An_aura_that_enchants_you_is_buried_on_an_opponent()
    {
        var board = new Board();
        board.Put(PlayerAura(new PlayerFilter(IsYou: true)), attachedToPlayer: 0);
        board.Put(PlayerAura(new PlayerFilter(IsYou: true)), attachedToPlayer: 1);

        var outcome = sba.Check(board.State);

        // One of the two goes: the one on seat 1, which is not its controller.
        var left = Assert.Single(outcome.Events.OfType<ObjectLeftBy>());
        Assert.Equal(new PlayerId(1), board.State.Objects[left.Object].AttachedToPlayer);
    }

    /// <summary>
    /// CR 702.5b's <c>Also</c> half, and the point of view it is asked from: the condition is a
    /// restriction on the <em>candidate</em>, so <c>You</c> inside it means the player being
    /// filtered, not the Aura's controller. Seat 0 is the active player, so "enchant a player
    /// whose turn it is" is satisfied on seat 0 and fails on seat 1; asked from the controller's
    /// point of view instead, both would stay and this test would fail on the second assertion.
    /// The divergence with <c>TriggerMatching.PlayerMatches</c> is in <c>seam-requests/E5d.md</c>.
    /// </summary>
    [Fact]
    [RuleRef("702.5b")]
    public void An_enchant_players_condition_is_asked_of_the_player_it_filters()
    {
        var whoseTurnItIs = new PlayerFilter(Also: new IsYourTurn());

        var stays = new Board();
        stays.Put(PlayerAura(whoseTurnItIs), attachedToPlayer: 0);
        Assert.Empty(sba.Check(stays.State).Events);

        var goes = new Board();
        goes.Put(PlayerAura(whoseTurnItIs), attachedToPlayer: 1);
        Assert.Equal(Zone.Graveyard, Zoned(sba.Check(goes.State).State, "sba curse"));
    }

    /// <summary>
    /// CR 800.4a with 704.5m: a player who has left the game is no longer a player, so the Aura
    /// on them is attached to nothing and is put into its owner's graveyard. Three seats, because
    /// with two the game would already be over and 704.3 performs nothing once it is (104.2a).
    /// </summary>
    [Fact]
    [RuleRef("800.4a")]
    public void An_aura_on_a_player_who_left_the_game_is_put_into_its_owners_graveyard()
    {
        var board = new Board(players: 3);
        board.Put(PlayerAura(), attachedToPlayer: 1);
        board.Lost(1);

        var outcome = sba.Check(board.State);

        Assert.Equal(Zone.Graveyard, Zoned(outcome.State, "sba curse"));
    }

    /// <summary>
    /// CR 704.5m's third case, and the one E3e's request said could not be half landed: attached
    /// to neither an object nor a player, an Aura still goes, whatever it may enchant.
    /// </summary>
    [Fact]
    [RuleRef("704.5m")]
    public void An_aura_that_enchants_a_player_but_is_attached_to_nothing_still_goes()
    {
        var board = new Board();
        board.Put(PlayerAura());

        Assert.Equal(Zone.Graveyard, Zoned(sba.Check(board.State).State, "sba curse"));
    }

    private static CardDefinition Aura(ObjectFilter? enchants = null) =>
        Board.Definition(
            "sba binding",
            CardType.Enchantment,
            subtypes: ["Aura"],
            abilities: [new KeywordAbility(KeywordKind.Enchant, Of: enchants ?? new ObjectFilter(Types: CardType.Creature))]);

    /// <summary>
    /// "Enchant player", "enchant opponent": an enchant ability that restricts nothing about an
    /// object. With no filter at all it is "enchant anything", CR 702.5b.
    /// </summary>
    private static CardDefinition PlayerAura(PlayerFilter? enchants = null) =>
        Board.Definition(
            "sba curse",
            CardType.Enchantment,
            subtypes: ["Aura"],
            abilities: [new KeywordAbility(KeywordKind.Enchant, OfPlayer: enchants)]);

    /// <summary>CR 702.72a: written as the vocabulary writes it, an <see cref="AddTypes"/> on itself.</summary>
    private static CardDefinition Changeling() =>
        Board.Definition(
            "sba mimic",
            CardType.Creature,
            2,
            2,
            subtypes: ["Shapeshifter"],
            abilities: [new StaticAbility(new AddTypes(AllCreatureTypes: true), To: new Self(), CharacteristicDefining: true)]);

    /// <summary>The same creature under an effect that removes every creature type, CR 205.3m.</summary>
    private static CardDefinition ChangelingThatLostEveryCreatureType() =>
        Board.Definition(
            "sba mimic",
            CardType.Creature,
            2,
            2,
            subtypes: ["Shapeshifter"],
            abilities:
            [
                new StaticAbility(new AddTypes(AllCreatureTypes: true), To: new Self(), CharacteristicDefining: true),
                new StaticAbility(new RemoveTypes(AllCreatureTypes: true), To: new Self()),
            ]);
}
