using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// What happens when an object changes zones (CR 400).
/// </summary>
public sealed class ZoneChangeTests
{
    [Fact]
    public void An_object_that_changes_zones_becomes_a_new_object()
    {
        // CR 400.7. The whole point: nothing that held the old id can find it afterwards, so an
        // aura cannot reattach to the creature that died and came back.
        var (game, alice, _) = TestCards.TwoPlayer();
        var wasInLibrary = game.State.GetPlayer(alice).Library[0];

        var nowInHand = game.Move(wasInLibrary, Zone.Hand, MoveCause.Draw);

        Assert.NotEqual(wasInLibrary, nowInHand);
        Assert.False(game.State.TryGetObject(wasInLibrary, out _));
        Assert.Equal(Zone.Hand, game.State.GetObject(nowInHand).Zone);
    }

    [Fact]
    public void The_card_survives_the_change_of_identity()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        var id = game.State.GetPlayer(alice).Library[0];
        var name = game.State.GetObject(id).Card.Name;

        var moved = game.Move(id, Zone.Hand, MoveCause.Draw);

        Assert.Equal(name, game.State.GetObject(moved).Card.Name);
    }

    [Fact]
    public void An_object_goes_to_its_owners_zone_not_its_controllers()
    {
        // CR 400.3. Bob's card that Alice somehow controls still returns to Bob's graveyard.
        var (game, alice, bob) = TestCards.TwoPlayer();
        var bobsCard = game.State.GetPlayer(bob).Library[0];

        var onBattlefield = game.Move(bobsCard, Zone.Battlefield, controllerId: alice);
        Assert.Equal(alice, game.State.GetObject(onBattlefield).ControllerId);

        var dead = game.Move(onBattlefield, Zone.Graveyard, MoveCause.Destroy);

        Assert.Contains(dead, game.State.GetPlayer(bob).Graveyard);
        Assert.Empty(game.State.GetPlayer(alice).Graveyard);
        // CR 108.4: in a zone that belongs to a player, the object is that player's.
        Assert.Equal(bob, game.State.GetObject(dead).ControllerId);
    }

    [Fact]
    public void Ownership_never_changes()
    {
        // CR 108.3.
        var (game, alice, bob) = TestCards.TwoPlayer();
        var bobsCard = game.State.GetPlayer(bob).Library[0];

        var stolen = game.Move(bobsCard, Zone.Battlefield, controllerId: alice);

        Assert.Equal(bob, game.State.GetObject(stolen).OwnerId);
        Assert.Equal(alice, game.State.GetObject(stolen).ControllerId);
    }

    [Fact]
    public void Exiling_something_already_exiled_still_makes_a_new_object()
    {
        // CR 400.8. It does not change zones, but it is new, so anything watching for "exiled"
        // sees it and anything holding the old id does not.
        var (game, alice, _) = TestCards.TwoPlayer();
        var exiled = game.Move(game.State.GetPlayer(alice).Library[0], Zone.Exile, MoveCause.Exile);

        var exiledAgain = game.Move(exiled, Zone.Exile, MoveCause.Exile);

        Assert.NotEqual(exiled, exiledAgain);
        Assert.Single(game.State.Exile);
        Assert.Equal(exiledAgain, game.State.Exile[0]);
    }

    [Fact]
    public void A_permanent_is_only_a_permanent_on_the_battlefield()
    {
        // CR 403.3. Status is not carried out of the zone it belonged to: a creature that dies
        // tapped is not a tapped card in the graveyard.
        var (game, alice, _) = TestCards.TwoPlayer();
        var card = game.State.GetPlayer(alice).Library[0];

        var permanent = game.Move(card, Zone.Battlefield, MoveCause.Play);
        Assert.NotNull(game.State.GetObject(permanent).Permanent);

        var dead = game.Move(permanent, Zone.Graveyard, MoveCause.Destroy);
        Assert.Null(game.State.GetObject(dead).Permanent);
    }

    [Fact]
    public void A_permanent_enters_untapped_and_summoning_sick()
    {
        // CR 302.6 — it has not been controlled since its controller's turn began.
        var (game, alice, _) = TestCards.TwoPlayer();

        var permanent = game.Move(
            game.State.GetPlayer(alice).Library[0], Zone.Battlefield, MoveCause.Play);

        var status = game.State.GetObject(permanent).Permanent!;
        Assert.False(status.IsTapped);
        Assert.True(status.HasSummoningSickness);
        Assert.Equal(0, status.DamageMarked);
    }

    [Fact]
    public void A_card_put_into_a_graveyard_goes_on_top()
    {
        // CR 404.1. Order matters there, and "top" has to mean one thing across the engine.
        var (game, alice, _) = TestCards.TwoPlayer();
        var library = game.State.GetPlayer(alice).Library;

        var first = game.Move(library[0], Zone.Graveyard, MoveCause.Mill);
        var second = game.Move(library[1], Zone.Graveyard, MoveCause.Mill);

        Assert.Equal([second, first], game.State.GetPlayer(alice).Graveyard);
    }

    [Fact]
    public void A_card_can_be_put_on_the_bottom_of_a_library()
    {
        var (game, alice, _) = TestCards.TwoPlayer();
        var top = game.State.GetPlayer(alice).Library[0];

        var bottomed = game.Move(top, Zone.Library, MoveCause.Other, position: ZonePosition.Bottom);

        var library = game.State.GetPlayer(alice).Library;
        Assert.Equal(bottomed, library[^1]);
        Assert.NotEqual(bottomed, library[0]);
    }

    [Fact]
    public void Every_new_object_gets_a_later_timestamp_than_the_one_before()
    {
        // CR 613.7. Continuous effects apply in timestamp order, so the order has to be total.
        var (game, alice, _) = TestCards.TwoPlayer();
        var library = game.State.GetPlayer(alice).Library;

        var first = game.Move(library[0], Zone.Battlefield, MoveCause.Play);
        var second = game.Move(library[1], Zone.Battlefield, MoveCause.Play);

        Assert.True(game.State.GetObject(second).Timestamp > game.State.GetObject(first).Timestamp);
    }
}
