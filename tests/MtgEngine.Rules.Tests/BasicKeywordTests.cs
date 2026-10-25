using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

public sealed class BasicKeywordTests
{
    private static readonly PlayerId A = new(0);
    private static readonly PlayerId B = new(1);
    private static readonly CardDefinition Forest = new("forest", "Forest", Layout.Normal,
        [new("Forest", FaceRole.Main, new(CardType.Land, Supertype.Basic, ["Forest"]))]);
    private static readonly CardDefinition Bounce = new("bounce", "Bounce", Layout.Normal,
        [new("Bounce", FaceRole.Main, new(CardType.Instant), "{0}", OracleText: "Return target creature to its owner's hand.")]);

    private static BasicCard Card(string name, int power, int toughness, params BasicKeyword[] keywords) => new(
        new(name, name, Layout.Normal, [new(name, FaceRole.Main, new(CardType.Creature), "{0}",
            new(power.ToString()), new(toughness.ToString()), OracleText: string.Join("\n", keywords.Distinct().Select(k => k switch
            {
                BasicKeyword.FirstStrike => "First strike",
                BasicKeyword.DoubleStrike => "Double strike",
                _ => k.ToString(),
            })))]), BasicBehavior.Creature)
    { Keywords = keywords.ToImmutableHashSet() };

    private sealed class Table(params BasicCard[] cards)
    {
        public BasicActions Actions { get; } = new(cards.Concat([new(Forest, BasicBehavior.Land), new(Bounce, BasicBehavior.ReturnCreature)]));
        public BasicCombat Combat => new(Actions);
        public GameState State { get; private set; } = Setup();

        private static GameState Setup()
        {
            var state = GameSetup.Create([PlayerState.Create(A, "A", 20), PlayerState.Create(B, "B", 20)]);
            foreach (var p in state.Players)
                for (var i = 0; i < 5; i++)
                    state = GameSetup.AddCard(state, p.Id, Forest, new("forest"));
            return state;
        }

        public ObjectId Add(BasicCard card, PlayerId player, Zone zone = Zone.Battlefield) => Add(card.Definition, player, zone);
        public ObjectId Add(CardDefinition card, PlayerId player, Zone zone = Zone.Hand)
        {
            State = GameSetup.AddCard(State, player, card, new(card.OracleId));
            var move = ZoneTransitions.Move(State, new(new(State.LastObjectId), zone == Zone.Battlefield
                ? ZoneKey.Shared(zone) : ZoneKey.Of(zone, player)));
            State = move.State;
            return move.Current;
        }

        public TurnExecution Check(TurnExecution e) => Combat.CompleteCheckpoint(e);
        public TurnExecution Start() => Check(TurnExecution.Start(State, A).CompleteBasicTurnAction());
        public TurnExecution Round(TurnExecution e)
        {
            var count = e.State.Players.Count(p => !p.HasLost) - e.State.Priority.PassedInSuccession.Count;
            for (var i = 0; i < count; i++)
            {
                e = e.Pass(e.State.Priority.Holder!.Value);
                if (i < count - 1)
                    e = Check(e);
            }
            return e;
        }

        public TurnExecution Reach(TurnExecution e, Step step)
        {
            for (var i = 0; i < 80; i++)
            {
                if (e.State.Turn.Step == step)
                    return e;
                e = e.Boundary switch
                {
                    ExecutionBoundary.Checkpoint => Check(e),
                    ExecutionBoundary.Priority => Round(e),
                    ExecutionBoundary.TurnAction => e.CompleteBasicTurnAction(),
                    _ => throw new InvalidOperationException("Unexpected boundary."),
                };
            }
            throw new InvalidOperationException("Step not reached.");
        }

        public TurnExecution Blocks(ObjectId attacker) => Round(Check(Combat.DeclareAttackers(Reach(Start(), Step.DeclareAttackers), A, [new(attacker, B)])));
        public TurnExecution Damage(ObjectId attacker, params ObjectId[] blockers) =>
            Round(Check(Combat.DeclareBlockers(Blocks(attacker), B, blockers.Select(b => new BlockDeclaration(b, attacker)))));
    }

