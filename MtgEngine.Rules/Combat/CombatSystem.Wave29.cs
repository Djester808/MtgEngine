using MtgEngine.Cards;
using MtgEngine.Rules.Engine;

namespace MtgEngine.Rules.Combat;

/// <summary>
/// The one entry point combat needs into costs it cannot pay itself, CR 508.1h with 118.6.
/// <para>
/// The total cost to attack is determined and locked in at CR 508.1h and paid in full at
/// CR 508.1j, and nothing in either rule says the cost is mana. A cost that moves objects — life,
/// a sacrifice, a discard — is paid against a cost context built from the zone system and the
/// query, and this subsystem is deliberately constructed with neither; the casting system is the
/// system that has them, and E3p § 2 gave it <see cref="ICastingSystem.PayForRule"/> and
/// <see cref="ICastingSystem.CanPayForRule"/> for exactly this caller (E3q § 3 tested them from
/// the caller's side).
/// </para>
/// </summary>
[RuleRef("508.1h")]
public sealed partial class CombatSystem
{
    /// <summary>
    /// The casting system, when there is one. A settable handle rather than a constructor
    /// argument for the same reason <see cref="Replacements"/> is one: the casting system is built
    /// with the interpreter, which moves and taps permanents that are in combat, so the two are
    /// composed in <c>Engine/Game.cs</c> once both exist. Null is the honest answer for a game
    /// built without one — a non-mana attack cost is then refused naming CR 508.1h, which is what
    /// this subsystem did before the handle existed, so the wiring and the call land in either
    /// order.
    /// </summary>
    [RuleRef("118.6")]
    public ICastingSystem? Casting { get; set; }
}
