using System.Collections.Immutable;
using System.Reflection;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// That two game states compare by what they describe, not by which objects hold them.
/// </summary>
/// <remarks>
/// These are the negative controls for <see cref="EventLogTests.Replaying_the_log_reproduces_the_state"/>.
/// That test compares two states for equality, so an equality that answered "yes" to everything
/// would pass it — and the generated record equality, which compares immutable collections by
/// reference, answered "no" to everything instead and made it fail for the wrong reason. Both
/// directions have to be pinned or the replay guarantee is decoration.
/// </remarks>
public sealed class StateEqualityTests
{
    [Fact]
    public void A_state_equals_itself_rebuilt_from_its_own_log()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        game.Draw(alice);

        Assert.Equal(game.State, GameReducer.Replay(game.Log));
    }

    [Fact]
    public void A_replay_that_stops_early_does_not_equal_the_finished_state()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        game.Draw(alice);
        game.Draw(alice);

        var oneEventShort = GameReducer.Replay(game.Log.Take(game.Log.Count - 1));

        Assert.NotEqual(game.State, oneEventShort);
    }

    [Fact]
    public void Moving_one_card_makes_the_state_different()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        var before = game.State;

        game.Draw(alice);

        Assert.NotEqual(before, game.State);
    }

    [Fact]
    public void A_different_life_total_makes_the_state_different()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        var before = game.State;

        game.ChangeLife(alice, -1);

        Assert.NotEqual(before, game.State);
    }

    [Fact]
    public void The_order_of_a_zone_is_part_of_the_state()
    {
        // CR 400.5: order in a library, graveyard or on the stack is not free to change. Two
        // libraries holding the same cards in a different order are different positions.
        var (game, alice, _) = TestCards.TwoPlayer();
        var player = game.State.GetPlayer(alice);

        var reversed = game.State.WithPlayer(player with { Library = [.. player.Library.Reverse()] });

        Assert.NotEqual(game.State, reversed);
    }

    [Fact]
    public void A_tapped_permanent_is_not_equal_to_an_untapped_one()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        var id = game.Move(game.State.GetPlayer(alice).Library[0], Zone.Battlefield, MoveCause.Play);
        var before = game.State;

        var permanent = before.GetObject(id);
        var tapped = before.WithObject(
            permanent with { Permanent = permanent.Permanent! with { IsTapped = true } });

        Assert.NotEqual(before, tapped);
    }

    [Fact]
    public void Counters_are_compared_by_content()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        var id = game.Move(game.State.GetPlayer(alice).Library[0], Zone.Battlefield, MoveCause.Play);
        var permanent = game.State.GetObject(id);

        var one = permanent.Permanent! with { Counters = new Dictionary<string, int> { ["+1/+1"] = 2 }.ToImmutableDictionary() };
        var same = permanent.Permanent! with { Counters = new Dictionary<string, int> { ["+1/+1"] = 2 }.ToImmutableDictionary() };
        var other = permanent.Permanent! with { Counters = new Dictionary<string, int> { ["+1/+1"] = 3 }.ToImmutableDictionary() };

        Assert.Equal(one, same);
        Assert.NotEqual(one, other);
    }

    [Fact]
    public void Equal_states_agree_on_their_hash_code()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        game.Draw(alice);

        var replayed = GameReducer.Replay(game.Log);

        Assert.Equal(game.State.GetHashCode(), replayed.GetHashCode());
    }

    /// <summary>
    /// Which state triggers have already fired is part of the position (CR 603.8).
    /// </summary>
    /// <remarks>
    /// <c>GameState.Equals</c> is a hand-written list beside a growing record, and this field was
    /// missing from it — the same omission the comment beside <c>Delayed</c> describes, live
    /// again and hiding the same way. Two states differing only in which state-triggered
    /// abilities have already fired for a condition that is still true compared <em>equal</em>,
    /// so <c>Replay(log) == State</c> — the invariant every behaviour test leans on — passed
    /// straight through a divergence in them, and the replayed game would fire every one of
    /// those triggers a second time.
    /// <para>
    /// The omission was not a decision. <see cref="Structural"/> had overloads for a list and a
    /// dictionary and none for a set, so the one field that is a set had nowhere to go.
    /// </para>
    /// <para>
    /// Asserted here rather than by pointing the reflective check below at <c>GameState</c>,
    /// which is where it belongs and where it is not: that check needs a way to vary every one
    /// of thirty-two property types and today knows about a dozen. Until it does, the whole of
    /// the guard on this type is tests like this one, written a field at a time.
    /// </para>
    /// </remarks>
    [Fact]
    public void Two_states_differing_only_in_their_armed_state_triggers_are_not_equal()
    {
        var (game, _, _) = TestCards.TwoPlayer();

        var armed = game.State with
        {
            ArmedStateTriggers = game.State.ArmedStateTriggers.Add("some-source:some-ability"),
        };

        Assert.NotEqual(game.State, armed);
        Assert.Equal(game.State, game.State with { });
    }

    /// <summary>
    /// Every field of a permanent's state is part of whether two of them are the same.
    /// </summary>
    /// <remarks>
    /// <c>GameReducer.Replay(log) == State</c> is the invariant the whole engine rests on, and
    /// every behaviour test in the suite asserts it on the way past. A field left out of
    /// <c>Equals</c> does not break that assertion - it <em>defeats</em> it: the replayed state
    /// differs from the real one in exactly that field and the comparison says they match.
    /// <para>
    /// Three separate vocabulary lists in this engine have gone stale by being written twice, and
    /// this is the same shape - a hand-written comparison beside a growing record. Four fields
    /// were added to these two types in one session (a Class's level, a Case's solved flag, a
    /// Room's open doors, a card on an adventure); nothing but care was stopping the fifth from
    /// being missed.
    /// </para>
    /// <para>
    /// The test varies one property at a time and asserts the result is no longer equal. A
    /// property it cannot vary is reported rather than skipped quietly, so a new field of an
    /// unfamiliar type fails loudly instead of being waved through.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_field_of_a_permanent_is_part_of_its_identity()
        => AssertEveryFieldCounts(new PermanentState());

    /// <summary>Every field of an object's state is part of its identity, for the same reason.</summary>
    [Fact]
    public void Every_field_of_an_object_is_part_of_its_identity()
        => AssertEveryFieldCounts(new GameObject
        {
            Id = ObjectId.New(),
            Card = TestCards.Creature("Identity Bear"),
            Zone = Zone.Battlefield,
            OwnerId = Guid.NewGuid(),
            ControllerId = Guid.NewGuid(),
            Timestamp = 1,
        });

    /// <summary>A player's state is folded from the log too, and compared the same way.</summary>
    [Fact]
    public void Every_field_of_a_player_is_part_of_their_identity()
        => AssertEveryFieldCounts(new PlayerState
        {
            PlayerId = Guid.NewGuid(),
            Name = "Identity",
            Life = 20,
        });

    /// <summary>
    /// Every settable characteristic survives both of the builder's copy paths (CR 613.8).
    /// </summary>
    /// <remarks>
    /// `CharacteristicsBuilder` is copied twice over: once into a throwaway that answers a
    /// dependency question, and once into the `ComputedCharacteristics` everything downstream
    /// reads. A field added to one and not the other is silent - the effect works while it is
    /// being computed and is gone by the time anything looks, or the dependency check answers
    /// about a characteristic the real computation will not have.
    /// <para>
    /// Found the hard way: adding `MinBlockers` landed in one path and not the other and the
    /// build was perfectly happy. Reached by reflection rather than by opening the type up,
    /// because the seam is internal on purpose and a test is not a reason to widen it.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_characteristic_survives_both_copy_paths()
    {
        var builderType = typeof(Characteristics).Assembly
            .GetType("MtgEngine.Rules.Abilities.CharacteristicsBuilder")!;

        var subject = new GameObject
        {
            Id = ObjectId.New(),
            Card = TestCards.Creature("Copy Path Bear"),
            Zone = Zone.Battlefield,
            OwnerId = Guid.NewGuid(),
            ControllerId = Guid.NewGuid(),
            Timestamp = 1,
        };

        var builder = Activator.CreateInstance(
            builderType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [subject],
            culture: null)!;

        var copyMethod = builderType.GetMethod(
            "Copy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var buildMethod = builderType.GetMethod(
            "Build", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var missed = new List<string>();

        foreach (var property in builderType.GetProperties())
        {
            if (property.SetMethod is null || property.GetIndexParameters().Length > 0)
                continue;

            var varied = Vary(property.GetValue(builder), property.PropertyType);
            if (varied is null)
                continue;

            property.SetValue(builder, varied);

            var copy = copyMethod.Invoke(builder, null)!;
            if (!Equals(builderType.GetProperty(property.Name)!.GetValue(copy), varied))
                missed.Add($"{property.Name} is lost by Copy()");

            var built = buildMethod.Invoke(builder, null)!;
            var onComputed = built.GetType().GetProperty(property.Name);
            if (onComputed is not null && !Equals(onComputed.GetValue(built), varied))
                missed.Add($"{property.Name} is lost by Build()");
        }

        Assert.True(missed.Count == 0, string.Join(", ", missed));
    }

    private static void AssertEveryFieldCounts<T>(T baseline)
        where T : notnull
    {
        var missed = new List<string>();
        var unvaried = new List<string>();

        foreach (var property in typeof(T).GetProperties())
        {
            if (property.SetMethod is null || property.GetIndexParameters().Length > 0)
                continue;

            var current = property.GetValue(baseline);
            if (Vary(current, property.PropertyType) is not { } different)
            {
                unvaried.Add($"{property.Name} ({property.PropertyType.Name})");
                continue;
            }

            var changed = (T)typeof(T)
                .GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(baseline, null)!;

            property.SetValue(changed, different);

            if (baseline.Equals(changed))
                missed.Add(property.Name);
        }

        Assert.True(
            unvaried.Count == 0,
            $"{typeof(T).Name}: no way to vary these, so they were never checked:\n  "
                + string.Join("\n  ", unvaried));

        Assert.True(
            missed.Count == 0,
            $"{typeof(T).Name}: changing these leaves the state equal, so a replay that got them "
                + $"wrong would still be reported as matching:\n  " + string.Join("\n  ", missed));
    }

    /// <summary>A value of this type that is not the one given.</summary>
    private static object? Vary(object? current, Type type)
    {
        var bare = Nullable.GetUnderlyingType(type) ?? type;

        if (bare == typeof(bool))
            return !(bool)(current ?? false);

        if (bare == typeof(int))
            return (int)(current ?? 0) + 1;

        if (bare == typeof(long))
            return (long)(current ?? 0L) + 1L;

        if (bare == typeof(Guid))
            return Guid.NewGuid();

        if (bare == typeof(ObjectId))
            return ObjectId.New();

        if (bare == typeof(string))
            return (current as string ?? string.Empty) + "different";

        if (bare.IsEnum)
        {
            return Enum.GetValues(bare)
                .Cast<object>()
                .FirstOrDefault(v => !v.Equals(current));
        }

        // The collections and the records inside the state each have their own shape, so they are
        // varied by asking them for one more of whatever they hold.
        if (current is ImmutableHashSet<int> ints)
            return ints.Add(ints.Count + 1);

        if (current is ImmutableHashSet<ObjectId> ids)
            return ids.Add(ObjectId.New());

        if (current is ImmutableList<int> list)
            return list.Add(list.Count + 1);

        if (current is ImmutableList<ObjectId> objects)
            return objects.Add(ObjectId.New());

        if (current is ImmutableList<string> words)
            return words.Add("different" + words.Count);

        if (current is ImmutableList<Target> targets)
            return targets.Add(Target.ToPlayer(Guid.NewGuid()));

        if (current is ImmutableList<CardDefinition> cards)
            return cards.Add(TestCards.Creature("Identity Spliced"));

        if (bare == typeof(AbilityOnStack))
        {
            return current is null
                ? new AbilityOnStack
                {
                    SourceId = ObjectId.New(),
                    AbilityId = "different",
                    Text = "different",
                }
                : null;
        }

        if (current is ImmutableDictionary<string, int> counters)
            return counters.SetItem("different", counters.Count + 1);

        if (bare == typeof(PermanentState))
            return current is null ? new PermanentState() : null;

        // Sneak writes the attack a spell will join onto the spell itself, so a nullable
        // AttackTarget is now one of the fields that has to count towards identity.
        if (bare == typeof(AttackTarget))
            return current is null ? AttackTarget.Player(Guid.NewGuid()) : null;

        if (bare == typeof(CardDefinition))
            return TestCards.Creature("Identity Other");

        if (bare == typeof(ManaPool))
            return current is ManaPool pool ? pool.Add(ManaColor.Green, 1) : null;

        return null;
    }
}
