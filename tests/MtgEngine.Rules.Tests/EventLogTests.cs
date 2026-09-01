using MtgEngine.Domain.Enums;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The log is the game and the state is a fold of it.
/// </summary>
/// <remarks>
/// This is the property the engine was rebuilt to have. If it holds, a game reported as broken
/// can be replayed exactly and pasted into a test; if it stops holding, every other guarantee
/// here is worth less, which is why it is asserted after a mixed run of actions rather than on
/// a fresh game.
/// </remarks>
public sealed class EventLogTests
{
    [Fact]
    public void Replaying_the_log_reproduces_the_state()
    {
        var (game, alice, bob) = TestCards.TwoPlayer();

        game.Draw(alice);
        game.Draw(alice);
        game.Draw(bob);
        var creature = game.Move(
            game.State.GetPlayer(alice).Hand[0], Zone.Battlefield, MoveCause.Play);
        game.ChangeLife(bob, -3);
        game.Move(creature, Zone.Graveyard, MoveCause.Destroy);
        game.ChangeLife(alice, 2);

        var replayed = GameReducer.Replay(game.Log);

        Assert.Equal(game.State, replayed);
    }

    [Fact]
    public void A_log_has_to_begin_with_the_game_starting()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => GameReducer.Replay([new LifeChanged(Guid.NewGuid(), -1, 19)]));

        Assert.Contains("GameStarted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_log_is_not_a_game()
    {
        Assert.Throws<InvalidOperationException>(() => GameReducer.Replay([]));
    }

    [Fact]
    public void A_rejected_action_leaves_no_trace_in_the_log()
    {
        // Events say what happened, never what was asked for. A move of something that is not
        // there fails before anything is recorded, so the log stays replayable.
        var (game, _, _) = TestCards.TwoPlayer();
        var before = game.Log.Count;

        Assert.Throws<InvalidOperationException>(() => game.Move(ObjectId.New(), Zone.Hand));

        Assert.Equal(before, game.Log.Count);
        Assert.Equal(game.State, GameReducer.Replay(game.Log));
    }

    [Fact]
    public void Changing_life_by_nothing_records_nothing()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        var before = game.Log.Count;

        game.ChangeLife(alice, 0);

        Assert.Equal(before, game.Log.Count);
    }

    [Fact]
    public void Events_cite_the_rule_they_answer_to()
    {
        // The Comprehensive Rules are a live asset in this repo, so a log line can be traced to
        // the sentence that caused it rather than to a comment about it.
        var (game, alice, _) = TestCards.TwoPlayer();
        game.Move(game.State.GetPlayer(alice).Library[0], Zone.Hand, MoveCause.Draw);

        var moved = game.Log.OfType<ObjectMoved>().Last();

        Assert.Equal("400.7", moved.Rule);
    }

    [Fact]
    public void The_game_starts_with_libraries_dealt_and_shuffled()
    {
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 10);

        Assert.IsType<GameStarted>(game.Log[0]);
        Assert.Equal(2, game.Log.OfType<LibraryShuffled>().Count());
        Assert.Equal(10, game.State.GetPlayer(alice).Library.Count);
        Assert.Equal(10, game.State.GetPlayer(bob).Library.Count);
        // CR 103.5: opening hands are part of the mulligan procedure, which needs priority.
        Assert.Empty(game.State.GetPlayer(alice).Hand);
    }

    [Fact]
    public void Every_event_type_is_registered_for_storage()
    {
        // The negative control for persistence. An event that is not registered cannot be
        // written, so a game containing it becomes unsavable — and the failure would show up as
        // a lost game rather than as a build error. Adding an event without registering it
        // fails here instead.
        var declared = typeof(GameEvent).Assembly
            .GetTypes()
            .Where(t => t.IsSubclassOf(typeof(GameEvent)) && !t.IsAbstract)
            .ToList();

        var missing = declared
            .Where(t => !EventLogSerializer.KnownEvents.Values.Contains(t))
            .Select(t => t.Name)
            .ToList();

        Assert.True(missing.Count == 0, "Unregistered events: " + string.Join(", ", missing));
        Assert.Equal(declared.Count, EventLogSerializer.KnownEvents.Count);
    }

    [Fact]
    public void A_stored_log_replays_to_the_same_game()
    {
        // This is what persistence is. The log is the game, so storing a game is storing its
        // events — there is no schema for state to design and no way for a stored game to
        // disagree with the engine that wrote it.
        var (game, alice, bob) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay(withMulligans: false);
        TestCards.PassToStep(game, TurnStep.PrecombatMain);
        game.PlayLand(alice, TestCards.PutInHand(game, alice, TestCards.BasicLand()));
        game.CastSpell(alice, TestCards.PutInHand(game, alice, TestCards.Creature("Ox")));
        game.PassPriority(alice);
        game.PassPriority(bob);
        TestCards.PassToTurn(game, 3);

        var stored = EventLogSerializer.Write(game.Log);
        var reloaded = GameReducer.Replay(EventLogSerializer.Read(stored));

        Assert.Equal(game.State, reloaded);
    }

    [Fact]
    public void A_reloaded_card_keeps_the_characteristics_the_engine_reads()
    {
        // The negative control for the card table. GameObject equality compares cards by oracle
        // id, so the round-trip test above would still pass if the table had dropped power,
        // toughness or keywords — and the game would then play differently on reload, which is
        // the one thing persistence must not do.
        var (game, alice, _) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay(withMulligans: false);
        var flier = game.Create(
            alice, TestCards.WithKeyword("Drake", KeywordAbility.Flying, 2, 3), Zone.Battlefield);

        var reloaded = GameReducer.Replay(
            EventLogSerializer.Read(EventLogSerializer.Write(game.Log)));
        var card = reloaded.GetObject(flier).Card;

        Assert.Equal("Drake", card.Name);
        Assert.Equal(2, card.Power);
        Assert.Equal(3, card.Toughness);
        Assert.True(card.HasKeyword(KeywordAbility.Flying));
        Assert.Equal(CardType.Creature, card.CardTypes);
    }

    [Fact]
    public void A_game_log_does_not_record_what_a_card_was_worth()
    {
        // A price is not part of a game. Storing one in a game log records something that
        // changes without the game changing, and puts a card's market value in a document whose
        // reason to exist is replaying a match.
        var (game, alice, _) = TestCards.TwoPlayer(deckSize: 40);
        game.BeginPlay(withMulligans: false);
        game.Create(alice, TestCards.Creature("Bear", 2, 2), Zone.Battlefield);

        var stored = EventLogSerializer.Write(game.Log);

        Assert.DoesNotContain("Prices", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ImageUri", stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Legalities", stored, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_log_with_an_event_this_build_cannot_read_is_refused()
    {
        // Dropping the line would produce a game that folds to a subtly different position,
        // which is worse than refusing to load it.
        var (game, _, _) = TestCards.TwoPlayer();
        var stored = EventLogSerializer.Write(game.Log)
            .Replace("\"LibraryShuffled\"", "\"SomethingFromTheFuture\"", StringComparison.Ordinal);

        Assert.ThrowsAny<Exception>(() => EventLogSerializer.Read(stored));
    }

    [Fact]
    public void The_game_has_not_reached_turn_one_until_a_turn_begins()
    {
        var (game, _, _) = TestCards.TwoPlayer();

        Assert.Equal(0, game.State.TurnNumber);
    }

    /// <summary>
    /// Every event the engine can emit has to be one the serializer can read back (CR n/a).
    /// </summary>
    /// <remarks>
    /// A completeness check by reflection rather than a list, because a list is exactly the thing
    /// that goes stale: an event added without a registry entry serializes fine and throws on the
    /// way back in, which is to say the game is saved and cannot be loaded. Fifteen events were
    /// added in one stretch of work on the card compiler, and nothing else would have noticed a
    /// missing one until a stored game failed to open.
    /// </remarks>
    [Fact]
    public void Every_event_type_can_be_read_back()
    {
        var declared = typeof(GameEvent).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(GameEvent)) && !t.IsAbstract)
            .Select(t => t.Name)
            .ToList();

        Assert.NotEmpty(declared);

        var missing = declared
            .Where(name => !EventLogSerializer.KnownEvents.ContainsKey(name))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Events the serializer cannot read back: " + string.Join(", ", missing));

        // And nothing registered under a name no event answers to, which would be a rename that
        // left the old entry behind — silently unreadable for every game stored before it.
        var stale = EventLogSerializer.KnownEvents
            .Where(pair => !declared.Contains(pair.Key, StringComparer.Ordinal))
            .Select(pair => pair.Key)
            .ToList();

        Assert.True(stale.Count == 0, "Registered names with no event: " + string.Join(", ", stale));
    }
}
