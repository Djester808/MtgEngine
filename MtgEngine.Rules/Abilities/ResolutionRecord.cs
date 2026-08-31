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

/// <summary>
/// What an object was as it left, for the record to keep (CR 608.2h).
/// </summary>
/// <remarks>
/// One type rather than three lookups, because every field of it is read at the same instant and
/// off the same object: the moment before a batch of moves is applied, while the old id still
/// names something. Splitting them would be three walks of the state per moved card and three
/// chances for one of them to be taken a step later than the others.
/// </remarks>
/// <param name="Card">The card it was, through the computed characteristics (CR 613.2c).</param>
/// <param name="Power">Its power as it left, or null when it had none.</param>
/// <param name="Toughness">Its toughness as it left, or null when it had none.</param>
public sealed record LastKnown(CardDefinition Card, int? Power, int? Toughness);

/// <summary>
/// What a cost took, measured the instant before it was paid (CR 601.2h, 602.2b, 608.2k).
/// </summary>
/// <remarks>
/// "…, where X is the sacrificed creature's power" points at an object the spell or ability's own
/// <em>cost</em> named, not at one an earlier effect of the same resolution touched. That is a
/// different back-reference with a different rule behind it — CR 608.2k, which lets an effect
/// refer to an untargeted object its cost referred to, and means that object still even after the
/// object has changed or gone — so it is deliberately not a <see cref="Touch"/> and does not go on
/// <see cref="ResolutionRecord"/>. Filing it there would have told <see cref="TouchFilter"/> that
/// "sacrificed" is a participle the record can answer, and it is not: the sacrifices that reader
/// refuses are the ones an <em>effect</em> makes, which are a question the player has not been
/// asked yet when the next sentence runs.
/// <para>
/// Three numbers rather than the object itself, because a permanent sacrificed to a cost is in a
/// graveyard under a new id long before the ability resolves (CR 400.7) and the card there answers
/// with its printed power, not with what an anthem had made it. CR 608.2h asks for the object's
/// last known information, so the numbers are read while the permanent is still on the battlefield
/// and carried forward — the same discipline <see cref="Touch"/> keeps, one step earlier in the
/// turn.
/// </para>
/// <para>
/// Null power and toughness for something that had none: a sacrificed artifact, a card exiled from
/// a graveyard to pay for an ability. A reader asked for a stat that was never there answers zero
/// rather than inventing one.
/// </para>
/// </remarks>
/// <param name="Power">Its power as the cost was paid, or null when it had none.</param>
/// <param name="Toughness">Its toughness as the cost was paid, or null when it had none.</param>
/// <param name="ManaValue">Its mana value, which every card has (CR 202.3).</param>
public sealed record CostPaid(int? Power, int? Toughness, int ManaValue);

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
    /// <summary>
    /// What the object was called <em>before</em> the move, so the two halves of one zone change
    /// can be joined up (CR 400.7).
    /// </summary>
    /// <remarks>
    /// The record was built for the participles — "each creature destroyed this way" asks what
    /// happened, never which id it happened to — so only the arriving id was kept. A pronoun is
    /// the other question: "return target creature card from your graveyard to the battlefield.
    /// <em>It</em> gains haste" names the object the sentence already targeted, and the only way
    /// to get from the id the player chose to the id the permanent arrived under is to have
    /// written the pair down. Null on a touch nothing joined, which is none of them today.
    /// </remarks>
    public ObjectId? OldId { get; init; }

    /// <summary>What it last had for power, as it left (CR 608.2h).</summary>
    /// <remarks>
    /// Last known information rather than the printed number, because "X is the power of the
    /// creature exiled this way" is asked about a creature that is no longer on the battlefield
    /// and whose counters and pumps counted right up until it left. The card alone answers the
    /// printed value, which is a different number on every creature this engine has ever pumped -
    /// and one that would be wrong in silence, since the sentence still compiles and still plays.
    /// <para>
    /// Null when the card has none, which is every noncreature. It is the card's own value for an
    /// object that was never on a battlefield, where there are no layers to apply and the printed
    /// characteristics are the real ones (CR 108.3).
    /// </para>
    /// </remarks>
    public int? Power { get; init; }

    /// <summary>What it last had for toughness, as it left (CR 608.2h).</summary>
    public int? Toughness { get; init; }

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
        IReadOnlyList<GameEvent> emitted, Func<ObjectId, LastKnown?> was)
    {
        ArgumentNullException.ThrowIfNull(emitted);
        ArgumentNullException.ThrowIfNull(was);

        var added = Touches;

        foreach (var e in emitted)
        {
            if (e is not ObjectMoved moved)
                continue;

            // Read off the id the object had *before* the move, because that is the id the state
            // still knows: the new object does not exist until the event is applied, and this
            // runs while the batch is being built.
            var leaving = was(moved.OldId);

            added = added.Add(new Touch(
                moved.NewId,
                leaving?.Card,
                moved.From,
                moved.To,
                moved.Cause,
                moved.LeavingControllerId ?? moved.ControllerId)
            {
                OldId = moved.OldId,
                Power = leaving?.Power,
                Toughness = leaving?.Toughness,
            });
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
    /// <summary>What the object must be, as alternatives; empty means any card.</summary>
    public ImmutableList<TouchNoun> Nouns { get; init; } = [];

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
            return Nouns.IsEmpty && Excluded.IsEmpty;

        if (Excluded.Exists(type => card.CardTypes.HasFlag(type)))
            return false;

        return Nouns.IsEmpty || Nouns.Exists(noun => noun.Admits(card));
    }

    /// <summary>This resolution's touches that the phrase names, in the order they happened.</summary>
    /// <remarks>
    /// The order matters to the effects that act on the set rather than count it: the choice a
    /// player is offered lists the cards in the order the game put them there, which is the order
    /// they went into the graveyard and the order the log will replay them in.
    /// </remarks>
    public IEnumerable<Touch> Matching(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var you = context.ControllerId;

        return context.Record.Touches.Where(
            touch => Admits(touch) && (!YoursOnly || touch.Controller == you));
    }

    /// <summary>How many of this resolution's touches the phrase names.</summary>
    public int In(ResolutionContext context) => Matching(context).Count();

    /// <summary>
    /// A characteristic of the one thing the phrase names, or zero (CR 608.2h).
    /// </summary>
    /// <remarks>
    /// "X is the mana value of the permanent exiled this way" is written in the singular and
    /// means it: the sentence before exiled exactly one thing. Zero when nothing was touched -
    /// which is a spell whose target had gone, and what those cards do - and zero again when
    /// several were, because a phrase that says "the permanent" has not said which one and any
    /// answer this picked would be a guess. Fail-closed in the same direction the verb list is:
    /// the number a card gets is one it could have printed, not one this reader invented.
    /// </remarks>
    public int StatIn(ResolutionContext context, TouchStat stat)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Matching(context).ToList() is [var only] ? ValueOf(context, only, stat) ?? 0 : 0;
    }

    /// <summary>
    /// One fold over every object the phrase names - the total, the greatest or the least
    /// (CR 608.2h).
    /// </summary>
    /// <remarks>
    /// The plural twin of <see cref="StatIn"/>, over the set <see cref="Matching"/> answers and
    /// through the per-touch reading that one uses, so a card that counts what this resolution
    /// touched and a card that totals it cannot disagree about which objects the phrase named.
    /// That is the whole point of asking the filter for the set once: the tally is
    /// <see cref="In"/>, the aggregate is this, and there is no third answer to "which objects".
    /// <para>
    /// A touch with no such characteristic is left out of the fold rather than folded in as a
    /// zero, which is the rule the board aggregate next door keeps for the same reason: a Sol
    /// Ring destroyed alongside the creatures has no power at all, and counting it as nought
    /// would drag "the least power among permanents destroyed this way" to nothing.
    /// </para>
    /// <para>
    /// Zero for an empty set, which is what a value that cannot be determined comes to
    /// (CR 107.2), and clamped at zero on the way out (CR 107.1b) - a creature that left the
    /// battlefield with -3/-0 on it had power below nought, and a calculation deciding the
    /// result of an effect uses zero instead.
    /// </para>
    /// </remarks>
    public int AggregateIn(ResolutionContext context, TouchStat stat, TouchFold fold)
    {
        ArgumentNullException.ThrowIfNull(context);

        var values = new List<int>();

        foreach (var touch in Matching(context))
        {
            if (ValueOf(context, touch, stat) is { } value)
                values.Add(value);
        }

        if (values.Count == 0)
            return 0;

        var answer = fold switch
        {
            TouchFold.Total => values.Sum(),
            TouchFold.Least => values.Min(),
            _ => values.Max(),
        };

        return Math.Max(0, answer);
    }

    /// <summary>
    /// What one recorded object's characteristic came to, or null when it has none.
    /// </summary>
    /// <remarks>
    /// <b>Last known information, not the card lying in the graveyard</b> (CR 608.2h). A creature
    /// that died with three +1/+1 counters on it was a 5/5 as it left; the object the graveyard
    /// holds is a new one (CR 400.7) with no layers applied to it (CR 613.1), so its power is the
    /// printed 2/2 on its face (CR 202.3). A fold that looked the touch up where it now lies
    /// would therefore be short by every counter and every pump on every card in this family, and
    /// short in silence - the sentence still compiles and the card still plays. The record stored
    /// what each object was as it left precisely so this does not have to guess; this reads that.
    /// <para>
    /// The one object that is <em>not</em> read from the record is the one still on the
    /// battlefield, which is where a "put onto the battlefield this way" touch leaves it. Nothing
    /// has left, so there is no last known information to use: CR 613 applies and the computed
    /// characteristics are what the permanent is now, counters and anthems included. What the
    /// record holds for that touch is what the card was in the zone it came from, which is the
    /// one number that is certainly wrong.
    /// </para>
    /// <para>
    /// Null rather than zero for a characteristic the object does not have, and null for a touch
    /// whose card could not be read at all - the same fail-closed direction <see cref="Admits"/>
    /// takes for a noun it cannot check.
    /// </para>
    /// </remarks>
    private static int? ValueOf(ResolutionContext context, Touch touch, TouchStat stat)
    {
        if (touch.To == Zone.Battlefield
            && context.State.TryGetObject(touch.Id, out var live)
            && live.Permanent is not null)
        {
            var now = Characteristics.Of(context.State, context.Abilities, live);

            return stat switch
            {
                TouchStat.Power => now.Power,
                TouchStat.Toughness => now.Toughness,
                _ => now.Card.Cmc,
            };
        }

        return stat switch
        {
            TouchStat.Power => touch.Power,
            TouchStat.Toughness => touch.Toughness,
            _ => touch.Card?.Cmc,
        };
    }
}

