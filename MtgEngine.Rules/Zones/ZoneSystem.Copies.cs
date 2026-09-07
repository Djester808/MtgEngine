using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Faces;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Zones;

/// <summary>
/// A copy made where the original stands, CR 707.12. An effect that says "you may cast a copy of
/// that card" does not cast the card: the copy is created in the zone the object is already in and
/// that copy is what is cast, so the copy has to be an object before any casting system is handed
/// anything, and only the zone system makes objects. Nothing here casts, and nothing here reads a
/// card: what the copy has is what <see cref="CopyRules"/> says the copiable values are (CR 707.2).
/// </summary>
[RuleRef("707.12")]
public sealed partial class ZoneSystem
{
    /// <summary>
    /// The designation that marks a copy of a card, so CR 704.5e can find one that is neither on
    /// the stack nor on the battlefield and make it cease to exist. A copy of a card is not a token
    /// (CR 111.1, and CR 704.5d would remove it from every zone but the battlefield), and
    /// <c>StackEntry.IsCopy</c> says only that a copy of a <em>spell</em> is on the stack, so the
    /// mark a copy of a card carries is this designation and nothing else.
    /// </summary>
    [RuleRef("704.5e")]
    public const string CopyOfCard = "copy-of-card";

    /// <summary>
    /// Create a copy of an object in the zone that object is in, CR 707.12. The copy has the
    /// original's copiable values and nothing else (CR 707.2: no counters, no damage, no tapped
    /// status, no attachments, no designations but the one that marks it a copy); it is owned and
    /// controlled by <paramref name="owner"/>, it is a copy of a card rather than a token, and it
    /// lands next to the original so the zone reads in the order a player would see. The original
    /// is not touched — a copy is a second object, not a change to the first.
    /// </summary>
    [RuleRef("707.12")]
    public Outcome CopyInPlace(GameState state, ObjectId original, PlayerId owner)
    {
        ArgumentNullException.ThrowIfNull(state);

        // No object, no copiable values, nothing to copy: the same silence a move of an object that
        // is not there gives, rather than an exception at a seam.
        if (!state.Objects.TryGetValue(original, out var copied))
            return Outcome.Unchanged(state);

        // CR 707.12: the same zone the object is in. CR 400.3 still decides whose that zone is, so
        // a copy owned by another player is created in the same kind of zone that belongs to its
        // own owner; for the ordinary case — the owner of the original — the two are the same key.
        var destination = KeyFor(copied.Zone.Zone, owner);

        // CR 306.5b: a copy made where a planeswalker stands is a permanent entering the
        // battlefield, so it arrives with its printed loyalty; a copy made in any other zone is
        // untouched by that rule.
        var made = Entering(CopyRules.NewCopy(
            CopyRules.CopyOf(copied),
            new ObjectId(state.NextObjectId),
            owner,
            owner,
            destination,
            state.NextTimestamp,
            isToken: false) with
        {
            Designations = [CopyOfCard],
        });

        var tape = new Tape(state);

        // CR 614.1c: the creation is proposed like every other event this subsystem would apply, so
        // a replacement effect watching objects being created reaches a copy too, and a creation
        // replaced away creates nothing to cast.
        tape.AddAll(Offer(state, new ObjectCreated(made, Beside(state, destination, original)), made.InitialController));
        return tape.Done();
    }

    /// <summary>
    /// Where the copy lands: immediately after the original, CR 707.12, so a copy of a card in a
    /// graveyard or a library sits beside what it copies instead of jumping to the top. An original
    /// that is not in the destination — the only way that happens is a copy owned by someone else,
    /// whose zone is their own (CR 400.3) — takes the default place at the end.
    /// </summary>
    [RuleRef("707.12")]
    private static int Beside(GameState state, ZoneKey destination, ObjectId original)
    {
        var index = state.In(destination).IndexOf(original);
        return index < 0 ? -1 : index + 1;
    }
}
