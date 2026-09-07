using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Query;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The questions the query layer could not answer until the state started recording the answer:
/// what happened this turn (CR 603.10), the designations the rules put on objects and players,
/// what was paid as a spell was cast (CR 601.2h, 702.32b), and who is the monarch. Every history
/// here is folded by the real events, never written into the state by hand, so a test fails if
/// the fold and the read disagree.
/// </summary>
public sealed class QueryBacklogTests
{
    private static readonly PlayerId Alice = new(0);
    private static readonly PlayerId Bob = new(1);

    [Fact]
    [RuleRef("603.10")]
    public void Entering_the_battlefield_this_turn_is_the_turns_history_and_not_the_object()
    {
        var board = new Board();
        var waiting = board.Put(Board.Creature(2, 2), Zone.Hand);
        var already = board.Put(Board.Creature(2, 2));
        var entered = Move(board, waiting, Zone.Battlefield);

        Assert.True(Holds(board, new EnteredThisTurn(), entered));
        Assert.False(Holds(board, new EnteredThisTurn(), already));
    }

    [Fact]
    [RuleRef("603.10")]
    public void A_spell_cast_this_turn_is_recorded_against_the_object_that_was_cast()
    {
        var board = new Board();
        var cast = board.Put(Board.Creature(1, 1), Zone.Stack);
        var quiet = board.Put(Board.Creature(1, 1), Zone.Stack);
        board.Fold(new Events.SpellCast(cast, Alice, Zone.Hand));

        Assert.True(Holds(board, new CastThisTurn(), cast));
        Assert.False(Holds(board, new CastThisTurn(), quiet));
        Assert.Equal(1, Value(board, new SpellsCastThisTurn(new You())));
        Assert.Equal(0, Value(board, new SpellsCastThisTurn(new EachOpponent())));
    }

    [Fact]
    [RuleRef("120.3")]
    public void Damage_this_turn_is_read_from_both_sides_of_the_turns_history()
    {
        var board = new Board();
        var source = board.Put(Board.Creature(3, 3));
        var victim = board.Put(Board.Creature(3, 3));
        var bystander = board.Put(Board.Creature(3, 3));
        board.Fold(new DamageDealt(source, victim, null, 2, Combat: false, FromCommander: false));
        board.Fold(new DamageDealt(source, null, Bob, 1, Combat: false, FromCommander: false));

        Assert.True(Holds(board, new DealtDamageThisTurn(), source));
        Assert.False(Holds(board, new DealtDamageThisTurn(), bystander));
        Assert.True(Holds(board, new WasDealtDamageThisTurn(), victim));
        Assert.False(Holds(board, new WasDealtDamageThisTurn(), bystander));

        Assert.Equal(3, Value(board, new DamageDealtThisTurn()));
        Assert.Equal(3, Value(board, new DamageDealtThisTurn(By: new Each(new ObjectFilter(Types: CardType.Creature)))));
        Assert.Equal(1, Value(board, new DamageDealtThisTurn(To: new EachOpponent())));
    }

    [Fact]
    [RuleRef("603.10")]
    public void Morbid_and_revolt_read_what_left_the_battlefield_this_turn()
    {
        var quiet = new Board();
        _ = quiet.Put(Board.Creature(2, 2));
        Assert.False(Holds(quiet, new Morbid()));
        Assert.False(Holds(quiet, new Revolt(new You())));

        var board = new Board();
        var doomed = board.Put(Board.Creature(2, 2));
        _ = Move(board, doomed, Zone.Graveyard);

        Assert.True(Holds(board, new Morbid()));
        Assert.True(Holds(board, new Revolt(new You())));
    }

