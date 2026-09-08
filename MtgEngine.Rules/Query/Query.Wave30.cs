using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Query;

/// <summary>
/// Wave 30's readings. The plural zone rider (CR 400.1) is answered in <c>Query.cs</c> beside the
/// singular one, because every filter goes through the same two doors; the two V18 amounts are
/// here — the names among a group, counted once each (CR 201.2b), and the two numbers nothing in
/// the engine keeps yet (CR 119.3, CR 903.8), which refuse rather than answer the zero that would
/// play the card and look right.
/// </summary>
public sealed partial class Query
{
    /// <summary>
    /// "lands you control with different names", CR 201.2b with 201.2a: how many distinct names the
    /// admitted objects have between them, so two objects sharing a name are one name. Never the
    /// object count, which is <see cref="CountOf"/> and is the reading this exists to refuse.
    /// <para>
    /// CR 201.2a: an object with no name has the same name as nothing, including another nameless
    /// object, so a nameless object is counted by neither this nor a name comparison and is dropped
    /// rather than counted as a name of its own. That is the one place this differs from
    /// <see cref="GreatestSameNameCount"/>, where a nameless object is still a group of one: there
    /// the question is how large a group is, here it is how many names there are, and a nameless
    /// object has none.
    /// </para>
    /// <para>
    /// The name comes from the layers (CR 613.1), so a name an effect changed is the name counted.
    /// A card with two names at once (CR 201.3a) has no spelling in the state — an object carries
    /// one name — so nothing here can split one, and a filter with a <c>Named</c> rider is not
    /// refused as <see cref="GreatestSameNameCount"/> refuses it: "creatures named X with different
    /// names" is a sentence that answers 1 or 0 rather than a contradiction.
    /// </para>
    /// </summary>
    [RuleRef("201.2b")]
    private int DistinctNames(GameState state, DistinctNameCount count, EffectContext ctx) =>
        Candidates(state, count.Filter, ctx)
            .Select(id => _layers.Of(state, id).Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.Ordinal)
            .Count();

    /// <summary>
    /// "if an opponent lost life this turn", CR 119.3: the life the admitted players lost this turn,
    /// counted as loss and never as net change — a player who lost 3 and gained 3 lost 3 — with
    /// damage dealt to a player (CR 119.3) and life paid as a cost (CR 119.4) both adding to it.
    /// <para>
    /// Nothing in <see cref="GameState"/> keeps that number.
    /// <see cref="TurnHistory.DamageReceivedByPlayer"/> is the damage half alone, so reading it
    /// would answer "nobody lost life" after an edict, a payment or a plain lose-life effect — the
    /// card played wrongly with nothing in the log to say so. Zero is a legal answer to this
    /// question, which is exactly why it cannot be the answer to a question the state never
    /// recorded. <c>seam-requests/E5m.md</c> § 1 asks <c>State/**</c> for the per-player total, and
    /// this arm reads it the moment it exists.
    /// </para>
    /// </summary>
    [RuleRef("119.3")]
    private static RefusedException NoLifeLostThisTurn() => Unanswerable(
        nameof(LifeLostThisTurn),
        "no per-player total of the life lost this turn is kept; the damage the turn recorded is one half of it, and a payment or an edict leaves no damage at all",
        "119.3");

    /// <summary>
    /// "for each time it was cast from the command zone this game", CR 903.8: the number the
    /// commander tax is built on — per game, never reset, and counting casts from the command zone
    /// alone, so a commander cast from a hand or a graveyard adds nothing to it.
    /// <para>
    /// No folder keeps it. <see cref="TurnHistory.SpellsCast"/> carries each cast's
    /// <see cref="SpellCastRecord.From"/>, which is the right half of the shape, but the turn's
    /// history is cleared as each turn begins, so it can answer "this turn" and never "this game" —
    /// and a count taken from it would read 0 for the commander cast three times before this turn,
    /// which is the tax undercharged and the card underpaid. <c>seam-requests/E5m.md</c> § 2 asks
    /// the casting system for the per-game count, written where the cast from the command zone is
    /// announced.
    /// </para>
    /// </summary>
    [RuleRef("903.8")]
    private static RefusedException NoCommanderCastCount() => Unanswerable(
        nameof(CommanderCastsThisGame),
        "no per-game count of the casts from the command zone is kept; the turn's history is cleared each turn and cannot answer a per-game question",
        "903.8");
}
