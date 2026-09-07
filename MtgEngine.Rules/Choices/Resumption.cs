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
}

/// <summary>
/// An answered question handed back to whoever asked it. Everything the asker needs is here:
/// the kind it asked under, who answered, the options they chose (not just their ids, so the
/// object or player behind each one travels with it) and the <see cref="ChoiceContext"/> that
/// located the asking — the source, the ability index and the path inside the effect tree.
/// </summary>
public sealed record Resumption(
    string Kind,
    PlayerId Player,
    ImmutableList<ChoiceOption> Chosen,
    ChoiceContext Context);

/// <summary>
/// The seam a choice resumes through when the question came from a card rather than from a
/// rule the choice system performs itself. The interpreter implements it: it is the only
/// subsystem that knows what an effect was in the middle of doing, and the
/// <see cref="ChoiceContext"/> is how it finds that place again after the answer.
/// </summary>
public interface IResumable : ISubsystem
{
    Outcome Resume(GameState state, Resumption resumption);
}

/// <summary>
/// The resumption seam before the interpreter lands, so a game that reaches a card's question
/// says which agent owns it rather than failing somewhere unrelated.
/// </summary>
public sealed class UnbuiltResumable : IResumable
{
    public string Name => "interpreter resumption";

    public Outcome Resume(GameState state, Resumption resumption) =>
        throw new NotImplementedException("interpreter resumption is not built yet: wave 2, agent E9.");
}
