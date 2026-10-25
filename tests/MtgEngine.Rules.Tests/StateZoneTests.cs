using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

public sealed class StateZoneTests
{
    private static readonly PlayerId Owner = new(0);
    private static readonly PlayerId Other = new(1);
    private static readonly CardDefinition Definition = new("secret-oracle", "Secret card", Layout.Normal,
        [new Face("Secret card", FaceRole.Main, new TypeLine(CardType.Creature), OracleText: "secret text")]);

    private static GameState Empty(int seats = 2) => GameSetup.Create(
        Enumerable.Range(0, seats).Select(i => PlayerState.Create(new(i), "Player", 20)));

    private static GameState Cards(int count = 1)
    {
        var state = Empty();
        for (var i = 0; i < count; i++)
            state = GameSetup.AddCard(state, Owner, Definition, new("secret-oracle", "secret-printing", CardFinish.Etched));
        return state;
    }

    private static ZoneMoveResult Move(GameState state, ObjectId id, Zone zone, ZonePlacement placement = ZonePlacement.Default,
        PlayerId? controller = null, bool faceDown = false) => ZoneTransitions.Move(state,
        new(id, ZoneTopology.IsPersonal(zone) ? ZoneKey.Of(zone, Owner) : ZoneKey.Shared(zone), placement, controller, faceDown));

    private static ZoneView View(GameState state, PlayerId viewer, Zone zone, PlayerId? owner = null) =>
        ZoneStateView.For(state, viewer).Zones.Single(z => z.Key == new ZoneKey(zone, owner));

    [Theory]
    [InlineData(2, 10)]
    [InlineData(3, 13)]
    [InlineData(4, 16)]
    public void Setup_materializes_all_personal_and_shared_zones(int seats, int zones)
    {
        var state = Empty(seats);
        Assert.Equal(zones, state.Zones.Count);
        Assert.All(state.Zones.Values, members => Assert.Empty(members));
        state.Validate();
    }

    [Fact]
    public void Setup_assigns_distinct_cards_and_copies_mutable_catalog_collections()
    {
        var subtypes = new List<string> { "Human" };
        var faces = new List<Face> { Definition.Faces[0] with { Types = new(CardType.Creature, Subtypes: subtypes) } };
        var state = GameSetup.AddCard(Empty(), Owner, Definition with { Faces = faces }, "secret-oracle");
        state = GameSetup.AddCard(state, Other, Definition, "secret-oracle", commander: true);
        subtypes.Clear();
        faces.Clear();
        Assert.Equal("Human", Assert.Single(state.Objects[new(1)].Face.Types.SubtypeList));
        Assert.Equal(2, state.Objects.Values.Select(o => o.CardInstanceId).Distinct().Count());
        Assert.True(state.Objects[new(2)].IsCommander);
        Assert.Equal(Other, state.Objects[new(2)].Owner);
        Assert.Single(state.In(ZoneKey.Shared(Zone.Command)));
    }