    /// <summary>
    /// Morbid is "a creature died" and revolt is "a permanent you controlled left the battlefield"
    /// (CR 603.10). Both need the departed object, which the history does not keep yet, so each
    /// assertion below is written against <see cref="TurnFacts.KeepsDepartedObjects"/>: it passes
    /// today on the unqualified count and passes on the rule itself once the seam lands. The
    /// waiting assertions are the two <c>Assert.Equal(qualified, ...)</c> lines — today they assert
    /// the wrong-but-honest yes, and after the seam they assert the no the rule wants.
    /// </summary>
    [Fact]
    [RuleRef("603.10")]
    public void Morbid_is_a_creature_and_revolt_is_a_permanent_you_controlled_once_the_history_keeps_them()
    {
        var qualified = TurnFacts.KeepsDepartedObjects;

        var land = new Board();
        var sacrificed = land.Put(Board.Definition("qb soil", CardType.Land));
        _ = Move(land, sacrificed, Zone.Graveyard);

        // A land dying is not morbid. Until the object is kept, "something died" is all the state says.
        Assert.Equal(!qualified, Holds(land, new Morbid()));

        var creature = new Board();
        var doomed = creature.Put(Board.Creature(2, 2));
        _ = Move(creature, doomed, Zone.Graveyard);
        Assert.True(Holds(creature, new Morbid()));

        // Revolt asks whose permanent left; an opponent's does not enable yours.
        var theirs = new Board();
        var opponents = theirs.Put(Board.Creature(2, 2), owner: 1);
        _ = Move(theirs, opponents, Zone.Graveyard);
        Assert.Equal(!qualified, Holds(theirs, new Revolt(new You())));
        Assert.True(Holds(theirs, new Revolt(new EachOpponent())));
    }

    /// <summary>
    /// A question about one source and one receiver, CR 120.3. The history totals damage per source
    /// and per receiver and never per pair, so both spellings refuse today; with the map they answer
    /// the sum of that pair. Every assertion here is against <see cref="TurnFacts.KeepsDamagePairs"/>.
    /// </summary>
    [Fact]
    [RuleRef("120.3")]
    public void Damage_from_one_source_to_one_player_is_refused_until_the_history_keeps_the_pair()
    {
        var board = new Board();
        var source = board.Put(Board.Creature(3, 3));
        var other = board.Put(Board.Creature(3, 3));
        board.Fold(new DamageDealt(source, null, Bob, 2, Combat: false, FromCommander: false));
        board.Fold(new DamageDealt(other, null, Alice, 5, Combat: false, FromCommander: false));

        var predicate = new DealtDamageThisTurn(new EachOpponent());
        var amount = new DamageDealtThisTurn(new Each(ObjectFilter.Creature), new EachOpponent());

        if (TurnFacts.KeepsDamagePairs)
        {
            Assert.True(Holds(board, predicate, source));
            Assert.False(Holds(board, predicate, other));
            Assert.Equal(2, Value(board, amount));
        }
        else
        {
            Assert.Throws<RefusedException>(() => Holds(board, predicate, source));
            Assert.Throws<RefusedException>(() => Value(board, amount));
        }

        // The unpaired questions are answered from the totals either way.
        Assert.True(Holds(board, new DealtDamageThisTurn(), source));
        Assert.Equal(7, Value(board, new DamageDealtThisTurn()));
    }

    /// <summary>
    /// "The first time this ability resolves each turn", CR 603.4. Nothing tallies resolutions, so
    /// the condition refuses; with the tally in place a source that has not resolved answers yes.
    /// </summary>
    [Fact]
    [RuleRef("603.4")]
    public void The_first_time_this_turn_is_refused_until_the_history_counts_resolutions()
    {
        var board = new Board();
        var source = board.Put(Board.Creature(1, 1));

        if (TurnFacts.CountsResolutions)
            Assert.True(Holds(board, new FirstTimeThisTurn(), source));
        else
            Assert.Throws<RefusedException>(() => Holds(board, new FirstTimeThisTurn(), source));
    }

