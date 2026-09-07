using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.Sba;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Continuous effects that apply to a player rather than to an object, CR 613.10: what the layer
/// system reports about a player, and what the state-based actions do about it. Every board is
/// made up; no card name and no card file takes part.
/// </summary>
public sealed class PlayerEffectTests
{
    private static readonly PlayerId Mine = new(0);
    private static readonly PlayerId Theirs = new(1);

    private readonly LayerSystem _layers = new();

    private readonly StateBasedActions _sba = new(new FakeZones(), new LayerSystem(), new FakeQuery(new FakeCharacteristics()));

    [Fact]
    [RuleRef("104.3")]
    public void A_player_who_cant_lose_is_still_in_the_game_at_zero_life()
    {
        // CR 101.2: the "can't" takes precedence over the loss 704.5a would perform.
        var board = new Board().Life(0, 0);
        var state = With(board.State, PlayerCantLose(Mine));

        var outcome = _sba.Check(state);

        Assert.Empty(outcome.Events.OfType<PlayerLost>());
        Assert.False(outcome.State.Player(Mine).HasLost);
        Assert.Null(outcome.State.Outcome);
    }

    [Fact]
    [RuleRef("704.5a")]
    public void The_player_loses_the_moment_the_effect_ends()
    {
        var board = new Board().Life(0, 0);
        var state = With(board.State, PlayerCantLose(Mine));
        Assert.Empty(_sba.Check(state).Events.OfType<PlayerLost>());

        // CR 613.10 again with the effect gone: 704.5a reads the state, and the state still has
        // this player at 0 life, so the loss happens on the very next check.
        var ended = new ActiveEffectChanged(1, null).Apply(state);
        var outcome = _sba.Check(ended);

        var lost = Assert.Single(outcome.Events.OfType<PlayerLost>());
        Assert.Equal(Mine, lost.Player);
        Assert.Contains("life", lost.Reason, StringComparison.Ordinal);
    }

    [Fact]
    [RuleRef("704.5b")]
    public void A_prevented_loss_still_consumes_the_drew_from_an_empty_library_flag()
    {
        // 704.5b is "since the last time state-based actions were checked", so the check that
        // read the flag consumes it even though the loss it would have caused did not happen.
        var board = new Board().DrewFromEmptyLibrary(0);
        var state = With(board.State, PlayerCantLose(Mine));

        var outcome = _sba.Check(state);

        Assert.Empty(outcome.Events.OfType<PlayerLost>());
        Assert.Contains(outcome.Events.OfType<PlayerFlagCleared>(), e => e.Player == Mine);
        Assert.False(outcome.State.Player(Mine).DrewFromEmptyLibrary);
    }

    [Fact]
    [RuleRef("104.2a")]
    public void The_last_player_remaining_wins_even_while_an_effect_says_they_cant_win()
    {
        // CR 104.2a: this win "happens immediately and overrides all effects that would preclude
        // that player from winning the game". "You can't win" bites on 104.2b, not on this.
        var board = new Board().Lost(1);
        var state = With(board.State, Active(1, new CantWinGame(new You()), Mine));

        Assert.True(_layers.PlayerRestrictions(state, Mine).CantWin);

        var outcome = _sba.Check(state);

        var ended = Assert.Single(outcome.Events.OfType<GameEnded>());
        Assert.Equal(Mine, Assert.Single(ended.Outcome.Winners));
        Assert.False(ended.Outcome.Draw);
    }

    [Fact]
    [RuleRef("119.3")]
    public void A_life_total_that_cant_change_reports_the_flag_and_ends_when_the_effect_does()
    {
        var state = With(new Board().State, Active(1, new PlayerCant(PlayerRestriction.LifeTotalChanges), Mine, new UntilEndOfTurn()));

        var limits = _layers.PlayerRestrictions(state, Mine);
        Assert.True(limits.LifeTotalCantChange);
        // A life total that can't change can't go up either, so no life-gain reaches this player.
        Assert.True(limits.CantGainLifeNow);

        // The duration is end of turn (CR 611.2b); the cleanup step's ActiveEffectChanged ends it.
        Assert.IsType<UntilEndOfTurn>(Assert.Single(state.ActiveEffects).Until);
        var after = new ActiveEffectChanged(1, null).Apply(state);

        Assert.False(_layers.PlayerRestrictions(after, Mine).LifeTotalCantChange);
    }

    [Fact]
    [RuleRef("702.16j")]
    public void Protection_from_everything_reports_prevented_damage_and_refused_targeting()
    {
        var state = With(new Board().State, Active(1, new PlayerProtectionFromEverything(), Mine));

        var limits = _layers.PlayerRestrictions(state, Mine);

        Assert.True(limits.ProtectionFromEverything);
        Assert.True(limits.CantBeDealtDamage);
        Assert.True(limits.CantBeTargeted);
        // Protection is not a shield against losing: this player still loses at 0 life.
        Assert.False(limits.CantLose);
    }

    [Fact]
    [RuleRef("120.3")]
    public void A_player_who_cant_be_dealt_damage_reports_it_without_protection()
    {
        var state = With(new Board().State, Active(1, new PlayerCant(PlayerRestriction.BeDealtDamage), Mine));

        var limits = _layers.PlayerRestrictions(state, Mine);

        Assert.True(limits.CantBeDealtDamage);
        Assert.False(limits.ProtectionFromEverything);
        Assert.False(limits.CantBeTargeted);
    }

