using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;
using LegalActionsSubsystem = MtgEngine.Rules.Legal.LegalActions;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// What a player may do now, CR 117. Every game is a real one through <c>LegacySubsystems.Create()</c>,
/// so an offer here is one the casting system would accept as a command; the tests that matter
/// most are the ones where the answer is nothing at all.
/// </summary>
public sealed class LegalActionsTests
{
    private const string LandOracle = "oracle-land";
    private const string BearOracle = "oracle-bear";
    private const string HasteOracle = "oracle-haste";
    private const string EngineOracle = "oracle-engine";

    private static PlayerId Me => new(0);

    private static PlayerId Them => new(1);

    [Fact]
    public void Command_zone_offers_use_actual_commander_payment_and_priority_checks()
    {
        var (game, upkeep) = Start(BearOracle);
        var main = AtStep(game, upkeep, Step.PrecombatMain);
        var handCard = main.In(ZoneKey.Of(Zone.Hand, Me))[0];
        var moved = new ZoneSystem().Move(main, handCard, ZoneKey.Of(Zone.Command, Me));
        var commander = moved.State.Objects.Values.Single(card => card.Zone.Zone == Zone.Command) with { IsCommander = true };
        var state = moved.State with { Objects = moved.State.Objects.SetItem(commander.Id, commander) };
        Assert.Contains(commander.Id, Actions(state, Me).Castable);
        Assert.DoesNotContain(commander.Id, Actions(state with { Priority = new PriorityState(Them, []) }, Me).Castable);
        var key = (CommanderIdentity.Of(commander), Me);
        var taxed = state with { CommanderCasts = state.CommanderCasts.SetItem(key, 1) };
        Assert.DoesNotContain(commander.Id, Actions(taxed, Me).Castable);
        var paid = new ManaChanged(Me, new ManaPool(0, 0, 0, 0, 0, 2), "test").Apply(taxed);
        Assert.Contains(commander.Id, Actions(paid, Me).Castable);
        Assert.DoesNotContain(commander.Id, Actions(state with
        {
            Objects = state.Objects.SetItem(commander.Id, commander with { IsCommander = false }),
        }, Me).Castable);
    }

    [Fact]
    [RuleRef("305.1")]
    public void A_land_is_playable_on_your_own_main_phase_and_nowhere_else()
    {
        var (game, upkeep) = Start(LandOracle);
        Assert.Empty(Actions(upkeep, Me).PlayableLands);

        var main = AtStep(game, upkeep, Step.PrecombatMain);
        var mine = Actions(main, Me);

        Assert.Equal(main.In(ZoneKey.Of(Zone.Hand, Me)), mine.PlayableLands);
        Assert.Empty(mine.Castable);
        Assert.True(mine.MayPass);
        Assert.Empty(Actions(main, Them).PlayableLands);
    }

    [Fact]
    [RuleRef("302.1")]
    public void A_creature_is_castable_only_when_the_casting_system_allows_it()
    {
        var (game, upkeep) = Start(BearOracle);
        Assert.Empty(Actions(upkeep, Me).Castable);

        var main = AtStep(game, upkeep, Step.PrecombatMain);
        var mine = Actions(main, Me);

        Assert.Equal(main.In(ZoneKey.Of(Zone.Hand, Me)), mine.Castable);
        Assert.Empty(mine.PlayableLands);
        Assert.Empty(Actions(main, Them).Castable);
    }

    [Fact]
    [RuleRef("117.1a")]
    public void Only_the_player_with_priority_may_act()
    {
        var (game, state) = Start(BearOracle);
        var main = AtStep(game, state, Step.PrecombatMain);
        var passed = main with { Priority = new PriorityState(Them, []) };

        var mine = Actions(passed, Me);

        Assert.False(mine.MayPass);
        Assert.Empty(mine.Castable);
        Assert.Empty(mine.PlayableLands);
        Assert.Empty(mine.Activatable);
        Assert.True(Actions(passed, Them).MayPass);
    }

    [Fact]
    [RuleRef("602.1a")]
    public void An_activated_ability_is_offered_to_the_player_who_controls_its_source()
    {
        var (game, state) = Start(EngineOracle);
        var main = AtStep(game, state, Step.PrecombatMain);
        var (board, source) = Play(main, Me);

        var offered = Assert.Single(Actions(board, Me).Activatable);

        Assert.Equal(source, offered.Source);
        Assert.Equal(0, offered.AbilityIndex);
        Assert.Equal("draw a card", offered.Text);
        Assert.Empty(Actions(board with { Priority = new PriorityState(Them, []) }, Them).Activatable);
    }

