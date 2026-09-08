using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Triggers;

// The vocabulary's trigger and the log's event are both spelled DamagePrevented — the card asks for
// the happening, the log records it — so a file that imports both namespaces has to say which it
// means. The alias is that, and it is the one line any other reader of both needs.
using DamagePrevented = MtgEngine.Rules.Events.DamagePrevented;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 28's ask of the log: the record that a prevention effect was applied (CR 615.13), which is
/// the one happening the log could not say — the damage it prevented is missing from the events it
/// leaves behind, so a trigger on it had nothing to match.
/// </summary>
public sealed partial class EventLogTests
{
    private static GameStarted Wave28Start() =>
        new([new PlayerStart("A", [], []), new PlayerStart("B", [], [])], Seed: 11, StartingLife: 20, Format: "test");

    /// <summary>
    /// CR 615.13 with 615.6: a stored game keeps both facts a prevention trigger asks about — that a
    /// prevention was applied and how much it prevented — for damage that would have been dealt to a
    /// permanent and for damage that would have been dealt to a player, because the trigger's
    /// recipient half reads whichever of the two the event carries.
    /// </summary>
    [Fact]
    [RuleRef("615.13")]
    public void A_prevention_round_trips_both_recipients()
    {
        var toObject = new DamagePrevented(new ObjectId(4), new ObjectId(7), null, 2, new ObjectId(9));
        var toPlayer = new DamagePrevented(new ObjectId(4), null, new PlayerId(1), 5, null);

        var json = EventLogSerializer.Write([Wave28Start(), toObject, toPlayer]);
        var back = EventLogSerializer.Read(json);

        Assert.Contains("DamagePrevented", json, StringComparison.Ordinal);
        Assert.Equal(toObject, Assert.IsType<DamagePrevented>(back[1]));
        Assert.Equal(toPlayer, Assert.IsType<DamagePrevented>(back[2]));

        // The prevention no object is behind keeps its null rather than reading back as some object:
        // "this way" can never name it, and a lost null would let it name the wrong one.
        Assert.Null(Assert.IsType<DamagePrevented>(back[2]).PreventedBy);
        Assert.Null(Assert.IsType<DamagePrevented>(back[2]).ToObject);
    }

    /// <summary>
    /// CR 615: the prevention itself moves nothing. The damage not dealt is already the whole of what
    /// the surviving <c>DamageDealt</c> says, so folding this event is the identity and a replay of a
    /// log that carries it lands exactly where a log without it lands.
    /// </summary>
    [Fact]
    [RuleRef("615")]
    public void A_prevention_folds_to_the_same_state()
    {
        var started = Wave28Start();
        var state = GameReducer.Replay([started]);
        var prevented = new DamagePrevented(new ObjectId(1), new ObjectId(2), null, 3, new ObjectId(2));

        // Two replays are two records whose immutable members differ by reference, so the identity
        // of the fold is the assertion, as it is for the searched-library event.
        Assert.Same(state, prevented.Apply(state));
    }

    /// <summary>
    /// The trigger system matches on <see cref="IDamagePrevented"/>, not on the record (CR 615.13),
    /// so the record answering that interface is the whole of the agreement between the folder that
    /// declares the event and the one that emits it. A rename of any of the five members breaks here
    /// rather than in a card that silently stops triggering.
    /// </summary>
    [Fact]
    [RuleRef("615.13")]
    public void A_prevention_is_what_the_trigger_system_reads()
    {
        var read = Assert.IsAssignableFrom<IDamagePrevented>(
            new DamagePrevented(new ObjectId(4), new ObjectId(7), new PlayerId(1), 2, new ObjectId(9)));

        Assert.Equal(new ObjectId(4), read.Source);
        Assert.Equal(new ObjectId(7), read.ToObject);
        Assert.Equal(new PlayerId(1), read.ToPlayer);
        Assert.Equal(2, read.Prevented);
        Assert.Equal(new ObjectId(9), read.PreventedBy);
    }
}