    [Fact]
    [RuleRef("613.10")]
    public void The_older_record_and_the_flag_are_the_same_answer()
    {
        var older = With(new Board().State, Active(1, new CantLoseGame(new You()), Mine));
        var flag = With(new Board().State, Active(1, new PlayerCant(PlayerRestriction.Lose), Mine));

        Assert.Equal(_layers.PlayerRestrictions(older, Mine), _layers.PlayerRestrictions(flag, Mine));
        Assert.True(_layers.PlayerRestrictions(older, Mine).CantLose);
    }

    [Fact]
    [RuleRef("611.2c")]
    public void An_effect_naming_the_other_player_does_not_apply_to_this_one()
    {
        var state = With(new Board().State, Active(1, new PlayerCant(PlayerRestriction.Lose), Theirs));

        Assert.True(_layers.PlayerRestrictions(state, Theirs).CantLose);
        Assert.Equal(PlayerLimits.None, _layers.PlayerRestrictions(state, Mine));
    }

    [Fact]
    [RuleRef("604.2")]
    public void A_static_ability_on_the_battlefield_restricts_its_controller()
    {
        // CR 611.3a: the set of players is recomputed on every call, so "you" is whoever
        // controls the permanent now, and the opponent is untouched.
        var board = new Board();
        board.Put(Persistent(new StaticAbility(new PlayerCant(PlayerRestriction.Lose), Players: new You())), owner: 0);

        Assert.True(_layers.PlayerRestrictions(board.State, Mine).CantLose);
        Assert.False(_layers.PlayerRestrictions(board.State, Theirs).CantLose);
    }

    [Fact]
    [RuleRef("604.2")]
    public void A_static_ability_naming_each_opponent_restricts_them_and_not_its_controller()
    {
        var board = new Board();
        board.Put(Persistent(new StaticAbility(new CantLoseGame(new EachOpponent()), Players: new EachOpponent())), owner: 0);

        Assert.True(_layers.PlayerRestrictions(board.State, Theirs).CantLose);
        Assert.False(_layers.PlayerRestrictions(board.State, Mine).CantLose);
    }

    [Fact]
    [RuleRef("104.3")]
    public void A_static_ability_keeps_an_opponent_in_the_game_at_zero_life()
    {
        var board = new Board().Life(1, 0);
        board.Put(Persistent(new StaticAbility(new CantLoseGame(new EachOpponent()), Players: new EachOpponent())), owner: 0);

        var outcome = _sba.Check(board.State);

        Assert.Empty(outcome.Events.OfType<PlayerLost>());
        Assert.Null(outcome.State.Outcome);
    }

    [Fact]
    [RuleRef("708.2a")]
    public void A_face_down_permanents_player_restriction_does_not_apply()
    {
        var board = new Board();
        var id = board.Put(Persistent(new StaticAbility(new PlayerCant(PlayerRestriction.Lose), Players: new You())), owner: 0);
        var state = board.State;
        state = state with { Objects = state.Objects.SetItem(id, state.Objects[id] with { FaceDown = true }) };

        Assert.False(_layers.PlayerRestrictions(state, Mine).CantLose);
    }

    [Fact]
    [RuleRef("613.10")]
    public void Two_effects_on_one_player_are_one_union_of_flags()
    {
        var state = new Board().State;
        state = state with
        {
            ActiveEffects =
            [
                Active(1, new PlayerCant(PlayerRestriction.Lose), Mine),
                Active(2, new PlayerCant(PlayerRestriction.BeTargeted | PlayerRestriction.BeDealtDamage), Mine),
                Active(3, new CantGainLife(new You()), Mine),
            ],
        };

        var limits = _layers.PlayerRestrictions(state, Mine);

        Assert.True(limits.CantLose);
        Assert.True(limits.CantBeTargeted);
        Assert.True(limits.CantBeDealtDamage);
        Assert.True(limits.CantGainLifeNow);
        Assert.False(limits.LifeTotalCantChange);
    }

    [Fact]
    [RuleRef("613.10")]
    public void An_object_scoped_effect_restricts_no_player()
    {
        var state = With(new Board().State, Active(1, new ModifyPt(new Fixed(1), new Fixed(1)), Mine));

        Assert.Equal(PlayerLimits.None, _layers.PlayerRestrictions(state, Mine));
    }

    private static GameState With(GameState state, ActiveEffect effect) =>
        state with { ActiveEffects = [effect] };

    private static ActiveEffect PlayerCantLose(PlayerId player) =>
        Active(1, new PlayerCant(PlayerRestriction.Lose), player);

    /// <summary>An effect that applies to a player, so it names them in <c>AppliesToPlayers</c> (CR 611.2c).</summary>
    private static ActiveEffect Active(int id, ContinuousEffect effect, PlayerId player, Duration? until = null) =>
        new(id, effect, null, player, [], [player], until ?? new UntilYourNextTurn(), id, false);

    /// <summary>An enchantment whose only ability is the static one under test.</summary>
    private static CardDefinition Persistent(StaticAbility ability) =>
        Board.Definition("player effect ward", CardType.Enchantment, abilities: [ability]);
}