    [Fact]
    [RuleRef("508.1a")]
    public void An_attacker_must_be_untapped_and_controlled_since_the_turn_began()
    {
        var (game, state) = Start(BearOracle);
        var (board, bear) = Play(AtStep(game, state, Step.PrecombatMain), Me);
        var declaring = board with { Turn = board.Turn with { Step = Step.DeclareAttackers } };

        // CR 302.6: it entered this turn, so it cannot attack yet.
        Assert.Empty(Actions(declaring, Me).EligibleAttackers);

        var settled = Settled(declaring, bear, sick: false);
        Assert.Equal([bear], Actions(settled, Me).EligibleAttackers);

        var tapped = settled with { Objects = settled.Objects.SetItem(bear, settled.Objects[bear] with { Tapped = true }) };
        Assert.Empty(Actions(tapped, Me).EligibleAttackers);

        // Not this player's step, and not this player's creature.
        Assert.Empty(Actions(settled, Them).EligibleAttackers);
        Assert.Empty(Actions(settled with { Turn = settled.Turn with { Step = Step.PrecombatMain } }, Me).EligibleAttackers);
    }

    [Fact]
    [RuleRef("702.10b")]
    public void Haste_lets_a_creature_that_entered_this_turn_attack()
    {
        var (game, state) = Start(HasteOracle);
        var (board, hasty) = Play(AtStep(game, state, Step.PrecombatMain), Me);
        var declaring = board with { Turn = board.Turn with { Step = Step.DeclareAttackers } };

        Assert.True(declaring.Objects[hasty].SummoningSick);
        Assert.Equal([hasty], Actions(declaring, Me).EligibleAttackers);
    }

    [Fact]
    [RuleRef("509.1a")]
    public void A_defending_player_may_block_with_their_untapped_creatures()
    {
        var (game, state) = Start(BearOracle);
        var (mine, attacker) = Play(AtStep(game, state, Step.PrecombatMain), Me);
        var (board, blocker) = Play(mine, Them);
        var settled = Settled(board, attacker, sick: false);

        var attacking = game.Engine.Combat.DeclareAttackers(
            settled with { Turn = settled.Turn with { Step = Step.DeclareAttackers } },
            new DeclareAttackers(Me, [new AttackDeclaration(attacker, new CombatTarget(Them, null))])).State;
        var blocking = attacking with { Turn = attacking.Turn with { Step = Step.DeclareBlockers } };

        Assert.Equal([blocker], Actions(blocking, Them).EligibleBlockers);
        Assert.Empty(Actions(blocking, Me).EligibleBlockers);
    }

    [Fact]
    [RuleRef("101.4")]
    public void A_pending_choice_is_the_only_thing_anyone_may_do()
    {
        var (game, state) = Start(LandOracle);
        var main = AtStep(game, state, Step.PrecombatMain);
        var waiting = main with
        {
            Pending = new PendingChoice(
                new ChoiceId(1),
                Them,
                "targets",
                "Choose a target.",
                [],
                1,
                1,
                new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty)),
        };

        var theirs = Actions(waiting, Them);
        Assert.True(theirs.MustAnswerChoice);
        Assert.False(theirs.MayPass);

