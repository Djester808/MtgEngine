using System.Collections.Immutable;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Abilities;

/// <summary>
/// What a printed "this way" points back at: the verb of the sentence that did the thing.
/// </summary>
/// <remarks>
/// The vocabulary is deliberately short, and every entry earns its place by being something an
/// effect does <em>while it resolves</em>. A verb whose events arrive later cannot be here: the
/// engine defers every question a player has to answer to the settle after the resolution
/// (<c>_looksOwed</c> and its siblings), so a sentence reading "cards revealed this way" or "cards
/// you discarded this way" would be asking about events that have not happened yet, would answer
/// nought for ever, and would do it on a card that compiled clean. Those are refused by the
/// reader rather than answered wrongly.
/// </remarks>
public enum TouchVerb
{
    /// <summary>Moved to exile, from wherever it was (CR 406.1).</summary>
    Exiled,

    /// <summary>Put into a graveyard from the top of a library (CR 701.13a).</summary>
    Milled,

    /// <summary>Put into a graveyard from the battlefield by destruction (CR 701.8).</summary>
    Destroyed,

    /// <summary>Put into a graveyard from the battlefield, however it got there (CR 700.4).</summary>
    Died,

    /// <summary>Put into a graveyard from anywhere.</summary>
    PutIntoGraveyard,

    /// <summary>Moved to a hand from anywhere but a library (CR 701.19).</summary>
    ReturnedToHand,

    /// <summary>Moved from a library to a hand (CR 121.1).</summary>
    Drawn,

    /// <summary>Moved onto the battlefield from anywhere.</summary>
    PutOntoBattlefield,
}

/// <summary>One thing an earlier effect of this same resolution did to one object (CR 608.2c).</summary>
/// <remarks>
/// The id is the one the object has <em>after</em> the move, because a card that changes zones
/// becomes a new object under a new id (CR 400.7) and the old one names nothing afterwards. Every
/// effect that moves something already picks the new id when it builds the event, so this records
/// the id that will exist rather than the id that did.
/// </remarks>
/// <param name="Id">What the object is called once it has moved (CR 400.7).</param>
/// <param name="Card">The card it was, read before the move while the old id still names it.</param>
/// <param name="From">Where it came from.</param>
/// <param name="To">Where it went.</param>
/// <param name="Cause">Why it moved, which is how destruction is told from anything else.</param>
/// <param name="Controller">
/// Who controlled it as it left the battlefield, or null when it was not on one (CR 613.1b).
/// </param>
public sealed record Touch(
    ObjectId Id,
    CardDefinition? Card,
    Zone From,
    Zone To,
    MoveCause Cause,
    Guid? Controller)
{
    /// <summary>Whether this is one of the things a printed participle names.</summary>
    /// <remarks>
    /// One place, asked by both readers, so a count and a condition can never disagree about what
    /// "destroyed this way" means. Destruction is the arm that needs the cause: a creature
    /// sacrificed to a cost has died and has been put into a graveyard, and has not been
    /// destroyed (CR 701.8a).
    /// </remarks>
    public bool Answers(TouchVerb verb) => verb switch
    {
        TouchVerb.Exiled => To == Zone.Exile,
        TouchVerb.Milled => From == Zone.Library && To == Zone.Graveyard,
        TouchVerb.Destroyed => From == Zone.Battlefield
            && To == Zone.Graveyard
            && Cause is MoveCause.Destroy or MoveCause.DestroyNoRegeneration,
        TouchVerb.Died => From == Zone.Battlefield && To == Zone.Graveyard,
        TouchVerb.PutIntoGraveyard => To == Zone.Graveyard,

        // A draw is a move from a library to a hand and is emphatically not a return, so the two
        // are separated by where the card came from rather than by where it went.
        TouchVerb.ReturnedToHand => To == Zone.Hand && From != Zone.Library,
        TouchVerb.Drawn => To == Zone.Hand && From == Zone.Library,
        TouchVerb.PutOntoBattlefield => To == Zone.Battlefield,
        _ => false,
    };
}

/// <summary>
/// What the effects before this one in the same resolution did, for a sentence that says
/// "this way" (CR 608.2).
/// </summary>
/// <remarks>
/// CR 608.2 has a resolution use "information about what happened during" itself, and nothing in
/// this engine wrote any of it down. "Destroy all creatures. Draw a card for each creature
/// destroyed this way" is one instruction whose second half is about the first half's result, and
/// the second half had nothing to ask - so the whole line went unread, on 797 corpus cards.
/// <para>
/// It is derived from the events an effect emitted rather than stored in the game state, which is
/// what keeps it honest and cheap at once: nothing new has to be folded, nothing new has to be
/// compared by <c>GameState.Equals</c>, and <c>Replay(log) == State</c> is untouched. The record
/// exists for the length of one resolution, which is exactly as long as "this way" means anything.
/// </para>
/// <para>
/// <b>It accumulates rather than replacing.</b> The verb disambiguates, not the position: "discard
/// two cards, then draw two cards. For each card drawn this way ..." asks about the draw and not
/// about the discard, and it says so in the sentence. Keeping only the last effect's events would
/// answer the same question by guessing at the order instead of reading the word, and would be
/// wrong the moment a card puts a third sentence between them.
/// </para>
/// </remarks>
public sealed record ResolutionRecord
{
    /// <summary>Nothing recorded - a resolution that has not run an effect yet.</summary>
    public static readonly ResolutionRecord Empty = new();

