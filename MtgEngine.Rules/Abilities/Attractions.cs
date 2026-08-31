using MtgEngine.Domain.Models;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Abilities;

/// <summary>
/// The Attraction deck, opening from it, and which Attractions a roll visits (CR 717, 701.51,
/// 701.52).
/// </summary>
/// <remarks>
/// <b>Nothing here is a new zone, a new state field or a new kind of object.</b> CR 717.2 says
/// where an Attraction deck lives in as many words — "a supplementary Attraction deck that exists
/// in the command zone" — so the deck is the player's Attraction cards sitting in
/// <see cref="Zone.Command"/>, in order, exactly as a dungeon card sits there (CR 309.2b) and an
/// emblem does. Opening one is <see cref="ObjectMoved"/> from that zone to the battlefield, which
/// is a move the reducer has always been able to fold.
/// <para>
/// The three facts that make this a data feature rather than an engine feature:
/// </para>
/// <list type="bullet">
/// <item><b>The lights are printed characteristics</b> (CR 717.1) that no rules text carries, so
/// they arrive on <see cref="CardDefinition.AttractionLights"/> from the bulk data. All 22
/// playable Attractions have them, and all 50 objects in the dump — the long-standing claim that
/// 46 Attractions were missing the field was a text match on cards that <em>open</em>
/// Attractions.</item>
/// <item><b>A visit is a die roll</b> (CR 701.52a), and the engine already rolls dice into the
/// log as their outcome (<see cref="DiceRolled"/>), so a replay reads the number rather than
/// rolling again.</item>
/// <item><b>A visit ability is an ordinary triggered ability</b> (CR 702.159a), so the only thing
/// missing between the roll and the card was an event saying which Attraction the number lit
/// up.</item>
/// </list>
/// <para>
/// It is one file for the reason <see cref="Dungeons.VentureEvents"/> is one method: three things
/// visit — the turn-based action at the start of a precombat main phase (CR 505.5, 717.4), the
/// two cards that print "roll to visit your Attractions", and the deferred branch behind that
/// roll — and a second copy of "whose lights hold this number" would be the copy that goes stale.
/// </para>
/// </remarks>
public static class Attractions
{
    /// <summary>The artifact subtype every Attraction has (CR 205.3g, 717.1).</summary>
    public const string Subtype = "Attraction";

    /// <summary>How many sides the die a visit rolls has (CR 701.52a).</summary>
    public const int DieSides = 6;

    /// <summary>
    /// Whether this card is an Attraction, asked of the printed card rather than the computed
    /// characteristics.
    /// </summary>
    /// <remarks>
    /// Printed on purpose, and it is not the usual mistake of reading <c>obj.Card</c> where a
    /// characteristic was meant. What makes an Attraction visitable is the column of lights
    /// (CR 717.1), which is printed on the cardboard and cannot be granted: a permanent that an
    /// effect turned into an Attraction has no lights, so no result could ever match it and
    /// asking the computed subtype would only find objects that can never be visited anyway.
    /// </remarks>
    public static bool Is(CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);

        return card.Subtypes.Contains(Subtype, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether a result is lit up on this Attraction (CR 701.52a).</summary>
    /// <remarks>
    /// False for an Attraction with no lights at all, which is the fail-closed half of the same
    /// rule: an empty column matches nothing, so a card that reached the game without its data
    /// sits on the battlefield doing nothing rather than being visited by every roll. The
    /// compiler refuses that card's visit line for the same reason, so this arm should be
    /// unreachable from the corpus — it is written down rather than assumed.
    /// </remarks>
    public static bool IsLit(CardDefinition card, int result)
    {
        ArgumentNullException.ThrowIfNull(card);

        return card.AttractionLights.Contains(result);
    }

    /// <summary>
    /// This player's Attraction deck, top first (CR 717.2).
    /// </summary>
    /// <remarks>
    /// The command zone keeps its objects in insertion order and <c>Game.Start</c> puts a
    /// shuffled Attraction deck into it one card at a time at the bottom, so the order here is
    /// the order of the deck and the first entry is its top card. Filtered by owner rather than
    /// controller: a deck belongs to the player who brought it, and an Attraction another player
    /// has gained control of on the battlefield is not in anybody's deck.
    /// <para>
    /// And filtered by <see cref="GameObject.IsJunked"/>, which is the other pile the same zone
    /// holds (CR 717.6a). A junked Attraction that counted as deck would be re-opened by the next
    /// card that says "open an Attraction" — off the top, because a move carries
    /// <see cref="ZonePosition.Top"/> — so destroying one would put it back rather than take it
    /// away.
    /// </remarks>
    public static IReadOnlyList<GameObject> DeckOf(GameState state, Guid playerId)
    {
        ArgumentNullException.ThrowIfNull(state);

        var deck = new List<GameObject>();

        foreach (var id in state.Command)
        {
            if (state.TryGetObject(id, out var card)
                && card.OwnerId == playerId
                && !card.IsJunked
                && Is(card.Card))
            {
                deck.Add(card);
            }
        }

        return deck;
    }

    /// <summary>
    /// Opening N Attractions: the top N cards of the deck go onto the battlefield (CR 701.51b).
    /// </summary>
    /// <remarks>
    /// Fewer than N when the deck is short, and none at all when it is empty — CR 701.51a lets a
    /// player open an Attraction only in a game they brought an Attraction deck to, and a player
    /// who brought none simply cannot follow the instruction. Nothing is thrown and nothing is
    /// invented: a deck with no cards left is a "put the top card" with no top card, the same
    /// shape as a draw from an empty library, and the card that said it goes on to its next
    /// sentence.
    /// </remarks>
    public static IReadOnlyList<GameEvent> OpenEvents(GameState state, Guid playerId, int count)
    {
        ArgumentNullException.ThrowIfNull(state);

        var opening = new List<GameEvent>();

        foreach (var card in DeckOf(state, playerId).Take(Math.Max(0, count)))
        {
            opening.Add(new ObjectMoved(
                card.Id,
                ObjectId.New(),
                Zone.Command,
                Zone.Battlefield,
                playerId,
                MoveCause.OpenAttraction));
        }

        return opening;
    }

    /// <summary>
    /// Which of this player's Attractions a result visits (CR 701.52a).
    /// </summary>
    /// <remarks>
    /// Control and not ownership here, and computed control at that (CR 613.1b): the rule says
    /// "if you control one or more Attractions with a number lit up equal to that result", so an
    /// Attraction stolen from its owner is visited by the roll of the player who now controls it
    /// and by nobody else.
    /// </remarks>
    public static IReadOnlyList<GameEvent> VisitEvents(
        GameState state, IAbilitySource abilities, Guid playerId, int result)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);

        var visits = new List<GameEvent>();

        foreach (var id in state.Battlefield)
        {
            if (!state.TryGetObject(id, out var attraction)
                || attraction.Permanent is null
                || !Is(attraction.Card)
                || !IsLit(attraction.Card, result))
            {
                continue;
            }

            if (Characteristics.ControllerOf(state, abilities, attraction) != playerId)
                continue;

            visits.Add(new AttractionVisited(playerId, id));
        }

        return visits;
    }
}
