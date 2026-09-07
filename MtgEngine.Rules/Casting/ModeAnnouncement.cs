using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Casting;

/// <summary>
/// The modes of a modal spell or ability as they are announced, CR 700.2: its controller chooses
/// them as part of casting the spell (CR 601.2b, 700.2a), activating the ability (CR 602.2b,
/// 700.2a) or putting the triggered ability on the stack (CR 603.3c, 700.2b), and the count is
/// held to the ceiling the card prints. A ceiling that depends on the game as the spell is cast
/// (<see cref="ChooseMode.MaxIf"/> with <see cref="ChooseMode.MaxIfMet"/>) is read here, once, and
/// the number it came to rides on the stack entry under <see cref="CeilingKey"/>, so nothing later
/// reads the condition again: a commander that leaves before resolution changes nothing, and a
/// copy copies the modes chosen (CR 700.2g, 707.10).
/// </summary>
[RuleRef("700.2")]
public static class ModeAnnouncement
{
    /// <summary>
    /// Where the ceiling read at announcement rides on <see cref="StackEntry.Chosen"/>, beside the
    /// interpreter's "modes" key. Recorded only when the card's ceiling is conditional, so an
    /// ordinary modal spell's entry is what it always was; the interpreter's mode question is what
    /// reads it when the announcement named no modes.
    /// </summary>
    public const string CeilingKey = "modes.max";

    /// <summary>
    /// CR 700.2 with 601.2b: the modes announced for the spell or ability whose text is
    /// <paramref name="bodies"/>, held to the printed bounds, with a conditional ceiling read now.
    /// An announcement naming no modes leaves them to the resolution's question, as it does today,
    /// but the ceiling is still read and recorded here so that question offers the number this
    /// announcement allowed. Null when the text is not modal or its ceiling is not conditional.
    /// </summary>
    /// <exception cref="RefusedException">
    /// CR 700.2 when the count is outside the bounds or an index names no mode; CR 700.2d when a
    /// mode is chosen twice and the card does not allow it.
    /// </exception>
    [RuleRef("700.2")]
    public static ImmutableDictionary<string, string>? Announce(GameState state, IQuery query, IEnumerable<Effect> bodies, EffectContext context, IReadOnlyList<int> modes)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        ArgumentNullException.ThrowIfNull(modes);
        var modal = bodies.Select(ModalIn).FirstOrDefault(m => m is not null);
        if (modal is null)
            return null;

        var ceiling = Ceiling(state, query, modal, context);
        if (modes.Count > 0)
            Require(modal, ceiling, modes);
        return modal.MaxIf is null
            ? null
            : ImmutableDictionary<string, string>.Empty.Add(CeilingKey, ceiling.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The most modes this announcement may choose, CR 700.2: <see cref="ChooseMode.MaxIfMet"/>
    /// while <see cref="ChooseMode.MaxIf"/> holds in the announcing player's context, otherwise
    /// <see cref="ChooseMode.Max"/>. The condition is read here and nowhere else (CR 601.2b).
    /// </summary>
    [RuleRef("601.2b")]
    public static int Ceiling(GameState state, IQuery query, ChooseMode modal, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(modal);
        return modal is { MaxIf: { } test, MaxIfMet: { } met } && query.Holds(state, test, context) ? met : modal.Max;
    }

    /// <summary>
    /// The bulleted list CR 700.2 describes: the <see cref="ChooseMode"/> at the root of the
    /// ability's text or among a <see cref="Sequence"/>'s steps. A modal inside an <see cref="If"/>,
    /// a <see cref="May"/> or any other branch runs only if that branch does, which resolution
    /// decides (CR 608.2c), so it is not this announcement's to count and the resolution asks it
    /// then, as it always has.
    /// </summary>
    [RuleRef("700.2")]
    public static ChooseMode? ModalIn(Effect? effect) => effect switch
    {
        ChooseMode modal => modal,
        Sequence sequence => sequence.Steps.Select(ModalIn).FirstOrDefault(m => m is not null),
        _ => null,
    };

    /// <summary>
    /// CR 700.2: the count within the bounds and every index a mode; CR 700.2d: no mode twice
    /// unless the card says a mode may be chosen more than once.
    /// </summary>
    [RuleRef("700.2")]
    private static void Require(ChooseMode modal, int ceiling, IReadOnlyList<int> modes)
    {
        if (modes.Count > ceiling)
            throw new RefusedException("More modes were chosen than this allows.", "700.2");
        if (modes.Count < modal.Min)
            throw new RefusedException("Too few modes were chosen.", "700.2");
        if (modes.Any(m => m < 0 || m >= modal.Modes.Count))
            throw new RefusedException("That is not one of the modes.", "700.2");
        if (!modal.SameModeTwice && modes.Distinct().Count() != modes.Count)
            throw new RefusedException("The same mode cannot be chosen twice.", "700.2d");
    }
}