    public ImmutableList<Touch> Touches { get; init; } = [];

    /// <summary>The record after one more effect's events (CR 608.2c).</summary>
    /// <remarks>
    /// Only zone changes, because only zone changes are what the printed participles name. A
    /// widening here is not free: an entry the readers cannot tell apart from another is how a
    /// count comes out too big, so a new verb wants a new arm on <see cref="Touch.Answers"/>
    /// rather than a looser gather here.
    /// </remarks>
    public ResolutionRecord Following(
        IReadOnlyList<GameEvent> emitted, Func<ObjectId, CardDefinition?> cardOf)
    {
        ArgumentNullException.ThrowIfNull(emitted);
        ArgumentNullException.ThrowIfNull(cardOf);

        var added = Touches;

        foreach (var e in emitted)
        {
            if (e is not ObjectMoved moved)
                continue;

            // The card is read off the id the object had *before* the move, because that is the
            // id the state still knows: the new object does not exist until the event is applied,
            // and this runs while the batch is being built.
            added = added.Add(new Touch(
                moved.NewId,
                cardOf(moved.OldId),
                moved.From,
                moved.To,
                moved.Cause,
                moved.LeavingControllerId ?? moved.ControllerId));
        }

        return added == Touches ? this : this with { Touches = added };
    }
}

/// <summary>
/// The printed phrase in front of "this way" - a verb, and what it happened to.
/// </summary>
/// <remarks>
/// One type shared by every reader of the family, because "for each creature card exiled this
/// way", "equal to the number of creature cards exiled this way" and "if a creature card is
/// exiled this way" are one question in three grammars. A filter per reader would be the "list the
/// compiler has to remember" bug this file has already paid for four times.
/// <para>
/// The types are alternatives and each alternative is a conjunction, which is how the pile counter
/// already reads a noun: "artifact or land card" is either, and "artifact creature card" is both.
/// </para>
/// </remarks>
public sealed record TouchFilter(TouchVerb Verb)
{
    /// <summary>Card types the object must have, as alternatives; empty means any card.</summary>
    public ImmutableList<ImmutableArray<CardType>> Types { get; init; } = [];

    /// <summary>Types the object must <em>not</em> have - "nonland card", "noncreature card".</summary>
    public ImmutableList<CardType> Excluded { get; init; } = [];

    /// <summary>
    /// Whether only a permanent this resolution's controller controlled counts.
    /// </summary>
    /// <remarks>
    /// "Each creature you controlled that was destroyed this way" against "each creature destroyed
    /// this way" - one sweeper, two different numbers, and the difference is a word. Read off the
    /// touch's leaving controller rather than off the object, because the object has left the
    /// battlefield and control is layer 2 (CR 613.1b): the controller stored on what it became is
    /// where its control started, not where it was when it died.
    /// </remarks>
    public bool YoursOnly { get; init; }

    /// <summary>Whether one recorded touch is what the phrase named.</summary>
    public bool Admits(Touch touch)
    {
        ArgumentNullException.ThrowIfNull(touch);

        if (!touch.Answers(Verb))
            return false;

        // A touch whose card could not be read is not admitted by a phrase that names a type.
        // Counting it would be guessing, and a count that comes out too big is the same class of
        // bug as one that comes out too small.
        if (touch.Card is not { } card)
            return Types.IsEmpty && Excluded.IsEmpty;

        if (Excluded.Exists(type => card.CardTypes.HasFlag(type)))
            return false;

        return Types.IsEmpty
            || Types.Exists(set => set.All(type => card.CardTypes.HasFlag(type)));
    }

    /// <summary>How many of this resolution's touches the phrase names.</summary>
    public int In(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var you = context.ControllerId;

        return context.Record.Touches.Count(
            touch => Admits(touch) && (!YoursOnly || touch.Controller == you));
    }
}

/// <summary>
/// "If a creature card is exiled this way, ..." - a guard on what this resolution just did
/// (CR 608.2c).
/// </summary>
/// <remarks>
/// Its own effect rather than an arm of <see cref="OnlyIf"/>, because the two ask different
/// things of different places: that one is handed a state, an ability source and the source
/// permanent, and every condition it can express is a fact about the board. This one is a fact
/// about the resolution, which only the context knows. <see cref="OnlyIfRollAtLeast"/> is the
/// same shape for the same reason, one field along.
/// <para>
/// The threshold is a count and not a flag, because "at least one card was milled this way" and
/// "two or more cards were milled this way" are one clause with a different number in it.
/// </para>
/// </remarks>
public sealed record OnlyIfTouched(
    TouchFilter Filter,
    int AtLeast,
    ImmutableList<IEffect> Effects) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Filter.In(context) < AtLeast)
            return [];

        var events = new List<GameEvent>();
        foreach (var effect in Effects)
            events.AddRange(effect.Resolve(context));

        return events;
    }
}
