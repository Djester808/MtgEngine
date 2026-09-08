using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Zones;

/// <summary>
/// A move that puts the object down face down, CR 701.40a and CR 702.37c. Two keyword actions want
/// it and neither could say it before: manifest turns the card face down <i>before</i> it is put
/// onto the battlefield, and a spell cast face down "enters the battlefield with the same
/// characteristics the spell had".
/// <para>
/// CR 400.7 is why it has to be an argument on the move rather than something done afterwards: the
/// object that arrives is a new object with no memory of the old one, so face-down status is
/// dropped by every zone change unless the move itself asks for it. Turning the permanent over
/// after it landed leaves the right board but the wrong game — the entering replacement of
/// CR 614.12 and every "whenever a creature enters" trigger read the proposal, and the proposal
/// would still be carrying the card's own name, types and printed power.
/// </para>
/// <para>
/// What a face-down object <i>is</i> is not written here: <c>Faces.FaceRules.FaceDown</c> holds the
/// 2/2 with no text, no name, no subtypes and no mana cost, and <c>Characteristics.Layers</c> reads
/// it for any object whose <see cref="GameObject.FaceDown"/> is set. This is only the road that
/// gets the flag onto the arriving object at the moment the rules put it there.
/// </para>
/// </summary>
[RuleRef("701.40a")]
public sealed partial class ZoneSystem
{
    /// <summary>
    /// The move of CR 400.7 with the card turned face down first, CR 701.40a. <paramref name="faceDown"/>
    /// says what the <i>arriving</i> object is, so it is on the event the replacement effects are
    /// offered; a false is the ordinary move, because a moved object is face up unless a rule says
    /// otherwise. The face index still chooses which face the card shows underneath (CR 712.12) —
    /// a face-down permanent is turned face up onto the face it was manifested or cast on.
    /// </summary>
    [RuleRef("701.40a")]
    public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where, bool tapped, PlayerId? controller, int? faceIndex, bool faceDown)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (Proposal(state, state, what, to, where, tapped, controller, faceIndex, faceDown) is not { } proposed)
            return Outcome.Unchanged(state);

        var tape = new Tape(state);
        // CR 614.1c and 616.1: the move is proposed, not performed. What actually happens is what
        // the replacement and prevention effects in force hand back — a tapped permanent, one with
        // counters, a different zone, nothing at all, or a question that stops the game. CR 701.40f
        // is the same road: a face-down object a rule keeps off the battlefield is not manifested
        // at all, and a move nothing performed leaves the card exactly where it was.
        tape.AddAll(Offer(state, proposed, AffectedBy(state, proposed)));
        return tape.Done();
    }
}