    [Fact]
    [RuleRef("508.1")]
    public void Raid_and_attacked_this_turn_count_the_attackers_the_turn_declared()
    {
        var board = new Board();
        var first = board.Put(Board.Creature(2, 2));
        var second = board.Put(Board.Creature(2, 2));

        Assert.False(Holds(board, new Raid(new You())));
        Assert.False(Holds(board, new AttackedThisTurn(new You(), new Fixed(1))));

        board.Fold(new AttackersDeclared(
            Alice,
            new Dictionary<ObjectId, CombatTarget> { [first] = new(Bob, null) }.ToImmutableDictionary()));

        Assert.True(Holds(board, new Raid(new You())));
        Assert.True(Holds(board, new AttackedThisTurn(new You(), new Fixed(1))));
        Assert.False(Holds(board, new AttackedThisTurn(new You(), new Fixed(2))));
        // The active player is the only one who declares attackers, CR 508.1, so an opponent
        // asking about their own attackers this turn gets none.
        Assert.False(Holds(board, new AttackedThisTurn(new EachOpponent(), new Fixed(1))));

        // A second combat phase folds into the same list, and a creature declared twice is still
        // one attacker.
        board.Fold(new AttackersDeclared(
            Alice,
            new Dictionary<ObjectId, CombatTarget> { [first] = new(Bob, null), [second] = new(Bob, null) }.ToImmutableDictionary()));
        Assert.True(Holds(board, new AttackedThisTurn(new You(), new Fixed(2))));
        Assert.False(Holds(board, new AttackedThisTurn(new You(), new Fixed(3))));
    }

    [Fact]
    [RuleRef("601.2a")]
    public void Paradox_and_cast_from_zone_read_where_the_spell_was_cast_from()
    {
        var board = new Board();
        var spell = board.Put(Board.Creature(1, 1), Zone.Stack);
        board.Fold(new Events.SpellCast(spell, Alice, Zone.Graveyard));

        Assert.True(Holds(board, new Paradox(new You())));
        Assert.False(Holds(board, new Paradox(new EachOpponent())));
        Assert.True(Holds(board, new CastFromZone(Zone.Graveyard), source: spell));
        Assert.False(Holds(board, new CastFromZone(Zone.Hand), source: spell));
    }

    [Fact]
    [RuleRef("506.2")]
    public void In_combat_is_attacking_or_blocking_and_the_one_disjunction_with_a_field()
    {
        var board = new Board();
        var attacker = board.Put(Board.Creature(2, 2));
        var blocker = board.Put(Board.Creature(2, 2), owner: 1);
        var bystander = board.Put(Board.Creature(2, 2));
        board.Fold(new AttackersDeclared(
            Alice,
            new Dictionary<ObjectId, CombatTarget> { [attacker] = new(Bob, null) }.ToImmutableDictionary()));
        board.Fold(new BlockersDeclared(
            Bob,
            new Dictionary<ObjectId, ImmutableList<ObjectId>> { [attacker] = [blocker] }.ToImmutableDictionary()));

        var query = Query(board);
        var filter = new ObjectFilter(InCombat: true);
        Assert.True(query.Matches(board.State, filter, attacker, Context()));
        Assert.True(query.Matches(board.State, filter, blocker, Context()));
        Assert.False(query.Matches(board.State, filter, bystander, Context()));
        Assert.True(query.Matches(board.State, new ObjectFilter(InCombat: false), bystander, Context()));
    }