/// <summary>Which way a sentence folds the objects it names (CR 608.2h).</summary>
/// <remarks>
/// The three the corpus prints, and the same three the board aggregate next door reads - "the
/// greatest" and "the highest" are one fold under two words, as are "the least" and "the lowest".
/// A closed list rather than a string, because a fold this did not recognise would have to pick
/// one, and every wrong pick is a number the card does not print.
/// </remarks>
public enum TouchFold
{
    /// <summary>Every value added together.</summary>
    Total,

    /// <summary>The largest - "the greatest", "the highest".</summary>
    Greatest,

    /// <summary>The smallest - "the least", "the lowest".</summary>
    Least,
}

/// <summary>Which characteristic of a recorded object a sentence asks for (CR 608.2h).</summary>
/// <remarks>
/// A closed list for the reason <c>VariableIsStatLine</c>'s is: the three the corpus prints, with
/// no arithmetic tail. "Its power minus 1" read as the bare stat is a card that plays a bigger
/// number than it prints.
/// </remarks>
public enum TouchStat
{
    Power,
    Toughness,
    ManaValue,
}

/// <summary>
/// One alternative of what a printed noun may be - "artifact creature card or Vehicle card".
/// </summary>
/// <remarks>
/// The types and the subtype are one alternative rather than two lists side by side, because they
/// are a conjunction where they appear together and a disjunction where they do not. Read as two
/// lists, "Zombie creature card exiled this way" would count every Zombie <em>or</em> every
/// creature, and on a sweeper that is nearly every card in the graveyard.
/// <para>
/// A subtype carries the card type it belongs to, taken from the shared table rather than assumed
/// to be a creature: "for each Equipment put into a graveyard this way" wants an artifact, and the
/// same assumption made one reader over is written up in <c>Specs.SubtypeCardType</c> as a card
/// that reported itself completely understood while matching nothing at all.
/// </para>
/// </remarks>
public sealed record TouchNoun
{
    /// <summary>Card types the object must have, all of them; empty means any card.</summary>
    public ImmutableArray<CardType> Types { get; init; } = [];

