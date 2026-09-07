using MtgEngine.Cards;
using MtgEngine.Rules.State;
using Chars = MtgEngine.Rules.Engine.Characteristics;

namespace MtgEngine.Rules.Layers;

/// <summary>
/// The one reading of an object that needs no state: its printed face. Every look-back in the
/// rules — a creature that died (CR 603.10), a token that has stopped existing (CR 111.7) —
/// asks what an object <em>was</em>, and the layers of CR 613 cannot answer that, because they
/// are computed from the objects that exist now.
/// </summary>
public sealed partial class LayerSystem
{
    /// <summary>
    /// An object read off no state at all: its printed face, CR 613.1, with the face-down rules
    /// of CR 613.2b and CR 708.2a already applied. A caller that still has the object in the
    /// state must use <see cref="Of(GameState, ObjectId)"/> instead — this deliberately sees no
    /// continuous effect, no copy effect and no counter, because there is no state to read them
    /// from and inventing them would make a look-back answer for a game that is not being played.
    /// </summary>
    [RuleRef("603.10")]
    public static Chars PrintedCharacteristics(GameObject obj) => Snapshot(Printed(obj));

    /// <summary>
    /// CR 613.1a through the seam: the same printed reading, reachable from a caller that holds
    /// <c>ICharacteristics</c> rather than this class. The query's look-back had been reaching
    /// past the interface for the static (seam-requests/E0d.md); this is the one method that
    /// closes that, and it takes the object rather than an id because a look-back's object is
    /// one the state no longer holds and so has no id to hand the layers.
    /// </summary>
    [RuleRef("613.1a")]
    public Chars PrintedOf(GameObject obj) => PrintedCharacteristics(obj);
}
