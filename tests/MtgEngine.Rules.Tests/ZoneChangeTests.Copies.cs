using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A copy made where the original stands, CR 707.12: the half of "you may cast a copy of that
/// card" that only the zone system can do. The state-based action that makes such a copy cease to
/// exist outside the stack and the battlefield (CR 704.5e) belongs to the rules system, so every
/// test here asserts the <em>creation</em> and nothing about what happens to the copy afterwards.
/// </summary>
public sealed partial class ZoneChangeTests
{
    [Fact]
    public void A_copy_in_place_has_the_originals_printed_characteristics_and_none_of_its_counters()
    {
        // CR 707.2: the copiable values are the printed values; counters, damage, tapped status and
        // attachments were never part of what is copied, so the copy arrives without them.
        var state = Zones.Create(Setup(), Store).State;
        var owner = new PlayerId(0);
        var card = InHand(state, owner, Bear);
        var battlefield = Zones.Move(state, card, ZoneKey.Shared(Zone.Battlefield)).State;
        var permanent = battlefield.In(ZoneKey.Shared(Zone.Battlefield))[0];
        var loaded = battlefield with
        {
            Objects = battlefield.Objects.SetItem(permanent, battlefield.Objects[permanent] with
            {
                Damage = 3,
                Counters = ImmutableDictionary<string, int>.Empty.SetItem("+1/+1", 2),
                AttachedTo = new ObjectId(999),
                Tapped = true,
            }),
        };

        var outcome = Zones.CopyInPlace(loaded, permanent, owner);
        var copy = outcome.State.Objects[((ObjectCreated)outcome.Events[^1]).Object.Id];

        Assert.Equal(loaded.Objects[permanent].Definition, copy.Definition);
        Assert.Equal(loaded.Objects[permanent].FaceIndex, copy.FaceIndex);
        Assert.Equal(Bear, copy.Definition.OracleId);
        Assert.Empty(copy.Counters);
        Assert.Equal(0, copy.Damage);
        Assert.False(copy.Tapped);
        Assert.Null(copy.AttachedTo);
        Assert.Equal(owner, copy.Owner);
        Assert.Equal(owner, copy.InitialController);
        AssertLogFolds(loaded, outcome);
    }

    [Fact]
    public void A_copy_in_place_is_a_copy_of_a_card_and_not_a_token()
    {
        // CR 704.5e reads the mark; CR 704.5d would remove a token from every zone but the
        // battlefield, which is not what a copy of a card in a graveyard does.
        var state = Zones.Create(Setup(), Store).State;
        var owner = new PlayerId(0);
        var card = InHand(state, owner, Bear);

        var outcome = Zones.CopyInPlace(state, card, owner);
        var copy = outcome.State.Objects[((ObjectCreated)outcome.Events[^1]).Object.Id];

        Assert.False(copy.IsToken);
        Assert.Contains(ZoneSystem.CopyOfCard, copy.Designations);
        Assert.Null(copy.Stack);
    }

    [Fact]
    public void A_copy_in_place_sits_beside_the_original_in_the_same_zone()
    {
        // CR 707.12: created in the same zone the object is in, next to what it copies rather than
        // on top of the zone.
        var state = Zones.Create(Setup(), Store).State;
        var owner = new PlayerId(0);
        var hand = ZoneKey.Of(Zone.Hand, owner);
        var original = state.In(hand)[2];

        var outcome = Zones.CopyInPlace(state, original, owner);
        var copy = ((ObjectCreated)outcome.Events[^1]).Object;
        var after = outcome.State.In(hand);

        Assert.Equal(hand, copy.Zone);
        Assert.Equal(state.In(hand).Count + 1, after.Count);
        Assert.Equal(2, after.IndexOf(original));
        Assert.Equal(3, after.IndexOf(copy.Id));
    }

    [Fact]
    public void The_outcome_of_a_copy_in_place_ends_with_the_copys_creation()
    {
        // The interpreter takes the last ObjectCreated of the outcome as the copy it casts
        // (seam-requests/E9i.md §4), so the copy's creation is the last event or the may-cast
        // announces the wrong object.
        var state = Zones.Create(Setup(), Store).State;
        var owner = new PlayerId(0);
        var card = InHand(state, owner, Bear);

        var outcome = Zones.CopyInPlace(state, card, owner);

        var created = Assert.IsType<ObjectCreated>(outcome.Events[^1]);
        Assert.Equal(created.Object.Id, outcome.Events.OfType<ObjectCreated>().Last().Object.Id);
        Assert.True(outcome.State.Objects.ContainsKey(created.Object.Id));
        AssertLogFolds(state, outcome);
    }

    [Fact]
    public void A_copy_in_place_leaves_the_original_exactly_as_it_was()
    {
        // A copy is a second object, CR 707.12; nothing about the first one changes, including its
        // id, its timestamp and its place in the zone.
        var state = Zones.Create(Setup(), Store).State;
        var owner = new PlayerId(0);
        var hand = ZoneKey.Of(Zone.Hand, owner);
        var original = state.In(hand)[0];

        var outcome = Zones.CopyInPlace(state, original, owner);
        var copy = ((ObjectCreated)outcome.Events[^1]).Object;

        Assert.Equal(state.Objects[original], outcome.State.Objects[original]);
        Assert.NotEqual(original, copy.Id);
        Assert.True(copy.Timestamp > state.Objects[original].Timestamp);
        Assert.Equal(0, outcome.State.In(hand).IndexOf(original));
    }

    [Fact]
    public void A_copy_of_a_card_in_a_graveyard_is_created_in_that_graveyard()
    {
        // CR 707.12 creates it where the card is. CR 704.5e then makes it cease to exist, because a
        // graveyard is neither the stack nor the battlefield — that state-based action is the rules
        // system's (E5f), so only the creation is asserted here.
        var state = Zones.Create(Setup(), Store).State;
        var owner = new PlayerId(0);
        var graveyard = ZoneKey.Of(Zone.Graveyard, owner);
        var buried = Zones.Move(state, InHand(state, owner, Bolt), graveyard).State;
        var card = buried.In(graveyard)[0];

        var outcome = Zones.CopyInPlace(buried, card, owner);
        var copy = ((ObjectCreated)outcome.Events[^1]).Object;

        Assert.Equal(graveyard, copy.Zone);
        Assert.Contains(copy.Id, outcome.State.In(graveyard));
        Assert.Contains(ZoneSystem.CopyOfCard, copy.Designations);
        Assert.False(copy.IsToken);
        AssertLogFolds(buried, outcome);
    }

    [Fact]
    public void A_copy_of_an_object_that_is_not_there_changes_nothing()
    {
        var state = Zones.Create(Setup(), Store).State;

        var outcome = Zones.CopyInPlace(state, new ObjectId(9999), new PlayerId(0));

        Assert.Empty(outcome.Events);
        Assert.Same(state, outcome.State);
    }
}