    /// <summary>A subtype the object must have as well, or null (CR 205.3).</summary>
    public string? Subtype { get; init; }

    /// <summary>Whether one card answers to this alternative.</summary>
    public bool Admits(CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (!Types.All(type => card.CardTypes.HasFlag(type)))
            return false;

        return Subtype is null
            || card.Subtypes.Contains(Subtype, StringComparer.OrdinalIgnoreCase);
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

/// <summary>
/// "Put a permanent card from among the cards milled this way into your hand" - the recorded set
/// as the thing an effect acts on (CR 608.2c).
/// </summary>
/// <remarks>
/// The other half of the family the count and the condition read. Those two ask how many and
/// whether; this one names them, which is what a sentence needs when the cards themselves are the
/// object of the verb rather than the size of the pile.
/// <para>
/// It asks rather than moving, because which card is a decision the player makes and this engine
/// never stops a resolution to take one - the request is settled afterwards like every other owed
/// question. <b>That is why nothing may be recorded from the answer.</b> A sentence later in the
/// same line asking "if you returned a card to your hand this way" would be asking about a move
/// that has not happened yet, and would answer nought for ever; the reader refuses those by name,
/// which is what keeps Cache Grab's second sentence unread rather than silently false.
/// </para>
/// <para>
/// The candidates are worked out here, while the record still exists, and travel in the event.
/// One that has moved on in between is dropped when the question is asked: a choice offering a
/// card that is not there any more is not a choice.
/// </para>
/// </remarks>
/// <param name="Filter">Which of the resolution's touches the phrase named.</param>
/// <param name="To">Where the chosen cards go.</param>
/// <param name="Cause">Why they move, which is what the triggers watching will see.</param>
/// <param name="Most">The ceiling on the answer - "up to two", or one for the singular form.</param>
/// <param name="Optional">
/// Whether taking none is allowed. "You may put" offers nothing when the player declines; "return
/// a creature card milled this way to your hand" is an instruction and takes one if there is one
/// to take (CR 608.2). The two differ only here, so they are one effect and not two.
/// </param>
public sealed record TakeFromTouched(
    TouchFilter Filter, Zone To, MoveCause Cause, int Most, bool Optional) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Zone? from = null;
        var candidates = ImmutableArray.CreateBuilder<ObjectId>();

        foreach (var touch in Filter.Matching(context))
        {
            // Where the participle put it, which is where it must still be. A card the rest of
            // this resolution has already moved on is no longer the thing the sentence named.
            if (!context.State.TryGetObject(touch.Id, out var still) || still.Zone != touch.To)
                continue;

            from = touch.To;
            candidates.Add(touch.Id);
        }

        if (from is not { } zone || candidates.Count == 0)
            return [];

        var most = Math.Min(Most, candidates.Count);

        return
        [
            new TouchedChoiceRequested(
                context.ControllerId,
                candidates.ToImmutable(),
                zone,
                To,
                Cause,
                Optional ? 0 : Math.Min(1, most),
                most),
        ];
    }
}