    [Fact]
    [RuleRef("701.60a")]
    public void Every_designation_the_rules_put_on_an_object_is_read_from_the_object()
    {
        var board = new Board();
        var marked = board.Put(Board.Creature(2, 2));
        var plain = board.Put(Board.Creature(2, 2));
        var state = Designate(
            board.State,
            marked,
            Designations.Suspected,
            Designations.RingBearer,
            Designations.Harnessed,
            Designations.Prepared,
            Designations.GoadedBy(Bob));

        Predicate[] predicates = [new IsSuspected(), new IsRingBearer(), new IsHarnessed(), new IsPrepared(), new IsGoaded()];
        foreach (var predicate in predicates)
        {
            Assert.True(Holds(state, predicate, marked), predicate.GetType().Name);
            Assert.False(Holds(state, predicate, plain), predicate.GetType().Name);
        }

        // CR 701.15a: goading names the goader, because the creature must attack someone else.
        Assert.True(Holds(state, new IsGoaded(new EachOpponent()), marked));
        Assert.False(Holds(state, new IsGoaded(new You()), marked));
    }

    [Fact]
    [RuleRef("724.2")]
    public void The_monarch_the_initiative_and_the_citys_blessing_come_from_the_state()
    {
        var board = new Board();
        _ = board.Put(Board.Creature(1, 1));

        Assert.False(Holds(board, new YouAreMonarch()));
        Assert.False(Holds(board, new YouHaveInitiative()));
        Assert.False(Holds(board, new CityBlessing(new You())));
        Assert.Empty(Query(board).Players(board.State, new Cards.Monarch(), Context()));

        var crowned = board.State with
        {
            Monarch = Alice,
            Initiative = Bob,
            Players = board.State.Players.SetItem(
                Alice.Value,
                board.State.Player(Alice) with { Designations = [Designations.CityBlessing] }),
        };

        Assert.True(Holds(crowned, new YouAreMonarch()));
        Assert.False(Holds(crowned, new YouHaveInitiative()));
        Assert.True(Holds(crowned, new CityBlessing(new You())));
        Assert.False(Holds(crowned, new CityBlessing(new EachOpponent())));
        Assert.Equal([Alice], Query(board).Players(crowned, new Cards.Monarch(), Context()));
    }

    [Fact]
    [RuleRef("702.32b")]
    public void What_was_paid_to_cast_a_spell_is_counted_from_its_stack_entry()
    {
        var board = new Board();
        var kicked = board.Put(
            Board.Creature(1, 1),
            Zone.Stack,
            stack: Entry([nameof(KeywordKind.Kicker), nameof(KeywordKind.Kicker), nameof(KeywordKind.Flashback)]));
        var plain = board.Put(Board.Creature(1, 1), Zone.Stack, stack: Entry([]));

        Assert.True(Holds(board, new WasKicked(), kicked));
        Assert.False(Holds(board, new WasKicked(), plain));

        Assert.Equal(2, Value(board, new Cards.TimesKicked(), source: kicked));
        Assert.Equal(0, Value(board, new Cards.TimesKicked(), source: plain));

        Assert.True(Holds(board, new WasKickedCondition(), source: kicked));
        Assert.True(Holds(board, new WasKickedCondition(2), source: kicked));
        Assert.False(Holds(board, new WasKickedCondition(3), source: kicked));
        Assert.False(Holds(board, new WasKickedCondition(), source: plain));

        Assert.True(Holds(board, new PaidAlternativeCost(KeywordKind.Flashback), source: kicked));
        Assert.False(Holds(board, new PaidAlternativeCost(KeywordKind.Flashback), source: plain));
    }

    [Fact]
    [RuleRef("601.2h")]
    public void Mana_spent_on_a_spell_answers_adamant_and_the_amount()
    {
        var board = new Board();
        var spell = board.Put(
            Board.Creature(1, 1),
            Zone.Stack,
            stack: Entry([], new Dictionary<Color, int> { [Color.Red] = 3, [Color.Green] = 1 }.ToImmutableDictionary()));
        var plain = board.Put(Board.Creature(1, 1), Zone.Stack, stack: Entry([]));

        Assert.True(Holds(board, new Adamant(Color.Red), source: spell));
        Assert.False(Holds(board, new Adamant(Color.Green), source: spell));
        Assert.False(Holds(board, new Adamant(Color.Red), source: plain));

        Assert.Equal(4, Value(board, new ManaSpentOnThis(), source: spell));
        Assert.Equal(3, Value(board, new ManaSpentOnThis(Color.Red), source: spell));
    }

