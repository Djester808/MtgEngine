using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Choices;

/// <summary>
/// The kinds of question the game stops to ask. A kind is a string in the state so that a
/// pending choice survives a replay of the log, and resumption is a branch on this value —
/// never a captured continuation, which could not be replayed (CR 101.4: the game stops and
/// waits for the answer, and everything after it follows from the answer alone).
/// </summary>
public static class ChoiceKinds
{
    /// <summary>CR 514.1: discard down to maximum hand size. Raised by the turn system.</summary>
    public const string Discard = "discard";

    /// <summary>CR 704.5j: which of the duplicate legends to keep. Raised by the state-based actions.</summary>
    public const string LegendRule = "legend-rule";

    /// <summary>CR 103.5: keep this hand or take a mulligan. Raised by the turn system's opening step.</summary>
    public const string Mulligan = "mulligan";

    /// <summary>CR 103.5: after keeping, put one card per mulligan taken on the bottom. Raised here.</summary>
    public const string MulliganBottom = "mulligan-bottom";

    /// <summary>CR 603.3b: the order this player's simultaneous triggers go on the stack.</summary>
    public const string TriggerOrder = "trigger-order";

    /// <summary>CR 616.1: which applicable replacement effect applies first.</summary>
    public const string ReplacementOrder = "replacement-order";

    /// <summary>CR 608.2: an optional instruction the controller may decline.</summary>
    public const string May = "may";

    /// <summary>CR 601.2b: a number or a value chosen while an effect is being followed.</summary>
    public const string Value = "value";

    /// <summary>CR 701.23: which card a search found.</summary>
    public const string Search = "search";

    /// <summary>CR 701.22: which of the scried cards go to the bottom.</summary>
    public const string Scry = "scry";

    /// <summary>CR 601.2c: the targets chosen for a spell or ability.</summary>
    public const string Targets = "targets";

    /// <summary>The option id that keeps the opening hand, CR 103.5.</summary>
    public const string Keep = "keep";

    /// <summary>
    /// CR 510.2 and 616.1: which combat damage step a replacement-order question halted —
    /// <see cref="FirstStrikeDamage"/> or <see cref="RegularDamage"/>. A mark on the question and
    /// not a continuation, so the answer alone says where the batch has to pick up.
    /// </summary>
    public const string CombatDamageStep = "combat-damage-step";

    /// <summary>CR 510.2: the index, in that step's batch, of the hit the question was asked about.</summary>
    public const string CombatDamageIndex = "combat-damage-index";

    /// <summary>CR 510.4: the first-strike combat damage step.</summary>
    public const string FirstStrikeDamage = "first";

    /// <summary>CR 510.4: the regular combat damage step.</summary>
    public const string RegularDamage = "regular";
}

/// <summary>
/// The resumption seam before the interpreter implements it (<see cref="IResumable"/> lives in
/// <c>Engine/Seams.cs</c>), so a game that reaches a card's question says which agent owns it
/// rather than failing somewhere unrelated.
/// <para>
/// It refuses rather than throwing an unhandled exception. A question a card asked is one the
/// engine raised and cannot yet finish, and <see cref="Engine.Game.Apply"/> turns a
/// <see cref="RefusedException"/> into a <see cref="Engine.Refused"/> naming CR 101.4: the
/// answer is not taken, the question still stands, and the table is told so. Anything else
/// leaves the front door — and a table that reached a "you may" would get a bare 500 rather
/// than a game that is still waiting on it.
/// </para>
/// </summary>
[RuleRef("101.4")]
public sealed class UnbuiltResumable : IResumable
{
    public string Name => "interpreter resumption";

    public Outcome Resume(GameState state, Resumption resumption) =>
        throw new RefusedException(
            $"That {resumption?.Kind ?? "question"} is a question a card asked, and answering one is not built yet.",
            "101.4");
}
