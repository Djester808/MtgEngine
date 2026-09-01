using System.Collections.Immutable;
using System.Reflection;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
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
    /// Kept as a named test even though <see cref="Every_field_of_a_state_is_part_of_its_identity"/>
    /// now covers the same field reflectively, because this one names the rule and the bug: a
    /// reader who breaks it should be told which CR 603.8 fact went missing, not only that
    /// property number twenty-three stopped counting.
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
    /// And the whole position, which is the one this invariant is actually about.
    /// </summary>
    /// <remarks>
    /// <c>GameReducer.Replay(log) == State</c> is asserted by every behaviour test in the suite,
    /// and the thing it compares is a <see cref="GameState"/>. So this is the type the guard was
    /// for, and the type it did not cover: the three above it are the state's <em>parts</em>, and
    /// a field missing from <c>GameState.Equals</c> defeats the invariant just as completely as
    /// one missing from a permanent's.
    /// <para>
    /// It was not covered because the check could not vary the field types this record is made
    /// of - thirty-five properties across immutable lists, sets and dictionaries of a dozen
    /// element types, four nested records and a nullable choice. Two live bugs came out of that
    /// gap, one per round: <c>Delayed</c> and then <c>ArmedStateTriggers</c>, each found by
    /// somebody noticing rather than by anything failing, and each hiding the same way - two
    /// states differing only in that field compared <em>equal</em>, so the invariant passed
    /// straight through the divergence. Varying is now built rather than listed, which is what
    /// made covering this type possible at all.
    /// </para>
    /// <para>
    /// Played on rather than started fresh: an empty game leaves most of these fields at their
    /// defaults, and a check that varies a default is a weaker check than one that varies a real
    /// position. A card is drawn and a permanent put onto the battlefield first, so the objects,
    /// the zones and the timestamps all have something in them.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_field_of_a_state_is_part_of_its_identity()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        game.Draw(alice);
        game.Move(game.State.GetPlayer(alice).Library[0], Zone.Battlefield, MoveCause.Play);

        AssertEveryFieldCounts(game.State);
    }

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
    /// <remarks>
    /// The collections and the records are <em>built</em> rather than listed, and that is the
    /// whole difference between this check and the one it replaced. A hand-written arm per
    /// element type is the same shape of list this file exists to police: it goes stale by
    /// omission, and an omission here is silent twice over - the property is reported
    /// "unvaried", somebody adds it to a tolerated list, and the field it was guarding stops
    /// being checked. <see cref="GameState"/> has thirty-five of them, of twenty-odd types, and
    /// writing an arm each is why it had none at all.
    /// </remarks>
    private static object? Vary(object? current, Type type, int depth = 0)
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

        // An immutable collection is varied by holding one more of whatever it holds. Asked of
        // the type rather than looked up in a list of the element types seen so far, so a field
        // whose element type nothing has used before is varied on the day it is added.
        if (bare.IsGenericType)
        {
            var open = bare.GetGenericTypeDefinition();
            var of = bare.GetGenericArguments();

            if (open == typeof(ImmutableList<>) || open == typeof(ImmutableHashSet<>))
            {
                return Sample(of[0], depth) is not { } one
                    ? null
                    : bare.GetMethod("Add", [of[0]])?.Invoke(current ?? Empty(bare), [one]);
            }

            if (open == typeof(ImmutableDictionary<,>))
            {
                return Sample(of[0], depth) is not { } key
                    || Sample(of[1], depth) is not { } value
                    ? null
                    : bare.GetMethod("SetItem", [of[0], of[1]])?
                        .Invoke(current ?? Empty(bare), [key, value]);
            }
        }

        // The records whose absence is itself a state: a permanent that is not on the
        // battlefield, an ability that is not on the stack, an attack a spell has not joined.
        // For these "different" means present where it was absent, and a second instance of one
        // that is already there says nothing.
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

        if (depth > 3)
            return null;

        // A record with nothing in the field yet: one of it differs from none of it.
        if (current is null)
            return Sample(bare, depth);

        // A record already there, varied by one of its own fields rather than by a fresh
        // instance - a fresh one of a record whose every field has a default compares equal to
        // the one in place, and this check reads "equal" as "the field is missing from Equals",
        // which would accuse the state of a defect it does not have.
        return VaryWithin(current, bare, depth);
    }

    /// <summary>The same record with one of its own fields changed, or null if none can be.</summary>
    private static object? VaryWithin(object current, Type type, int depth)
    {
        var clone = type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public);
        if (clone is null)
            return null;

        foreach (var property in type.GetProperties())
        {
            if (property.SetMethod is null || property.GetIndexParameters().Length > 0)
                continue;

            var copy = clone.Invoke(current, null)!;

            if (Vary(property.GetValue(copy), property.PropertyType, depth + 1) is not { } inner)
                continue;

            property.SetValue(copy, inner);

            // Proved different rather than assumed: a field the inner record leaves out of its
            // own Equals hands back something that compares equal, and the caller would read
            // that as the outer state being at fault.
            if (!current.Equals(copy))
                return copy;
        }

        return null;
    }

    /// <summary>The empty instance of an immutable collection type.</summary>
    private static object? Empty(Type type) =>
        type.GetField("Empty", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);

    /// <summary>One value of a type, for putting inside a collection that had none.</summary>
    /// <remarks>
    /// It only has to exist. Every collection this fills is compared by <c>Structural</c>, which
    /// checks the counts before it compares any element, so a collection that has gained one is
    /// already unequal and the sample itself is never looked at.
    /// </remarks>
    private static object? Sample(Type type, int depth)
    {
        var bare = Nullable.GetUnderlyingType(type) ?? type;

        if (bare == typeof(bool))
            return true;

        if (bare == typeof(int))
            return 1;

        if (bare == typeof(long))
            return 1L;

        if (bare == typeof(Guid))
            return Guid.NewGuid();

        if (bare == typeof(ObjectId))
            return ObjectId.New();

        if (bare == typeof(string))
            return "sample";

        if (bare.IsEnum)
            return Enum.GetValues(bare).Cast<object>().First();

        if (bare == typeof(CardDefinition))
            return TestCards.Creature("Identity Sample");

        if (Empty(bare) is { } empty)
            return empty;

        if (depth > 3 || bare.IsAbstract || bare.IsInterface)
            return null;

        var constructor = bare.GetConstructors()
            .OrderBy(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (constructor is null)
            return bare.IsValueType ? Activator.CreateInstance(bare) : null;

        var wanted = constructor.GetParameters();
        var arguments = new object?[wanted.Length];

        for (var i = 0; i < arguments.Length; i++)
        {
            arguments[i] = Sample(wanted[i].ParameterType, depth + 1);

            if (arguments[i] is null && wanted[i].ParameterType.IsValueType)
                return null;
        }

        return constructor.Invoke(arguments);
    }
}
