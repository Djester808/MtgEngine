using System.Text.Json;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Tests;

[Trait("Scope", "Permanent")]
public sealed class PermanentContractTests
{
    private static readonly ZoneSystem Zones = new();
    private static readonly PlayerId Owner = new(0);
    private static readonly PlayerId Controller = new(1);
    private static readonly ZoneKey Battlefield = ZoneKey.Shared(Zone.Battlefield);
    private static readonly CardDefinition Definition = new("card", "Front", Layout.Normal,
        [new Face("Front", FaceRole.Main, new TypeLine(CardType.Creature), OracleText: "Descriptive text.")]);

    [Fact]
    public void Lookup_returns_the_authoritative_object_with_separate_owner_and_controller()
    {
        var entered = Enter();
        var obj = Assert.Single(entered.State.Objects.Values);
        Assert.Same(obj, PermanentAccess.Require(entered.State, obj.Id));
        Assert.Equal(Owner, obj.Owner);
        Assert.Equal(Controller, PermanentAccess.Evaluate(entered.State, obj.Id, new LayerSystem()).Controller);
        Assert.Equal(new CardInstanceId(1), obj.CardInstanceId);
        Assert.Equal("printing", obj.DeckSelection!.PrintingId);
        Assert.Equal(CardFinish.Etched, obj.DeckSelection.Finish);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Missing_stale_and_inconsistent_membership_is_rejected(int corruption)
    {
        var state = Enter().State;
        var obj = Assert.Single(state.Objects.Values);
        state = corruption switch
        {
            0 => state with { Objects = state.Objects.Remove(obj.Id) },
            1 => state with { Zones = state.Zones.SetItem(Battlefield, []) },
            2 => state with { Objects = state.Objects.SetItem(obj.Id, obj with { Zone = ZoneKey.Of(Zone.Hand, Owner) }) },
            _ => state with { Objects = state.Objects.SetItem(obj.Id, obj with { Id = new ObjectId(999) }) },
        };
        Assert.Null(PermanentAccess.Find(state, obj.Id));
        Assert.Throws<InvalidOperationException>(() => PermanentAccess.Require(state, obj.Id));
        Assert.Throws<InvalidOperationException>(() => PermanentAccess.Evaluate(state, obj.Id, new LayerSystem()));
    }

    [Fact]
    public void Evaluation_uses_current_control_without_rewriting_stored_ownership_or_initial_control()
    {
        var state = Enter().State;
        var obj = Assert.Single(state.Objects.Values);
        state = state with
        {
            ActiveEffects = [new ActiveEffect(1, new ControlBy(new You()), null, Owner,
                [obj.Id], [], new UntilEndOfTurn(), 10, false)],
        };
        Assert.Equal(Owner, PermanentAccess.Evaluate(state, obj.Id, new LayerSystem()).Controller);
        Assert.Equal(Controller, PermanentAccess.Require(state, obj.Id).InitialController);
        Assert.Equal(Owner, PermanentAccess.Require(state, obj.Id).Owner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tokens_and_legacy_cards_do_not_require_a_physical_card_identity(bool token)
    {
        var state = Enter().State;
        var obj = Assert.Single(state.Objects.Values) with { IsToken = token, CardInstanceId = null, DeckSelection = null };
        state = state with { Objects = state.Objects.SetItem(obj.Id, obj) };
        Assert.Same(obj, PermanentAccess.Require(state, obj.Id));
        Assert.Equal(token, PermanentAccess.Require(state, obj.Id).IsToken);
    }

    [Fact]
    public void Stored_status_survives_existing_events_and_replay_without_changing_identity()
    {
        var entered = Enter();
        var id = Assert.Single(entered.State.Objects.Keys);
        var events = entered.Events.Add(new ObjectChanged(id, Tapped: true, FaceDown: true,
            PhasedOut: true, Damage: 3, AttachedToPlayer: Controller)).Add(new CountersChanged(id, "charge", 2));
        var state = GameReducer.Replay(EventLogSerializer.Read(EventLogSerializer.Write(events)));
        var obj = PermanentAccess.Require(state, id);
        Assert.True(obj.Tapped);
        Assert.True(obj.FaceDown);
        Assert.True(obj.PhasedOut);
        Assert.Equal(3, obj.Damage);
        Assert.Equal(2, obj.Counters["charge"]);
        Assert.Equal(Controller, obj.AttachedToPlayer);
        Assert.Null(obj.AttachedTo);
        Assert.Equal(new CardInstanceId(1), obj.CardInstanceId);
        Assert.False(PermanentAccess.Require(entered.State, id).Tapped);
    }

    [Fact]
    public void Leaving_and_returning_invalidates_old_object_identity_but_preserves_selection()
    {
        var entered = Enter();
        var old = Assert.Single(entered.State.Objects.Values);
        var left = Zones.Move(entered.State, old.Id, ZoneKey.Of(Zone.Graveyard, Owner));
        Assert.Null(PermanentAccess.Find(left.State, old.Id));
        var card = Assert.Single(left.State.Objects.Values);
        Assert.Null(PermanentAccess.Find(left.State, card.Id));
        var returned = Zones.Move(left.State, card.Id, Battlefield);
        var current = Assert.Single(returned.State.Objects.Values);
        Assert.NotEqual(old.Id, current.Id);
        Assert.Same(current, PermanentAccess.Require(returned.State, current.Id));
        Assert.Equal(old.CardInstanceId, current.CardInstanceId);
        Assert.Equal(old.DeckSelection, current.DeckSelection);
    }

    [Fact]
    public void Face_down_evaluation_and_projection_do_not_disclose_stored_printing_or_text()
    {
        var entered = Enter();
        var id = Assert.Single(entered.State.Objects.Keys);
        var state = new ObjectChanged(id, FaceDown: true).Apply(entered.State);
        var evaluated = PermanentAccess.Evaluate(state, id, new LayerSystem());
        Assert.Equal(2, evaluated.Power);
        Assert.Equal(2, evaluated.Toughness);
        var view = new ViewProjector(new LayerSystem(), _ => true).Project(state, Owner);
        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain("printing", json);
        Assert.DoesNotContain("Descriptive text.", json);
        Assert.DoesNotContain("CardInstanceId", json);
    }

    private static Outcome Enter()
    {
        var start = Zones.Create(new GameSetup(
            [new PlayerStart("A", [new StartingCard("card", "printing", CardFinish.Etched)], []),
             new PlayerStart("B", [], [])], 1, OpeningHandSize: 0), new Store());
        var entered = Zones.Move(start.State, new ObjectId(1), Battlefield, controller: Controller);
        return entered with { Events = start.Events.AddRange(entered.Events) };
    }

    private sealed class Store : IDefinitionStore
    {
        public CardDefinition? ByOracleId(string oracleId) => Definition;
        public CardDefinition? ByName(string name) => Definition;
    }
}