    [Fact]
    public void Movement_recreates_object_but_preserves_card_printing_owner_and_commander()
    {
        var state = GameSetup.AddCard(Empty(), Owner, Definition, new("secret-oracle", "printing"), commander: true);
        var original = state.Objects[new(1)];
        var field = Move(state, original.Id, Zone.Battlefield, controller: Other);
        var changed = field.State.Objects[field.Current] with
        {
            Tapped = true,
            Damage = 3,
            PhasedOut = true,
            Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 2),
            Chosen = ImmutableDictionary<string, string>.Empty.Add("color", "Blue"),
            Designations = ["monstrous"],
            AttachedToPlayer = Other,
            DealtDeathtouchDamage = true,
        };
        var dirty = field.State with { Objects = field.State.Objects.SetItem(changed.Id, changed) };
        var grave = ZoneTransitions.Move(dirty, new(changed.Id, ZoneKey.Of(Zone.Graveyard, Other)));
        var card = grave.State.Objects[grave.Current];
        Assert.Equal(ZoneKey.Of(Zone.Graveyard, Owner), card.Zone);
        Assert.Equal(original.CardInstanceId, card.CardInstanceId);
        Assert.Equal(original.DeckSelection, card.DeckSelection);
        Assert.True(card.IsCommander);
        Assert.Equal(Owner, card.Owner);
        Assert.False(card.Tapped || card.PhasedOut || card.DealtDeathtouchDamage);
        Assert.Equal(0, card.Damage);
        Assert.Empty(card.Counters);
        Assert.Empty(card.Chosen);
        Assert.Empty(card.Designations);
        Assert.Null(card.AttachedToPlayer);
        Assert.False(grave.State.Objects.ContainsKey(changed.Id));
        Assert.Equal(original, state.Objects[original.Id]);
        Assert.True(card.Timestamp > changed.Timestamp);
        grave.State.Validate();
    }

    [Fact]
    public void Control_changes_keep_identity_membership_and_owner()
    {
        var field = Move(Cards(), new(1), Zone.Battlefield);
        var state = ZoneTransitions.ChangeController(field.State, field.Current, Other);
        Assert.Equal(field.State.Zones, state.Zones);
        Assert.Equal(field.State.Objects[field.Current].Timestamp, state.Objects[field.Current].Timestamp);
        Assert.Equal(Owner, state.Objects[field.Current].Owner);
        Assert.Equal(Other, state.Objects[field.Current].Controller);
        Assert.Equal(Owner, state.Objects[field.Current].InitialController);
        var hand = Move(state, field.Current, Zone.Hand);
        Assert.Null(Assert.Single(View(hand.State, Owner, Zone.Hand, Owner).Objects).Controller);
        Assert.Throws<ArgumentException>(() => ZoneTransitions.ChangeController(hand.State, hand.Current, Other));
    }

    [Fact]
    public void Stack_to_battlefield_retains_stored_controller_with_a_new_identity()
    {
        var stack = Move(Cards(), new(1), Zone.Stack, controller: Other);
        var field = Move(stack.State, stack.Current, Zone.Battlefield);
        Assert.Equal(Other, field.State.Objects[field.Current].Controller);
        Assert.NotEqual(stack.Current, field.Current);
    }

    [Theory]
    [InlineData(Zone.Library)]
    [InlineData(Zone.Graveyard)]
    [InlineData(Zone.Stack)]
    public void Ordered_zone_insertion_respects_top_and_bottom(Zone zone)
    {
        var state = Cards(3);
        var staged = new List<ObjectId>();
        foreach (var id in new[] { 1, 2, 3 })
        {
            var moved = Move(state, new(id), Zone.Exile);
            state = moved.State;
            staged.Add(moved.Current);
        }
        var first = Move(state, staged[0], zone);
        var top = Move(first.State, staged[1], zone, ZonePlacement.Top);
        var bottom = Move(top.State, staged[2], zone, ZonePlacement.Bottom);
        var key = zone == Zone.Stack ? ZoneKey.Shared(zone) : ZoneKey.Of(zone, Owner);
        var expected = zone == Zone.Stack ? new[] { bottom.Current, first.Current, top.Current }
            : new[] { top.Current, first.Current, bottom.Current };
        Assert.Equal(expected, bottom.State.In(key));
        if (zone == Zone.Library)
            Assert.Empty(View(bottom.State, Owner, zone, key.Player).Objects);
        else
            Assert.Equal(expected, View(bottom.State, Owner, zone, key.Player).Objects.Select(o => o.Id));
    }

    [Theory]
    [InlineData(Zone.Exile)]
    [InlineData(Zone.Command)]
    public void Reentry_into_exile_or_command_recreates_identity(Zone zone)
    {
        var first = Move(Cards(), new(1), zone);
        var second = Move(first.State, first.Current, zone);
        Assert.NotEqual(first.Current, second.Current);
        Assert.Single(second.State.In(ZoneKey.Shared(zone)));
        Assert.Equal(new CardInstanceId(1), second.State.Objects[second.Current].CardInstanceId);
    }

    [Fact]
    public void Invalid_requests_are_atomic_and_cannot_reuse_stale_ids()
    {
        var state = Cards();
        var moved = Move(state, new(1), Zone.Hand);
        Assert.Throws<ArgumentException>(() => Move(moved.State, new(1), Zone.Exile));
        Assert.Throws<ArgumentException>(() => Move(moved.State, moved.Current, Zone.Hand));
        Assert.Throws<ArgumentException>(() => Move(state, new(1), Zone.Exile, ZonePlacement.Top));
        Assert.Throws<ArgumentException>(() => Move(state, new(1), Zone.Hand, controller: Other));
        Assert.Throws<NotSupportedException>(() => Move(state, new(1), Zone.Battlefield, faceDown: true));
        Assert.Throws<ArgumentException>(() => ZoneTransitions.Move(state, new(new(1), ZoneKey.Shared(Zone.OutsideTheGame))));
        Assert.Throws<ArgumentException>(() => ZoneTransitions.Move(state, new(new(1), ZoneKey.Shared(Zone.Hand))));
        Assert.Single(state.Objects);
        Assert.Equal(new ObjectId(1), Assert.Single(state.In(ZoneKey.Of(Zone.Library, Owner))));
    }

    [Fact]
    public void Allocator_preserves_high_water_marks_and_fails_atomically_on_overflow()
    {
        var state = Cards() with { LastObjectId = 80, LastTimestamp = 90 };
        var moved = Move(state, new(1), Zone.Exile);
        Assert.Equal(81, moved.Current.Value);
        Assert.Equal(91, moved.State.Objects[moved.Current].Timestamp);
        Assert.Throws<OverflowException>(() => Move(state with { LastObjectId = int.MaxValue }, new(1), Zone.Hand));
        Assert.Throws<OverflowException>(() => Move(state with { LastTimestamp = int.MaxValue }, new(1), Zone.Hand));
    }

    [Theory]
    [InlineData(CardType.Instant, Zone.Library, Zone.Battlefield)]
    [InlineData(CardType.Sorcery, Zone.Library, Zone.Battlefield)]
    [InlineData(CardType.Conspiracy, Zone.Command, Zone.Hand)]
    [InlineData(CardType.Plane, Zone.Command, Zone.Exile)]
    public void Prohibited_card_type_transitions_leave_the_object_unchanged(CardType type, Zone source, Zone destination)
    {
        var definition = Definition with { Faces = [Definition.Faces[0] with { Types = new(type) }] };
        var state = GameSetup.AddCard(Empty(), Owner, definition, "secret-oracle", source == Zone.Command);
        var result = Move(state, new(1), destination);
        Assert.False(result.Changed);
        Assert.Same(state, result.State);
        Assert.Equal(result.Previous, result.Current);
    }

    [Fact]
    public void Validation_rejects_duplicate_physical_cards_wrong_owner_and_outside_game_membership()
    {
        var state = Cards(2);
        var second = state.Objects[new(2)];
        Assert.Throws<InvalidOperationException>(() => (state with
        {
            Objects = state.Objects.SetItem(second.Id, second with { CardInstanceId = new(1) }),
        }).Validate());
        Assert.Throws<InvalidOperationException>(() => (state with
        {
            Objects = state.Objects.SetItem(second.Id, second with { Owner = Other }),
        }).Validate());
        Assert.Throws<InvalidOperationException>(() => (state with
        {
            Zones = state.Zones.Add(ZoneKey.Shared(Zone.OutsideTheGame), []),
        }).Validate());
    }

    [Fact]
    public void Hidden_piles_disclose_counts_without_identifiers_or_printed_metadata()
    {
        var state = Cards(2);
        var hand = Move(state, new(1), Zone.Hand);
        var otherView = ZoneStateView.For(hand.State, Other);
        var json = JsonSerializer.Serialize(otherView);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CardInstanceId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Timestamp", json, StringComparison.Ordinal);
        Assert.Empty(View(hand.State, Other, Zone.Hand, Owner).Objects);
        Assert.Equal(1, View(hand.State, Other, Zone.Hand, Owner).Count);
        Assert.Empty(View(hand.State, Owner, Zone.Library, Owner).Objects);
        Assert.Equal("secret-printing", Assert.Single(View(hand.State, Owner, Zone.Hand, Owner).Objects).Printed!.PrintingId);
        Assert.Throws<ArgumentOutOfRangeException>(() => ZoneStateView.For(state, new(2)));
    }

    [Fact]
    public void Exile_inspection_is_explicit_per_viewer_and_expires_on_reentry()
    {
        var exile = Move(Cards(), new(1), Zone.Exile, faceDown: true);
        Assert.Null(Assert.Single(View(exile.State, Owner, Zone.Exile).Objects).Printed);
        var allowed = ZoneTransitions.AllowExileLook(exile.State, exile.Current, Other);
        Assert.NotNull(Assert.Single(View(allowed, Other, Zone.Exile).Objects).Printed);
        Assert.Null(Assert.Single(View(allowed, Owner, Zone.Exile).Objects).Printed);
        var reentry = Move(allowed, exile.Current, Zone.Exile, faceDown: true);
        Assert.Empty(reentry.State.ExileLookPermissions);
        Assert.Null(Assert.Single(View(reentry.State, Other, Zone.Exile).Objects).Printed);
        var grave = Move(reentry.State, reentry.Current, Zone.Graveyard);
        Assert.False(grave.State.Objects[grave.Current].FaceDown);
        Assert.NotNull(Assert.Single(View(grave.State, Other, Zone.Graveyard, Owner).Objects).Printed);
    }

    [Fact]
    public void Legacy_disclosure_and_pending_payloads_cannot_bypass_projection()
    {
        var state = Cards() with
        {
            LibraryInspections = ImmutableDictionary<string, LibraryInspection>.Empty.Add("stale", new(Other, Owner, [new(1)]) { PublicReveal = true }),
            Pending = new(new(1), Owner, "secret choice", "secret prompt", [new("secret", "secret label", new(1))], 0, 1),
            LastReveal = new(Owner, [new("secret-oracle", "Secret card", null, null, "secret-printing")]),
        };
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(ZoneStateView.For(state, Other)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Attachments_do_not_follow_a_card_to_its_new_object()
    {
        var first = Move(Cards(2), new(1), Zone.Battlefield);
        var second = Move(first.State, new(2), Zone.Battlefield);
        var state = second.State with
        {
            Objects = second.State.Objects.SetItem(second.Current,
                second.State.Objects[second.Current] with { AttachedTo = first.Current }),
        };
        var moved = Move(state, first.Current, Zone.Hand);
        Assert.Null(moved.State.Objects[second.Current].AttachedTo);
        Assert.Equal(Zone.Battlefield, moved.State.Objects[second.Current].Zone.Zone);
        Assert.Equal(first.Current, state.Objects[second.Current].AttachedTo);
    }

    [Fact]
    public void Face_down_battlefield_details_follow_current_control_not_ownership()
    {
        var field = Move(Cards(), new(1), Zone.Battlefield, controller: Other);
        var state = field.State with
        {
            Objects = field.State.Objects.SetItem(field.Current, field.State.Objects[field.Current] with { FaceDown = true }),
        };
        Assert.Null(Assert.Single(View(state, Owner, Zone.Battlefield).Objects).Printed);
        Assert.NotNull(Assert.Single(View(state, Other, Zone.Battlefield).Objects).Printed);
        var changed = ZoneTransitions.ChangeController(state, field.Current, Owner);
        Assert.NotNull(Assert.Single(View(changed, Owner, Zone.Battlefield).Objects).Printed);
        Assert.Null(Assert.Single(View(changed, Other, Zone.Battlefield).Objects).Printed);
    }

    [Fact]
    public void Nonphysical_objects_require_a_future_lifecycle_instead_of_basic_movement()
    {
        var state = Cards();
        var token = state.Objects[new(1)] with { CardInstanceId = null, DeckSelection = null, IsToken = true };
        state = state with { Objects = state.Objects.SetItem(token.Id, token) };
        Assert.Throws<NotSupportedException>(() => Move(state, token.Id, Zone.Exile));
    }

    [Fact]
    public void Repeated_moves_preserve_invariants_and_physical_identity_without_reusing_objects()
    {
        var state = Cards(4);
        var ids = state.Objects.Keys.ToHashSet();
        var physical = state.Objects.Values.Select(o => o.CardInstanceId).ToHashSet();
        var destinations = new[] { Zone.Hand, Zone.Battlefield, Zone.Graveyard, Zone.Exile, Zone.Command, Zone.Library };
        for (var round = 0; round < 12; round++)
        {
            foreach (var card in state.Objects.Values.ToArray())
            {
                var move = Move(state, card.Id, destinations[round % destinations.Length]);
                state = move.State;
                Assert.True(ids.Add(move.Current));
                state.Validate();
                Assert.Equal(4, state.Zones.Values.Sum(z => z.Count));
                Assert.True(physical.SetEquals(state.Objects.Values.Select(o => o.CardInstanceId)));
            }
        }
    }
}
