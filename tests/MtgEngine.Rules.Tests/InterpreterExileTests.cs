using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// CR 701.13's three riders, none of which the interpreter read.
///
/// <c>FaceDown</c> is implemented: "exile the top card of your library face down" (Necropotence)
/// exiled it face UP, so every player could read a card nobody is allowed to see.
///
/// <c>Until</c> is refused rather than performed wrongly: it had no return to end it, so "exile
/// until this leaves the battlefield" exiled the card for EVER, and no card in the corpus
/// writes it, so refusing costs nothing that works today.
///
/// <c>ExiledWith</c> has no reader either, and is NOT refused. A refusal is not a quiet gap at
/// the front door: <c>Game.Apply</c> turns it into a Refused answer and discards the whole
/// command, so The Darkness Crystal's "instead exile it and you gain 2 life" would neither exile
/// nor gain, and the creature would just die. The exile is right; only the link back is missing,
/// and it is owed with the remembered key that has to outlive one resolution beside it
/// (seam-requests/EINT7.md).
/// </summary>
public sealed class InterpreterExileTests
{
    [Fact]
    [RuleRef("701.13c")]
    public void A_card_exiled_face_down_is_face_down()
    {
        var table = new InterpreterTable();
        var card = table.Permanent(table.A);

        var events = table.Run(new Exile(new Remembered("it"), FaceDown: true), table.Context() with
        {
            Remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem("it", [card]),
        });

        var moved = Assert.Single(events.OfType<ObjectMoved>());
        Assert.Equal(Zone.Exile, moved.To.Zone.Zone);
        Assert.True(moved.To.FaceDown);
        Assert.DoesNotContain(events, e => e is ObjectChanged { FaceDown: true });
    }

    [Fact]
    [RuleRef("701.13")]
    public void A_card_exiled_face_up_stays_face_up()
    {
        var table = new InterpreterTable();
        var card = table.Permanent(table.A);

        var events = table.Run(new Exile(new Remembered("it")), table.Context() with
        {
            Remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem("it", [card]),
        });

        Assert.False(Assert.Single(events.OfType<ObjectMoved>()).To.FaceDown);
        Assert.DoesNotContain(events, e => e is ObjectChanged { FaceDown: true });
    }

    [Fact]
    [RuleRef("701.13")]
    public void An_exile_with_a_duration_is_refused_rather_than_made_permanent()
    {
        var table = new InterpreterTable();
        var card = table.Permanent(table.A);

        var refused = Assert.Throws<RefusedException>(() =>
            table.Run(new Exile(new Remembered("it"), Until: new WhileSourceOnBattlefield()), table.Context() with
            {
                Remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem("it", [card]),
            }));

        Assert.Equal("701.13", refused.Rule);
    }

    /// <summary>
    /// CR 701.13: the exile still happens. The link back is not recorded and nothing reads it,
    /// but refusing the exile would take the card's working half with it — the creature would not
    /// be exiled at all, and at the front door the command that killed it would be rejected.
    /// </summary>
    [Fact]
    [RuleRef("701.13")]
    public void An_exile_linked_to_a_source_still_exiles_the_card()
    {
        var table = new InterpreterTable();
        var card = table.Permanent(table.A);

        var events = table.Run(new Exile(new Remembered("it"), ExiledWith: new Self()), table.Context() with
        {
            Remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem("it", [card]),
        });

        Assert.Contains(events, e => e is ObjectMoved { To.Zone.Zone: Zone.Exile });
    }
}
