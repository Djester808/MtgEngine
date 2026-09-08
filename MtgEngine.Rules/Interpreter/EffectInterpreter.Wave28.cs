using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Interpreter;

/// <summary>
/// Wave 28: manifest (CR 701.40). The keyword action that puts a card onto the battlefield as a
/// face-down 2/2 creature; nothing in the engine had an arm for it, so every card that manifests
/// refused with "no interpreter arm".
/// <para>
/// The characteristics are not written here: <c>Faces/FaceRules.FaceDown</c> already holds the
/// 2/2 with no name, no types beyond creature and no mana cost that CR 701.40a describes, and
/// <c>Characteristics/Layers</c> reads it for any object whose <c>FaceDown</c> is set. This arm
/// only makes the object face down, so manifest and morph (CR 702.37b) share one face and one
/// reading of what a face-down permanent is.
/// </para>
/// </summary>
public sealed partial class EffectInterpreter
{
    /// <summary>
    /// CR 701.40a: turn the card face down — it becomes a 2/2 face-down creature card with no
    /// text, no name, no subtypes and no mana cost — and put it onto the battlefield face down.
    /// A record with no <c>What</c> manifests the top card of the named player's library, which is
    /// what every printed manifest does; a record that names cards manifests those.
    /// <para>
    /// CR 701.40e: several cards are manifested one at a time, so each is moved on the state the
    /// one before it left, and a question raised on the way stops the rest (CR 616.1).
    /// </para>
    /// </summary>
    [RuleRef("701.40")]
    private EffectRun Manifesting(EffectRun run, Cards.Manifest manifest)
    {
        var before = run.State;

        if (manifest.What is { } named)
        {
            foreach (var id in Objects(run, named))
            {
                run = ManifestOne(run, id);
                if (run.Halted)
                    return run;
            }

            return Recall(run, manifest.RememberAs, Landed(before, run));
        }

        foreach (var player in PlayersOrYou(run, manifest.Who))
        {
            // CR 701.40e with 107.3b: how many, from the top of that player's own library.
            var count = manifest.N is null ? 1 : Math.Max(0, Number(run, manifest.N));
            foreach (var id in run.State.In(ZoneKey.Of(Zone.Library, player)).Take(count).ToList())
            {
                run = ManifestOne(run, id);
                if (run.Halted)
                    return run;
            }
        }

        return Recall(run, manifest.RememberAs, Landed(before, run));
    }

    /// <summary>
    /// One card manifested, CR 701.40a. CR 701.40f: if the face-down object cannot enter the
    /// battlefield the card is not manifested at all and stays where it was, which is exactly what
    /// a move the zone system did not perform leaves behind — so nothing is turned over when
    /// nothing landed.
    /// <para>
    /// <b>Known gap, filed as a seam request (E9w, <c>Zones/**</c>).</b> CR 701.40a turns the card
    /// face down <i>before</i> it is put onto the battlefield, so a "whenever a creature enters"
    /// trigger and any entering replacement must see a face-down 2/2. <c>IZoneSystem.Move</c> has
    /// no face-down flag and <c>Reborn</c> builds the arriving object face up, so the only road the
    /// interpreter has is to move the card and then turn it over. The board that results is right;
    /// what watches the entering itself sees the card's own face for that one event.
    /// </para>
    /// </summary>
    [RuleRef("701.40a")]
    private EffectRun ManifestOne(EffectRun run, ObjectId id)
    {
        (run, var landed) = Relocate(run, id, Zone.Battlefield, Placement.Default, tapped: false, controller: null);
        if (run.Halted || landed is not { } arrived || !run.State.Objects.ContainsKey(arrived))
            return run;

        return Emit(run, new ObjectChanged(arrived, FaceDown: true));
    }
}
