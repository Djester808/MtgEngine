using System.Globalization;
using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// What a viewer may see, CR 400.2. Every game here is a real one built through
/// <c>Subsystems.Wave1()</c>, and the strongest assertion is the one made on the serialised
/// view: a name a viewer is not entitled to must not appear anywhere in the text, however the
/// projector arranges its records.
/// </summary>
public sealed class HiddenInformationTests
{
    private const string MineOracle = "oracle-mine";
    private const string TheirsOracle = "oracle-theirs";
    private const string MineName = "quiet fixture of the first seat";
    private const string TheirsName = "quiet fixture of the second seat";
    private const string ChangelingOracle = "oracle-changeling";
    private const string ChangelingName = "quiet fixture that is anything";

    private static PlayerId Me => new(0);

    private static PlayerId Them => new(1);

    [Fact]
    [RuleRef("400.2")]
    public void An_opponents_hand_is_a_count_and_never_a_card()
    {
        var state = Start();

        var view = Project(state, Me);

        Assert.Equal(7, view.Players[1].HandCount);
        Assert.Equal(7, view.Hand.Count);
        Assert.All(view.Hand, card => Assert.Equal(MineName, card.Name));

        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain(TheirsName, json, StringComparison.Ordinal);
        Assert.DoesNotContain(TheirsOracle, json, StringComparison.Ordinal);
        Assert.Contains(MineName, json, StringComparison.Ordinal);
    }

    [Fact]
    [RuleRef("401.2")]
    public void A_library_is_a_count_and_never_a_card()
    {
        var state = Start(openingHand: 0);

        var view = Project(state, Me);

        Assert.Empty(view.Hand);
        Assert.Equal(10, view.Players[0].LibraryCount);
        Assert.Equal(10, view.Players[1].LibraryCount);

        // Nothing has left a library, so neither deck's name may be anywhere in the view.
        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain(MineName, json, StringComparison.Ordinal);
        Assert.DoesNotContain(TheirsName, json, StringComparison.Ordinal);
    }

    [Fact]
    [RuleRef("708.2")]
    public void A_face_down_permanent_shows_as_face_down_with_no_name_and_no_definition()
    {
        var (state, id) = MoveFromHand(Start(), Them, ZoneKey.Shared(Zone.Battlefield));
        state = state with { Objects = state.Objects.SetItem(id, state.Objects[id] with { FaceDown = true }) };

        var view = Project(state, Me);

        var permanent = Assert.Single(view.Battlefield);
        Assert.True(permanent.FaceDown);
        Assert.Equal(string.Empty, permanent.Name);
        Assert.Equal(string.Empty, permanent.OracleId);
        Assert.Equal(2, permanent.Power);
        Assert.Equal(2, permanent.Toughness);
        Assert.Equal(new[] { "Creature" }, permanent.Types);
        Assert.Empty(permanent.Keywords);
        Assert.DoesNotContain(TheirsName, JsonSerializer.Serialize(view), StringComparison.Ordinal);
    }

    [Fact]
    [RuleRef("400.2")]
    public void The_public_zones_are_listed_for_everyone()
    {
        var (state, id) = MoveFromHand(Start(), Them, ZoneKey.Of(Zone.Graveyard, Them));

        var view = Project(state, Me);

        var card = Assert.Single(view.Players[1].Graveyard);
        Assert.Equal(id, card.Id);
        Assert.Equal(TheirsName, card.Name);
        Assert.Equal(1, view.Players[1].GraveyardCount);
        Assert.Equal(6, view.Players[1].HandCount);
    }

    [Fact]
    [RuleRef("613.5")]
    public void Every_object_carries_computed_characteristics_and_the_stores_enforcement()
    {
        var (state, _) = MoveFromHand(Start(), Them, ZoneKey.Shared(Zone.Battlefield));

        var view = Project(state, Me);

        var permanent = Assert.Single(view.Battlefield);
        Assert.Equal(2, permanent.Power);
        Assert.Equal(3, permanent.Toughness);
        Assert.Contains("Creature", permanent.Types);
        Assert.Contains("Beast", permanent.Subtypes);
        Assert.Contains(KeywordKind.Flying.ToString(), permanent.Keywords);
        Assert.Equal(Them, permanent.Controller);

        // The store enforces the first seat's card and badges the second's honour-system.
        Assert.False(permanent.Enforced);
        Assert.True(view.Hand[0].Enforced);
    }