    [Theory]
    [InlineData(BasicKeyword.Reach)]
    [InlineData(BasicKeyword.Flying)]
    public void Flying_accepts_reach_or_flying_but_rejects_ground_blockers(BasicKeyword defense)
    {
        var flyer = Card("flyer", 2, 3, BasicKeyword.Flying);
        var guard = Card("guard", 1, 3, defense);
        var ground = Card("ground", 1, 3);
        var t = new Table(flyer, guard, ground);
        var a = t.Add(flyer, A);
        var b = t.Add(guard, B);
        var c = t.Add(ground, B);
        var e = t.Blocks(a);
        Assert.Throws<InvalidOperationException>(() => t.Combat.DeclareBlockers(e, B, [new(c, a)]));
        e = t.Round(t.Check(t.Combat.DeclareBlockers(e, B, [new(b, a)])));
        e = t.Check(t.Combat.DealDamage(e, A));
        Assert.Equal(20, e.State.Player(B).Life);
        Assert.Equal(2, e.State.Objects[b].Damage);
        Assert.Equal(1, e.State.Objects[a].Damage);
    }

    [Fact]
    public void Reach_is_not_evasion_and_flying_can_block_ground()
    {
        var reach = Card("reach", 1, 3, BasicKeyword.Reach);
        var flying = Card("flying", 1, 3, BasicKeyword.Flying);
        var t = new Table(reach, flying);
        var a = t.Add(reach, A);
        var b = t.Add(flying, B);
        var e = t.Check(t.Combat.DealDamage(t.Damage(a, b), A));
        Assert.Equal(1, e.State.Objects[a].Damage);
        Assert.Equal(1, e.State.Objects[b].Damage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Haste_cast_this_turn_can_attack_and_vigilance_changes_only_attack_tap(bool vigilance)
    {
        var card = vigilance ? Card("runner", 2, 2, BasicKeyword.Haste, BasicKeyword.Vigilance) : Card("runner", 2, 2, BasicKeyword.Haste);
        var t = new Table(card);
        var hand = t.Add(card, A, Zone.Hand);
        var e = t.Check(t.Reach(t.Start(), Step.PrecombatMain));
        e = t.Check(t.Actions.Cast(e, A, new(hand, ManaPool.Empty)));
        e = t.Check(t.Actions.Resolve(t.Round(e)));
        var a = Assert.Single(e.State.Objects.Values, o => o.IsPermanent && o.Definition.OracleId == "runner").Id;
        e = t.Reach(e, Step.DeclareAttackers);
        Assert.True(e.State.Objects[a].SummoningSick);
        e = t.Combat.DeclareAttackers(e, A, [new(a, B)]);
        Assert.Equal(!vigilance, e.State.Objects[a].Tapped);
        e = t.Round(t.Check(e));
        e = t.Round(t.Check(t.Combat.DeclareBlockers(e, B, [])));
        e = t.Check(t.Combat.DealDamage(e, A));
        Assert.Equal(18, e.State.Player(B).Life);
    }

    [Fact]
    public void Defender_wins_over_haste_and_can_still_block()
    {
        var wall = Card("wall", 2, 4, BasicKeyword.Defender, BasicKeyword.Haste);
        var t = new Table(wall);
        var a = t.Add(wall, A);
        var e = t.Reach(t.Start(), Step.DeclareAttackers);
        Assert.Throws<InvalidOperationException>(() => t.Combat.DeclareAttackers(e, A, [new(a, B)]));
        var bear = Card("bear", 2, 2);
        t = new Table(wall, bear);
        a = t.Add(bear, A);
        var b = t.Add(wall, B);
        e = t.Check(t.Combat.DealDamage(t.Damage(a, b), A));
        Assert.False(e.State.Objects.ContainsKey(a));
        Assert.True(e.State.Objects.ContainsKey(b));
    }

    [Fact]
    public void Vigilance_does_not_allow_a_tapped_creature_to_attack()
    {
        var card = Card("watcher", 2, 2, BasicKeyword.Vigilance);
        var t = new Table(card);
        var a = t.Add(card, A);
        var e = t.Start();
        e = t.Check(e.Act(A, s => s with { Objects = s.Objects.SetItem(a, s.Objects[a] with { Tapped = true }) }));
        e = t.Reach(e, Step.DeclareAttackers);
        Assert.Throws<InvalidOperationException>(() => t.Combat.DeclareAttackers(e, A, [new(a, B)]));
    }

    [Fact]
    public void Menace_validates_whole_declaration_and_loss_of_one_blocker_does_not_undo_block()
    {
        var menace = Card("menace", 3, 4, BasicKeyword.Menace);
        var guard = Card("guard", 1, 4);
        var t = new Table(menace, guard);
        var a = t.Add(menace, A);
        var b = t.Add(guard, B);
        var c = t.Add(guard, B);
        var bounce = t.Add(Bounce, A);
        var e = t.Blocks(a);
        Assert.Throws<InvalidOperationException>(() => t.Combat.DeclareBlockers(e, B, [new(b, a)]));
        e = t.Check(t.Combat.DeclareBlockers(e, B, [new(b, a), new(c, a)]));
        e = t.Check(t.Actions.Cast(e, A, new(bounce, ManaPool.Empty, c)));
        e = t.Check(t.Actions.Resolve(t.Round(e)));
        e = t.Check(t.Combat.DealDamage(t.Round(e), A));
        Assert.Equal(20, e.State.Player(B).Life);
        Assert.Equal(3, e.State.Objects[b].Damage);
    }

    [Fact]
    public void Flying_and_menace_require_two_individually_legal_blockers()
    {
        var card = Card("threat", 2, 5, BasicKeyword.Flying, BasicKeyword.Menace);
        var reach = Card("reach", 1, 4, BasicKeyword.Reach);
        var ground = Card("ground", 1, 4);
        var t = new Table(card, reach, ground);
        var a = t.Add(card, A);
        var b = t.Add(reach, B);
        var c = t.Add(ground, B);
        var e = t.Blocks(a);
        Assert.Throws<InvalidOperationException>(() => t.Combat.DeclareBlockers(e, B, [new(b, a), new(c, a)]));
        e = t.Round(t.Check(t.Combat.DeclareBlockers(e, B, [])));
        Assert.Equal(18, t.Check(t.Combat.DealDamage(e, A)).State.Player(B).Life);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Trample_requires_lethal_assignment_even_against_indestructible(bool indestructible)
    {
        var trampler = Card("trampler", 5, 5, BasicKeyword.Trample);
        var guard = indestructible ? Card("guard", 2, 3, BasicKeyword.Indestructible) : Card("guard", 2, 3);
        var t = new Table(trampler, guard);
        var a = t.Add(trampler, A);
        var b = t.Add(guard, B);
        var e = t.Damage(a, b);
        Assert.Throws<InvalidOperationException>(() => t.Combat.DealDamage(e, A));
        Assert.Throws<InvalidOperationException>(() => t.Combat.DealDamage(e, A, [new(a, ImmutableDictionary<ObjectId, int>.Empty.Add(b, 2), 3)]));
        e = t.Check(t.Combat.DealDamage(e, A, [new(a, ImmutableDictionary<ObjectId, int>.Empty.Add(b, 3), 2)]));
        Assert.Equal(18, e.State.Player(B).Life);
        Assert.Equal(indestructible, e.State.Objects.ContainsKey(b));
    }

    [Fact]
    public void Deathtouch_trample_needs_one_per_blocker_and_lifelink_counts_all_damage()
    {
        var threat = Card("threat", 5, 5, BasicKeyword.Deathtouch, BasicKeyword.Trample, BasicKeyword.Lifelink);
        var guard = Card("guard", 1, 8);
        var t = new Table(threat, guard);
        var a = t.Add(threat, A);
        var b = t.Add(guard, B);
        var c = t.Add(guard, B);
        var e = t.Damage(a, b, c);
        Assert.Throws<InvalidOperationException>(() => t.Combat.DealDamage(e, A,
            [new(a, ImmutableDictionary<ObjectId, int>.Empty.Add(b, 1), 4)]));
        e = t.Combat.DealDamage(e, A, [new(a, ImmutableDictionary<ObjectId, int>.Empty.Add(b, 1).Add(c, 1), 3)]);
        Assert.True(e.State.Objects[b].DealtDeathtouchDamage);
        Assert.Equal(25, e.State.Player(A).Life);
        Assert.Equal(17, e.State.Player(B).Life);
        e = t.Check(e);
        Assert.False(e.State.Objects.ContainsKey(b));
        Assert.False(e.State.Objects.ContainsKey(c));
    }

    [Fact]
    public void Lifelink_is_simultaneous_with_other_damage_and_can_save_defender()
    {
        var attacker = Card("attacker", 3, 4);
        var guard = Card("guard", 3, 4, BasicKeyword.Lifelink);
        var t = new Table(attacker, guard);
        var a = t.Add(attacker, A);
        var extra = t.Add(attacker, A);
        var b = t.Add(guard, B);
        var e = t.Start();
        e = t.Check(e.Act(A, s => s with { Players = s.Players.SetItem(1, s.Player(B) with { Life = 1 }) }));
        e = t.Combat.DeclareAttackers(t.Reach(e, Step.DeclareAttackers), A, [new(a, B), new(extra, B)]);
        e = t.Round(t.Check(e));
        e = t.Round(t.Check(t.Combat.DeclareBlockers(e, B, [new(b, a)])));
        e = t.Check(t.Combat.DealDamage(e, A));
        Assert.Equal(1, e.State.Player(B).Life);
        Assert.False(e.State.Player(B).HasLost);
        Assert.Null(e.State.Outcome);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void Zero_damage_does_not_apply_deathtouch_or_lifelink(int power, int gained)
    {
        var threat = Card("threat", power, 5, BasicKeyword.Deathtouch, BasicKeyword.Lifelink);
        var guard = Card("guard", 1, 8);
        var t = new Table(threat, guard);
        var a = t.Add(threat, A);
        var b = t.Add(guard, B);
        var e = t.Check(t.Combat.DealDamage(t.Damage(a, b), A));
        Assert.Equal(20 + gained, e.State.Player(A).Life);
        Assert.Equal(power == 0, e.State.Objects.ContainsKey(b));
    }

    [Fact]
    public void Indestructible_ignores_lethal_and_deathtouch_but_not_zero_toughness()
    {
        var touch = Card("touch", 5, 5, BasicKeyword.Deathtouch);
        var immortal = Card("immortal", 1, 2, BasicKeyword.Indestructible);
        var zero = Card("zero", 1, 0, BasicKeyword.Indestructible);
        var t = new Table(touch, immortal, zero);
        var a = t.Add(touch, A);
        var b = t.Add(immortal, B);
        var z = t.Add(zero, B);
        var e = t.Damage(a, b);
        Assert.False(e.State.Objects.ContainsKey(z));
        e = t.Check(t.Combat.DealDamage(e, A));
        Assert.Equal(5, e.State.Objects[b].Damage);
        Assert.False(e.State.Objects[b].DealtDeathtouchDamage);
        e = t.Reach(e, Step.Cleanup).CompleteBasicTurnAction();
        Assert.Equal(0, e.State.Objects[b].Damage);
    }

    [Theory]
    [InlineData(BasicKeyword.FirstStrike)]
    [InlineData(BasicKeyword.DoubleStrike)]
    public void Early_strike_kills_blocker_before_it_can_deal_damage(BasicKeyword keyword)
    {
        var striker = Card("striker", 2, 2, keyword);
        var guard = Card("guard", 3, 2);
        var t = new Table(striker, guard);
        var a = t.Add(striker, A);
        var b = t.Add(guard, B);
        var e = t.Damage(a, b);
        Assert.Equal(Step.FirstStrikeCombatDamage, e.State.Turn.Step);
        e = t.Check(t.Combat.DealDamage(e, A));
        Assert.False(e.State.Objects.ContainsKey(b));
        Assert.Equal(0, e.State.Objects[a].Damage);
        Assert.Equal(ExecutionBoundary.Priority, e.Boundary);
        e = t.Round(e);
        Assert.Equal(Step.CombatDamage, e.State.Turn.Step);
        e = t.Check(t.Combat.DealDamage(e, A));
        Assert.Equal(20, e.State.Player(B).Life); // Still blocked, even with double strike.
        Assert.Equal(Step.EndOfCombat, t.Round(e).State.Turn.Step);
    }

    [Theory]
    [InlineData(BasicKeyword.FirstStrike, 18)]
    [InlineData(BasicKeyword.DoubleStrike, 16)]
    public void Unblocked_strikers_deal_in_the_correct_steps(BasicKeyword keyword, int life)
    {
        var card = Card("striker", 2, 2, keyword);
        var t = new Table(card);
        var a = t.Add(card, A);
        var e = t.Check(t.Combat.DealDamage(t.Damage(a), A));
        Assert.Equal(18, e.State.Player(B).Life);
        e = t.Check(t.Combat.DealDamage(t.Round(e), A));
        Assert.Equal(life, e.State.Player(B).Life);
    }

    [Fact]
    public void Double_strike_trample_uses_marked_damage_then_all_damage_after_blocker_dies()
    {
        var card = Card("striker", 4, 4, BasicKeyword.DoubleStrike, BasicKeyword.Trample);
        var guard = Card("guard", 1, 3);
        var t = new Table(card, guard);
        var a = t.Add(card, A);
        var b = t.Add(guard, B);
        var e = t.Check(t.Combat.DealDamage(t.Damage(a, b), A,
            [new(a, ImmutableDictionary<ObjectId, int>.Empty.Add(b, 3), 1)]));
        Assert.Equal(19, e.State.Player(B).Life);
        e = t.Check(t.Combat.DealDamage(t.Round(e), A));
        Assert.Equal(15, e.State.Player(B).Life);
    }

    [Fact]
    public void Response_between_strike_steps_can_remove_double_striker()
    {
        var card = Card("striker", 2, 2, BasicKeyword.DoubleStrike);
        var t = new Table(card);
        var a = t.Add(card, A);
        var bounce = t.Add(Bounce, A);
        var e = t.Check(t.Combat.DealDamage(t.Damage(a), A));
        e = t.Check(t.Actions.Cast(e, A, new(bounce, ManaPool.Empty, a)));
        e = t.Check(t.Actions.Resolve(t.Round(e)));
        e = t.Check(t.Combat.DealDamage(t.Round(e), A));
        Assert.Equal(18, e.State.Player(B).Life);
        Assert.False(e.State.Objects.ContainsKey(a));
    }

    [Fact]
    public void Bouncing_only_first_striker_before_damage_avoids_extra_damage_step()
    {
        var card = Card("striker", 2, 2, BasicKeyword.FirstStrike);
        var guard = Card("guard", 1, 3);
        var t = new Table(card, guard);
        var a = t.Add(card, A);
        var b = t.Add(guard, B);
        var bounce = t.Add(Bounce, A);
        var e = t.Check(t.Combat.DeclareBlockers(t.Blocks(a), B, [new(b, a)]));
        e = t.Check(t.Actions.Cast(e, A, new(bounce, ManaPool.Empty, a)));
        e = t.Check(t.Actions.Resolve(t.Round(e)));
        Assert.Equal(Step.CombatDamage, t.Round(e).State.Turn.Step);
    }

    [Fact]
    public void First_striking_blocker_kills_attacker_and_ordinary_blocker_waits()
    {
        var attacker = Card("attacker", 2, 2);
        var striker = Card("striker", 2, 3, BasicKeyword.FirstStrike);
        var ordinary = Card("ordinary", 1, 4);
        var t = new Table(attacker, striker, ordinary);
        var a = t.Add(attacker, A);
        var b = t.Add(striker, B);
        var c = t.Add(ordinary, B);
        var e = t.Damage(a, b, c);
        Assert.Equal(Step.FirstStrikeCombatDamage, e.State.Turn.Step);
        // No split is requested from the attacker: it does not deal damage in this step.
        e = t.Check(t.Combat.DealDamage(e, A));
        Assert.False(e.State.Objects.ContainsKey(a));
        Assert.Equal(0, e.State.Objects[b].Damage);
        Assert.Equal(0, e.State.Objects[c].Damage);
        e = t.Check(t.Combat.DealDamage(t.Round(e), A));
        Assert.Equal(20, e.State.Player(A).Life);
    }

    [Fact]
    public void Double_strike_trample_accounts_for_marked_damage_on_indestructible_blocker()
    {
        var attacker = Card("attacker", 3, 5, BasicKeyword.DoubleStrike, BasicKeyword.Trample);
        var guard = Card("guard", 1, 4, BasicKeyword.Indestructible);
        var t = new Table(attacker, guard);
        var a = t.Add(attacker, A);
        var b = t.Add(guard, B);
        var e = t.Check(t.Combat.DealDamage(t.Damage(a, b), A,
            [new(a, ImmutableDictionary<ObjectId, int>.Empty.Add(b, 3))]));
        Assert.Equal(3, e.State.Objects[b].Damage);
        e = t.Round(e);
        Assert.Throws<InvalidOperationException>(() => t.Combat.DealDamage(e, A, [new(a, ImmutableDictionary<ObjectId, int>.Empty, 3)]));
        e = t.Check(t.Combat.DealDamage(e, A, [new(a, ImmutableDictionary<ObjectId, int>.Empty.Add(b, 1), 2)]));
        Assert.Equal(18, e.State.Player(B).Life);
        Assert.Equal(4, e.State.Objects[b].Damage);
        Assert.Equal(1, e.State.Objects[a].Damage);
    }

    [Fact]
    public void Trample_can_overassign_to_blocker_and_only_affects_attacking_damage()
    {
        var attacker = Card("attacker", 4, 4, BasicKeyword.Trample);
        var blocker = Card("blocker", 5, 2, BasicKeyword.Trample);
        var t = new Table(attacker, blocker);
        var a = t.Add(attacker, A);
        var b = t.Add(blocker, B);
        var e = t.Check(t.Combat.DealDamage(t.Damage(a, b), A,
            [new(a, ImmutableDictionary<ObjectId, int>.Empty.Add(b, 4))]));
        Assert.Equal(20, e.State.Player(A).Life);
        Assert.Equal(20, e.State.Player(B).Life);
        Assert.False(e.State.Objects.ContainsKey(a));
        Assert.False(e.State.Objects.ContainsKey(b));
    }

    [Fact]
    public void Trample_hits_defender_after_all_blockers_are_bounced()
    {
        var card = Card("trampler", 3, 3, BasicKeyword.Trample);
        var guard = Card("guard", 1, 4);
        var t = new Table(card, guard);
        var a = t.Add(card, A);
        var b = t.Add(guard, B);
        var bounce = t.Add(Bounce, A);
        var e = t.Check(t.Combat.DeclareBlockers(t.Blocks(a), B, [new(b, a)]));
        e = t.Check(t.Actions.Cast(e, A, new(bounce, ManaPool.Empty, b)));
        e = t.Check(t.Actions.Resolve(t.Round(e)));
        e = t.Check(t.Combat.DealDamage(t.Round(e), A));
        Assert.Equal(17, e.State.Player(B).Life);
    }

    [Fact]
    public void Keyword_game_rejects_quiet_checkpoint_provider_and_hides_registered_hand_identity()
    {
        var card = Card("bird", 1, 1, BasicKeyword.Flying);
        var t = new Table(card);
        t.Add(card, B, Zone.Hand);
        var e = TurnExecution.Start(t.State, A).CompleteBasicTurnAction();
        Assert.Throws<NotSupportedException>(() => t.Actions.CompleteCheckpoint(e));
        e = t.Check(e);
        var json = System.Text.Json.JsonSerializer.Serialize(MtgEngine.Rules.Views.ZoneStateView.For(e.State, A));
        Assert.DoesNotContain("bird", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Keywords", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cast_haste_double_strike_lifelink_creature_plays_through_to_a_winner()
    {
        var card = Card("finisher", 10, 3, BasicKeyword.Haste, BasicKeyword.DoubleStrike, BasicKeyword.Lifelink);
        var t = new Table(card);
        var hand = t.Add(card, A, Zone.Hand);
        var e = t.Check(t.Reach(t.Start(), Step.PrecombatMain));
        e = t.Check(t.Actions.Cast(e, A, new(hand, ManaPool.Empty)));
        e = t.Check(t.Actions.Resolve(t.Round(e)));
        var a = Assert.Single(e.State.Objects.Values, o => o.IsPermanent).Id;
        e = t.Combat.DeclareAttackers(t.Reach(e, Step.DeclareAttackers), A, [new(a, B)]);
        e = t.Round(t.Check(e));
        e = t.Round(t.Check(t.Combat.DeclareBlockers(e, B, [])));
        e = t.Check(t.Combat.DealDamage(e, A));
        Assert.Equal(10, e.State.Player(B).Life);
        Assert.Equal(30, e.State.Player(A).Life);
        e = t.Check(t.Combat.DealDamage(t.Round(e), A));
        Assert.Equal(ExecutionBoundary.Finished, e.Boundary);
        Assert.Equal([A], e.State.Outcome!.Winners);
        Assert.Equal(40, e.State.Player(A).Life);
    }

    [Fact]
    public void Explicit_registration_rejects_mismatched_text_unknown_keywords_and_noncreature_keywords()
    {
        var card = Card("bird", 1, 1, BasicKeyword.Flying);
        Assert.Throws<NotSupportedException>(() => new BasicActions([card with { Keywords = [] }]));
        Assert.Throws<NotSupportedException>(() => new BasicActions([card with { Keywords = [(BasicKeyword)999] }]));
        Assert.Throws<NotSupportedException>(() => new BasicActions([new(Forest, BasicBehavior.Land) { Keywords = [BasicKeyword.Haste] }]));
        var extra = card.Definition with { Faces = [card.Definition.Faces[0] with { OracleText = "Flying\nDraw a card." }] };
        Assert.Throws<NotSupportedException>(() => new BasicActions([card with { Definition = extra }]));
        var t = new Table(card);
        t.Add(extra, A, Zone.Hand);
        Assert.Throws<NotSupportedException>(() => t.Start());
    }
}
