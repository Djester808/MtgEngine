using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Choices;

/// <summary>
/// The resumption seam before the interpreter implements it (<see cref="IResumable"/> lives in
/// <c>Engine/Seams.cs</c>), so a game that reaches a card's question says which agent owns it
/// rather than failing somewhere unrelated. The kinds themselves are in
/// <see cref="ChoiceKinds"/>, one file over.
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
            resumption is null
                ? "That question is one a card asked, and answering one is not built yet."
                : $"That {resumption.Kind} question is one a card asked, and answering one is not built yet.",
            "101.4");
}