/// <summary>
/// "You may play cards exiled this way until the end of your next turn" (CR 601.3e).
/// </summary>
/// <remarks>
/// The same permission the impulse-draw idiom grants, given to the recorded set instead of to the
/// cards one adjacent sentence exiled. That is not a rewording of the same reader: "this way"
/// points at whatever exiled them, which may be two sentences back, may be a loop, and on
/// Heartless Conscription is a sweeper. Reading it as the pair would need the two sentences to be
/// next to each other and would silently take the wrong cards when they are not.
/// <para>
/// Nothing is asked and nothing is chosen, so this one carries no risk of the deferred-answer
/// trap: the permission lands on every card the phrase named, as it resolves.
/// </para>
/// </remarks>
/// <param name="ThroughOwnersNextTurn">
/// Whether the window runs to the end of the owner's next turn rather than to the end of this one
/// - two different durations, and the longer one cannot be written as a turn number.
/// </param>
public sealed record MayPlayTouched(TouchFilter Filter, bool ThroughOwnersNextTurn) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        foreach (var touch in Filter.Matching(context))
        {
            if (!context.State.TryGetObject(touch.Id, out var still) || still.Zone != touch.To)
                continue;

            events.Add(new CardMayBePlayed(
                touch.Id, context.State.TurnNumber, ThroughOwnersNextTurn));
        }

        return events;
    }
}