        var mine = Actions(waiting, Me);
        Assert.False(mine.MustAnswerChoice);
        Assert.False(mine.MayPass);
        Assert.Empty(mine.PlayableLands);
    }

    [Fact]
    [RuleRef("104.1")]
    public void A_finished_game_allows_nothing()
    {
        var (game, state) = Start(LandOracle);
        var main = AtStep(game, state, Step.PrecombatMain);
        var over = main with { Outcome = new GameOutcome([Them], Draw: false, "concession") };

        var mine = Actions(over, Me);

        Assert.Empty(mine.PlayableLands);
        Assert.Empty(mine.Castable);
        Assert.Empty(mine.Activatable);
        Assert.Empty(mine.EligibleAttackers);
        Assert.False(mine.MayPass);
        Assert.False(mine.MustAnswerChoice);
    }

    /// <summary>
    /// CR 508.1a and 509.1a are read once, by the combat system. Every attacker this view offers
    /// is one <see cref="ICombatSystem.CouldAttack"/> accepts and every blocker one
    /// <see cref="ICombatSystem.CouldBlock"/> accepts, over a whole real board, so a restriction
    /// added to the combat system can never be missing from the view.
    /// </summary>
    [Fact]
    [RuleRef("508.1a")]
    public void An_offered_attacker_is_exactly_one_the_combat_system_would_accept()
    {
        var (game, state) = Start(BearOracle);
        var main = AtStep(game, state, Step.PrecombatMain);
        var (one, settled) = Play(main, Me);
        var (two, sick) = Play(one, Me);
        var (three, theirs) = Play(two, Them);
        var board = Settled(three, settled, sick: false);
        var declaring = board with { Turn = board.Turn with { Step = Step.DeclareAttackers } };

        var combat = game.Engine.Combat;
        var offered = Actions(declaring, Me).EligibleAttackers;

        Assert.Equal([settled], offered);
        Assert.All(offered, id => Assert.True(combat.CouldAttack(declaring, id)));

        // The two the view withheld: one is summoning sick (CR 302.6), the other is not this
        // player's creature, and the combat system agrees about the first.
        Assert.False(combat.CouldAttack(declaring, sick));
        Assert.DoesNotContain(theirs, offered);
    }

    /// <summary>
    /// The view asks; it does not decide. A combat system that refuses one creature makes that
    /// creature disappear from the offer with no change here, which is what "one reading" means.
    /// </summary>
    [Fact]
    [RuleRef("508.1a")]
    public void A_creature_the_combat_system_refuses_is_not_offered()
    {
        var (game, state) = Start(BearOracle);
        var main = AtStep(game, state, Step.PrecombatMain);
        var (one, first) = Play(main, Me);
        var (two, second) = Play(one, Me);
        var board = Settled(Settled(two, first, sick: false), second, sick: false);
        var declaring = board with { Turn = board.Turn with { Step = Step.DeclareAttackers } };

        var refusing = new LegalActionsFakeCombat(game.Engine.Combat, second);

        Assert.Equal([first, second], Actions(declaring, Me).EligibleAttackers);
        Assert.Equal([first], Actions(declaring, Me, refusing).EligibleAttackers);
    }

    /// <summary>
    /// CR 509.1a is the combat system's too: a blocker it refuses is not offered, and the step,
    /// the declaration and CR 506.2's "who is defending" stay the view's own gates.
    /// </summary>
    [Fact]
    [RuleRef("509.1a")]
    public void A_blocker_the_combat_system_refuses_is_not_offered()
    {
        var (game, state) = Start(BearOracle);
        var main = AtStep(game, state, Step.PrecombatMain);
        var (mine, attacker) = Play(main, Me);
        var (withOne, first) = Play(mine, Them);
        var (board, second) = Play(withOne, Them);
        var settled = Settled(board, attacker, sick: false);

        var attacking = game.Engine.Combat.DeclareAttackers(
            settled with { Turn = settled.Turn with { Step = Step.DeclareAttackers } },
            new DeclareAttackers(Me, [new AttackDeclaration(attacker, new CombatTarget(Them, null))])).State;
        var blocking = attacking with { Turn = attacking.Turn with { Step = Step.DeclareBlockers } };

        Assert.Equal([first, second], Actions(blocking, Them).EligibleBlockers);

        var refusing = new LegalActionsFakeCombat(game.Engine.Combat, first);
        Assert.Equal([second], Actions(blocking, Them, refusing).EligibleBlockers);
    }

    /// <summary>
    /// A composition with no combat system cannot read CR 508.1a, so it offers nothing rather
    /// than a second, drifting reading of the rule. Everything else it can answer, it still does.
    /// </summary>
    [Fact]
    [RuleRef("508.1")]
    public void With_no_combat_system_no_attacker_and_no_blocker_is_offered()
    {
        var (game, state) = Start(BearOracle);
        var main = AtStep(game, state, Step.PrecombatMain);
        var (board, bear) = Play(main, Me);
        var settled = Settled(board, bear, sick: false);
        var declaring = settled with { Turn = settled.Turn with { Step = Step.DeclareAttackers } };

        var layers = new LayerSystem();
        var query = new Query.Query(layers);
        var casting = new Casting.CastingSystem(new ZoneSystem(), query, layers, new Stubs.Interpreter());
        var blind = new LegalActionsSubsystem(casting, layers).For(declaring, Me);

        Assert.Empty(blind.EligibleAttackers);
        Assert.Empty(blind.EligibleBlockers);
        Assert.Equal([bear], Actions(declaring, Me).EligibleAttackers);
    }

    private static Views.LegalActions Actions(GameState state, PlayerId player, ICombatSystem? combat = null)
    {
        var layers = new LayerSystem();
        var query = new Query.Query(layers);
        var casting = new Casting.CastingSystem(new ZoneSystem(), query, layers, new Stubs.Interpreter());
        return new LegalActionsSubsystem(casting, layers, combat ?? new Combat.CombatSystem(layers, query)).For(state, player);
    }

    /// <summary>
    /// A real combat system with one creature struck out. It exists to prove the view asks
    /// rather than decides, so everything except the two questions is delegated untouched.
    /// </summary>
    private sealed class LegalActionsFakeCombat(ICombatSystem inner, ObjectId refused) : ICombatSystem
    {
        public string Name => "legal-actions fake combat";

        public Outcome DeclareAttackers(GameState state, DeclareAttackers command) => inner.DeclareAttackers(state, command);

        public Outcome DeclareBlockers(GameState state, DeclareBlockers command) => inner.DeclareBlockers(state, command);

        public Outcome DealCombatDamage(GameState state, bool firstStrike) => inner.DealCombatDamage(state, firstStrike);

        public Outcome EndCombat(GameState state) => inner.EndCombat(state);

        public bool CouldAttack(GameState state, ObjectId creature) => creature != refused && inner.CouldAttack(state, creature);

        public bool CouldBlock(GameState state, ObjectId creature) => creature != refused && inner.CouldBlock(state, creature);

        // Both resumption overloads, forwarded: a wrapper that misses a member inherits the
        // seam's throwing default rather than failing to compile (SeamDecoratorTests).
        public Outcome ResumeCombatDamage(GameState state, bool firstStrike, int from) =>
            inner.ResumeCombatDamage(state, firstStrike, from);

        public Outcome ResumeCombatDamage(GameState state, bool firstStrike, int from, ImmutableList<GameEvent> dealt) =>
            inner.ResumeCombatDamage(state, firstStrike, from, dealt);
    }

    private static (Game Game, GameState State) Start(string oracleId)
    {
        var game = new Game(LegacySubsystems.Create(), new LegalActionsDefinitions());
        var setup = new GameSetup(
            [new PlayerStart("One", [.. Deck(oracleId)], []), new PlayerStart("Two", [.. Deck(oracleId)], [])],
            Seed: 3);
        return (game, game.Create(setup).State);
    }

    private static ImmutableList<string> Deck(string oracleId) => [.. Enumerable.Repeat(oracleId, 10)];

    private static GameState AtStep(Game game, GameState state, Step step)
    {
        for (var guard = 0; guard < 30 && state.Turn.Step != step; guard++)
            state = game.Engine.Turn.Advance(state).State;

        Assert.Equal(step, state.Turn.Step);
        return state;
    }

    /// <summary>Puts one card from a hand onto the battlefield, CR 400.7 giving it a new id.</summary>
    private static (GameState State, ObjectId Id) Play(GameState state, PlayerId player)
    {
        var card = state.In(ZoneKey.Of(Zone.Hand, player))[0];
        var moved = new ZoneSystem().Move(state, card, ZoneKey.Shared(Zone.Battlefield), controller: player);
        return (moved.State, moved.Events.OfType<ObjectMoved>().Last().To.Id);
    }

    /// <summary>CR 302.6: a permanent its controller has controlled since the turn began is no longer summoning sick.</summary>
    private static GameState Settled(GameState state, ObjectId id, bool sick) =>
        state with { Objects = state.Objects.SetItem(id, state.Objects[id] with { SummoningSick = sick }) };

    private sealed class LegalActionsDefinitions : IDefinitionStore
    {
        public CardDefinition? ByOracleId(string oracleId) => oracleId switch
        {
            LandOracle => Land(),
            BearOracle => Creature(BearOracle, "quiet fixture bear", []),
            HasteOracle => Creature(HasteOracle, "quiet fixture courier", [new KeywordAbility(KeywordKind.Haste)]),
            EngineOracle => Creature(EngineOracle, "quiet fixture engine", [Ability()]),
            _ => null,
        };

        public CardDefinition? ByName(string name) => null;

        private static Ability Ability() =>
            new ActivatedAbility([], new Draw(new Fixed(1))) { Text = "draw a card" };

        private static CardDefinition Land() =>
            new(LandOracle, "quiet fixture waste", Layout.Normal,
                [new Face("quiet fixture waste", FaceRole.Main, new TypeLine(CardType.Land, Supertype.Basic, ["Waste"]))]);

        private static CardDefinition Creature(string oracleId, string name, Ability[] abilities) =>
            new(oracleId, name, Layout.Normal,
                [
                    new Face(
                        name,
                        FaceRole.Main,
                        new TypeLine(CardType.Creature, Supertype.None, ["Beast"]),
                        Power: new PrintedStat(2.ToString(CultureInfo.InvariantCulture)),
                        Toughness: new PrintedStat(2.ToString(CultureInfo.InvariantCulture)),
                        Abilities: abilities),
                ]);
    }
}
