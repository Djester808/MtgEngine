using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Events;

// Events about a library as a library rather than about one object in it: the record that a player
// searched one (CR 701.23). A card that watches a search reads this and nothing else.

/// <summary>
/// A player searched a library, CR 701.23a: an effect instructed them to look at all of its cards
/// and find what it described. This event is the record of the search itself, written when the
/// search begins — whatever it finds, and whether it finds anything at all — because CR 701.23f
/// says an ability that triggers on a library being searched triggers, so such a trigger needs an
/// event to match. A player looking at their own library with no instruction to search is not
/// searching and writes nothing.
/// <para>
/// <paramref name="Player"/> owns the library; <paramref name="Searcher"/> is the player doing the
/// searching, who is the player a trigger matches. An effect can send a player into another
/// player's library, so the two are separate fields and
/// neither is derived from the other. The shuffle CR 701.23c may call for afterwards is its own
/// <see cref="ZoneReordered"/> and no part of this event; a search over several libraries is one
/// of these per library, and CR 701.23h's repeated search of one library is a single search and so
/// a single event.
/// </para>
/// </summary>
[RuleRef("701.23")]
public sealed record LibrarySearched(PlayerId Player, PlayerId Searcher) : GameEvent
{
    /// <summary>
    /// CR 701.23a: looking at the cards in a library changes nothing about them or their order, so
    /// this event folds to the state it was given. The log line is the whole of the event, which is
    /// what lets a replay land on the same state whether or not the search was recorded.
    /// </summary>
    public override GameState Apply(GameState state) => state;
}