    [Fact]
    [RuleRef("700.5")]
    public void Devotion_to_a_chosen_color_counts_the_color_that_was_chosen()
    {
        var board = new Board();
        _ = board.Put(Board.Definition("qb devotion", CardType.Creature, 1, 1) with { Faces = Costed("{B}{B}{R}") });

        var chosen = Context() with { Chosen = ImmutableDictionary<string, string>.Empty.SetItem("colour", "Black") };
        var red = Context() with { Chosen = ImmutableDictionary<string, string>.Empty.SetItem("colour", "Red") };

        var query = Query(board);
        Assert.Equal(2, query.Evaluate(board.State, new Cards.Devotion(new You(), ChosenColorKey: "colour"), chosen));
        Assert.Equal(1, query.Evaluate(board.State, new Cards.Devotion(new You(), ChosenColorKey: "colour"), red));
        // The printed colour is never used when the card chose one; nothing chosen is a refusal.
        Assert.Throws<RefusedException>(
            () => query.Evaluate(board.State, new Cards.Devotion(new You(), Color.Black, "colour"), Context()));
    }

    [Fact]
    [RuleRef("702.174")]
    public void A_promised_gift_is_answered_no_until_the_stack_entry_records_it()
    {
        var board = new Board();
        var spell = board.Put(Board.Creature(1, 1), Zone.Stack, stack: Entry([]));

        Assert.False(Holds(board, new GiftPromised(), source: spell));
    }

    [Fact]
    [RuleRef("603.4")]
    public void A_question_the_state_still_cannot_answer_refuses_instead_of_throwing()
    {
        var board = new Board();
        _ = board.Put(Board.Creature(1, 1));

        if (!TurnFacts.CountsResolutions)
        {
            var refused = Assert.Throws<RefusedException>(() => Holds(board, new FirstTimeThisTurn()));
            Assert.Contains(nameof(FirstTimeThisTurn), refused.Reason, StringComparison.Ordinal);
            Assert.Equal("603.4", refused.Rule);
        }

        // The pair of source and receiver is not recorded, so this refuses rather than guessing.
        if (!TurnFacts.KeepsDamagePairs)
        {
            Assert.Throws<RefusedException>(
                () => Value(board, new DamageDealtThisTurn(new Each(ObjectFilter.Creature), new You())));
        }
    }

    [Fact]
    [RuleRef("101.4")]
    public void A_game_holding_a_card_the_query_cannot_answer_refuses_the_command()
    {
        var game = new Game(Subsystems.Wave2(), new QueryBacklogDefinitions());
        var deck = Enumerable.Repeat(QueryBacklogDefinitions.SoilId, 12).ToImmutableList();
        var state = game.Create(new GameSetup(
            [new PlayerStart("One", deck, []), new PlayerStart("Two", deck, [])],
            Seed: 11,
            StartingLife: 20,
            Format: "test")).State;

        // Priority passes until the active player's main phase, where a land may be played (CR 505.6b).
        for (var i = 0; i < 40 && state.Turn.Step != Step.PrecombatMain; i++)
        {
            var holder = state.Priority.Holder ?? state.Turn.ActivePlayer;
            var passed = game.Apply(state, new PassPriority(holder));
            state = Assert.IsType<Applied>(passed).State;
        }

        var land = state.In(ZoneKey.Of(Zone.Hand, state.Turn.ActivePlayer))[0];
        var result = game.Apply(state, new PlayLand(state.Turn.ActivePlayer, land));

        // The trigger's intervening "if" asks a question nothing answers (CR 603.4). The table is
        // told which primitive is missing; the game does not die with the command half applied.
        // Once the history counts resolutions the question has an answer and the land is played.
        if (TurnFacts.CountsResolutions)
        {
            Assert.IsType<Applied>(result);
        }
        else
        {
            var refusal = Assert.IsType<Refused>(result);
            Assert.Contains(nameof(FirstTimeThisTurn), refusal.Reason, StringComparison.Ordinal);
        }
    }

