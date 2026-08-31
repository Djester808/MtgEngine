using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Abilities;

/// <summary>
/// A question a printed condition asks about the board (CR 109.5).
/// </summary>
/// <param name="state">The board the question is asked of.</param>
/// <param name="abilities">
/// What the game knows about abilities, so a condition about a <em>characteristic</em> gets the
/// answer CR 613 gives rather than the printed one.
/// </param>
/// <param name="source">The permanent, spell or ability the condition is printed on.</param>
/// <param name="subject">
/// The seat the surrounding text has already named — "that player" — or null when it named
/// nobody.
/// </param>
/// <remarks>
/// A named delegate rather than the <c>Func</c> this used to be, because the fourth argument is
/// the whole point and a positional <c>Guid?</c> on the end of a <c>Func</c> is invisible at every
/// call site. A condition is very often about a <em>particular</em> player the sentence around it
/// picked out - "at the beginning of each opponent's upkeep, if that player has one or fewer cards
/// in hand" - and this signature had no seat at all, so the word could not be expressed even where
/// it was understood.
/// <para>
/// <strong>A null seat is not the controller.</strong> Every reader below that reads a subject
/// naming one goes through <c>BoardConditions.Seats</c>, which yields nothing at all when
/// the seat is unknown - and an empty set of seats answers false whether the question was "any of
/// them" or "all of them". A condition quietly re-aimed at whoever controls the card is the defect
/// this parameter exists to make impossible: it reads perfectly and asks about the wrong player.
/// </para>
/// </remarks>
public delegate bool BoardCondition(
    GameState state, IAbilitySource abilities, GameObject source, Guid? subject);