    [Fact]
    [RuleRef("101.4")]
    public void Another_players_choice_is_visible_as_a_question_and_never_as_its_options()
    {
        var asked = new PendingChoice(
            new ChoiceId(1),
            Them,
            "discard",
            $"Discard {TheirsName}?",
            [new ChoiceOption("a", TheirsName)],
            1,
            1,
            new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty));
        var state = Start() with { Pending = asked };

        Assert.Equal(asked, Project(state, Them).Pending);

        var onlooker = Project(state, Me);
        Assert.Equal(Them, onlooker.Pending!.Player);
        Assert.Equal("discard", onlooker.Pending.Kind);
        Assert.Empty(onlooker.Pending.Options);
        Assert.DoesNotContain(TheirsName, JsonSerializer.Serialize(onlooker), StringComparison.Ordinal);
    }

    /// <summary>
    /// CR 205.3d: a changeling is every creature type (CR 702.72a) and carries none of the names
    /// in its subtype list, so a view that showed only the list would say it was a Shapeshifter
    /// and nothing else. The flag is what a client asking "is this a Goblin" has to read.
    /// </summary>
    [Fact]
    [RuleRef("205.3d")]
    public void A_changeling_on_the_battlefield_is_shown_as_every_creature_type()
    {
        var state = Start(mine: ChangelingOracle);
        var (board, id) = MoveFromHand(state, Me, ZoneKey.Shared(Zone.Battlefield));

        var shown = Assert.Single(Project(board, Them).Battlefield, o => o.Id == id);

        Assert.True(shown.EveryCreatureType);
        Assert.DoesNotContain("Goblin", shown.Subtypes, StringComparer.OrdinalIgnoreCase);

        // An ordinary creature is not every creature type, so the flag says something.
        var (ordinary, plain) = MoveFromHand(Start(), Me, ZoneKey.Shared(Zone.Battlefield));
        Assert.False(Assert.Single(Project(ordinary, Them).Battlefield, o => o.Id == plain).EveryCreatureType);
    }

    /// <summary>
    /// CR 708.2 and 708.4: a face-down permanent has no subtypes at all, and "every creature
    /// type" is a subtype answer — saying it would name the changeling underneath as surely as
    /// the card's name would.
    /// </summary>
    [Fact]
    [RuleRef("708.2")]
    public void A_face_down_changeling_says_nothing_about_its_creature_types()
    {
        var state = Start(mine: ChangelingOracle);
        var (board, id) = MoveFromHand(state, Me, ZoneKey.Shared(Zone.Battlefield));
        var hidden = board with { Objects = board.Objects.SetItem(id, board.Objects[id] with { FaceDown = true }) };

        var shown = Assert.Single(Project(hidden, Me).Battlefield, o => o.Id == id);

        Assert.True(shown.FaceDown);
        Assert.False(shown.EveryCreatureType);
        Assert.Empty(shown.Subtypes);
        Assert.Equal(string.Empty, shown.Name);
    }

    /// <summary>
    /// CR 303.4f and 400.2: an Aura attached to a player is on the table, so the attachment is
    /// public. Both viewers are shown it — the player it enchants and the one who does not
    /// control it — and it is the player half that is asserted here, because a projector that
    /// carried only <c>AttachedTo</c> would show the same Aura attached to nothing at all.
    /// </summary>
    [Fact]
    [RuleRef("303.4f")]
    public void An_aura_attached_to_a_player_shows_that_attachment_to_both_players()
    {
        var (board, id) = MoveFromHand(Start(), Me, ZoneKey.Shared(Zone.Battlefield));
        var attached = new ObjectChanged(id, AttachedToPlayer: Them).Apply(board);

        foreach (var viewer in new[] { Me, Them })
        {
            var shown = Assert.Single(Project(attached, viewer).Battlefield, o => o.Id == id);
            Assert.Equal(Them, shown.AttachedToPlayer);
            Assert.Null(shown.AttachedTo);
        }

        // And it is not shown when there is none: the field says something.
        Assert.Null(Assert.Single(Project(board, Them).Battlefield, o => o.Id == id).AttachedToPlayer);
    }

    /// <summary>
    /// CR 702.16j with 400.2: protection from everything is a continuous effect on a player, and
    /// what is on the battlefield is public, so both viewers are shown it beside the life total —
    /// it is what tells a client why damage to that seat never lands. The unprotected seat carries
    /// the flag false in the same view, so the field says something rather than being decoration.
    /// <para>
    /// The board is built by hand and the projection runs through the real
    /// <see cref="LayerSystem"/>, so the flag is computed from a static ability exactly as a game
    /// would compute it. The definition is invented; no card name reaches the engine.
    /// </para>
    /// </summary>
    [Fact]
    [RuleRef("702.16j")]
    public void Protection_from_everything_is_shown_beside_the_protected_players_life()
    {
        var board = new Board();
        board.Put(Board.Definition(
            "quiet aegis of the first seat",
            CardType.Enchantment,
            abilities: [new StaticAbility(new PlayerProtectionFromEverything(), Players: new You())]));

        foreach (var viewer in new[] { Me, Them })
        {
            var view = Project(board.State, viewer);

            Assert.True(view.Players[0].ProtectionFromEverything);
            Assert.False(view.Players[1].ProtectionFromEverything);
        }
    }

    /// <summary>
    /// CR 707.10 with 400.2: a copy of a spell has the copied spell's name and characteristics, so
    /// a view that carried only those would show the same spell twice and a client could not say
    /// which object CR 704.5e is about to remove. The stack is public, so both viewers are shown
    /// it, and the object beside it — a real card on the same stack — carries the flag false in
    /// the same view.
    /// </summary>
    [Fact]
    [RuleRef("707.10")]
    public void A_copy_on_the_stack_is_shown_as_a_copy_and_the_card_beside_it_is_not()
    {
        var board = new Board();
        var card = board.Put(Board.Definition("quiet spell of the first seat", CardType.Instant), Zone.Stack);
        var copy = board.Put(
            Board.Definition("quiet spell of the first seat", CardType.Instant),
            Zone.Stack,
            stack: new StackEntry(null, null, [], [], [], null, IsCopy: true));

        foreach (var viewer in new[] { Me, Them })
        {
            var stack = Project(board.State, viewer).Stack;

            Assert.True(Assert.Single(stack, o => o.Id == copy).IsCopy);
            Assert.False(Assert.Single(stack, o => o.Id == card).IsCopy);
        }
    }

    private static GameView Project(GameState state, PlayerId viewer) =>
        new ViewProjector(new LayerSystem(), oracle => oracle == MineOracle).Project(state, viewer);

    private static GameState Start(int openingHand = 7, string mine = MineOracle)
    {
        var game = new Game(Subsystems.Wave1(), new HiddenInformationDefinitions());
        var setup = new GameSetup(
            [new PlayerStart("One", Deck(mine), []), new PlayerStart("Two", Deck(TheirsOracle), [])],
            Seed: 11,
            OpeningHandSize: openingHand);
        return game.Create(setup).State;
    }

    private static ImmutableList<string> Deck(string oracleId) => [.. Enumerable.Repeat(oracleId, 10)];

    /// <summary>Moves one card out of a hand so a public zone has something in it, CR 400.7 giving it a new id.</summary>
    private static (GameState State, ObjectId Id) MoveFromHand(GameState state, PlayerId owner, ZoneKey to)
    {
        var card = state.In(ZoneKey.Of(Zone.Hand, owner))[0];
        var moved = new ZoneSystem().Move(state, card, to, controller: owner);
        return (moved.State, moved.Events.OfType<ObjectMoved>().Last().To.Id);
    }

    private sealed class HiddenInformationDefinitions : IDefinitionStore
    {
        private static readonly CardDefinition Mine = Card(MineOracle, MineName, 2, 2, []);

        private static readonly CardDefinition Theirs = Card(TheirsOracle, TheirsName, 2, 3, [new KeywordAbility(KeywordKind.Flying)]);

        /// <summary>CR 702.72a, written as the vocabulary writes it: <see cref="AddTypes"/> on itself.</summary>
        private static readonly CardDefinition Shifter = Card(
            ChangelingOracle,
            ChangelingName,
            2,
            2,
            [new StaticAbility(new AddTypes(AllCreatureTypes: true), To: new Self(), CharacteristicDefining: true)]);

        public CardDefinition? ByName(string name) =>
            name == MineName ? Mine : name == TheirsName ? Theirs : name == ChangelingName ? Shifter : null;

        public CardDefinition? ByOracleId(string oracleId) =>
            oracleId == MineOracle ? Mine
            : oracleId == TheirsOracle ? Theirs
            : oracleId == ChangelingOracle ? Shifter
            : null;

        private static CardDefinition Card(string oracleId, string name, int power, int toughness, Ability[] abilities) =>
            new(
                oracleId,
                name,
                Layout.Normal,
                [
                    new Face(
                        name,
                        FaceRole.Main,
                        new TypeLine(CardType.Creature, Supertype.None, ["Beast"]),
                        Power: new PrintedStat(power.ToString(CultureInfo.InvariantCulture)),
                        Toughness: new PrintedStat(toughness.ToString(CultureInfo.InvariantCulture)),
                        Abilities: abilities),
                ]);
    }
}