    private static ImmutableList<Face> Costed(string manaCost) =>
        [new Face("qb devotion", FaceRole.Main, new TypeLine(CardType.Creature), ManaCost: manaCost)];

    private static StackEntry Entry(string[] optionalCosts, ImmutableDictionary<Color, int>? manaSpent = null) =>
        new(null, null, [], [], [], null, false, manaSpent, Zone.Hand, [.. optionalCosts]);

    /// <summary>A real zone change, so the turn's history is folded by the event and not written by the test.</summary>
    private static ObjectId Move(Board board, ObjectId what, Zone to)
    {
        var obj = board.State.Objects[what];
        var key = to is Zone.Battlefield or Zone.Stack or Zone.Exile ? ZoneKey.Shared(to) : ZoneKey.Of(to, obj.Owner);
        var moved = obj with { Id = new ObjectId(board.State.NextObjectId), Zone = key, Timestamp = board.State.NextTimestamp };
        board.Fold(new ObjectMoved(what, moved, -1, obj));
        return moved.Id;
    }

    private static GameState Designate(GameState state, ObjectId id, params string[] designations) =>
        state with
        {
            Objects = state.Objects.SetItem(id, state.Objects[id] with { Designations = [.. designations] }),
        };

    private static MtgEngine.Rules.Query.Query Query(Board board) => Query(board.State);

    private static MtgEngine.Rules.Query.Query Query(GameState state) => new(new FakeCharacteristics());

    private static EffectContext Context(ObjectId? source = null) => new(
        source,
        Alice,
        [],
        [],
        null,
        ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
        ImmutableDictionary<string, string>.Empty,
        ImmutableDictionary<string, int>.Empty,
        [],
        null);

    private static bool Holds(Board board, Predicate predicate, ObjectId candidate) =>
        Holds(board.State, predicate, candidate);

    private static bool Holds(GameState state, Predicate predicate, ObjectId candidate) =>
        Query(state).Matches(state, new ObjectFilter(In: state.Objects[candidate].Zone.Zone, Also: predicate), candidate, Context(candidate));

    private static bool Holds(Board board, Condition condition, ObjectId? source = null) =>
        Holds(board.State, condition, source);

    private static bool Holds(GameState state, Condition condition, ObjectId? source = null) =>
        Query(state).Holds(state, condition, Context(source));

    private static int Value(Board board, Amount amount, ObjectId? source = null) =>
        Query(board).Evaluate(board.State, amount, Context(source));

    /// <summary>
    /// One card: a land whose triggered ability asks, as an intervening "if" (CR 603.4), a
    /// question nothing in the state answers. It is the shape the first verify run died on.
    /// </summary>
    private sealed class QueryBacklogDefinitions : IDefinitionStore
    {
        internal const string SoilId = "query-backlog-soil";
        internal const string SoilName = "Query Backlog Soil";

        private static readonly CardDefinition Soil = new(
            SoilId,
            SoilName,
            Layout.Normal,
            [
                new Face(
                    SoilName,
                    FaceRole.Main,
                    new TypeLine(CardType.Land),
                    Abilities:
                    [
                        new TriggeredAbility(
                            new Cards.LandPlayed(new You()),
                            new GainLife(new Fixed(1), new You()),
                            If: new FirstTimeThisTurn()),
                    ]),
            ]);

        public CardDefinition? ByName(string name) => string.Equals(name, SoilName, StringComparison.Ordinal) ? Soil : null;

        public CardDefinition? ByOracleId(string oracleId) => string.Equals(oracleId, SoilId, StringComparison.Ordinal) ? Soil : null;
    }
}
