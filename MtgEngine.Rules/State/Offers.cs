using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;

namespace MtgEngine.Rules.State;

/// <summary>
/// A standing offer to cast one card from a hand for nothing (CR 601.2b).
/// </summary>
/// <remarks>
/// **This is the offer-a-cast family's other shape, and the difference from
/// <see cref="GameObject.MayCastFree"/> is that this one has to be spent.** That flag is a fact
/// about one particular object - a cascaded card in exile, a maddened card in a graveyard - and
/// there is exactly one card wearing it, so taking the offer and taking it twice are the same
/// action. "You may cast a spell with mana value 3 or less from your hand without paying its
/// mana cost" names no card at all. The player chooses one when they cast, which means the
/// permission cannot live on a card: written onto every card in hand that answers the
/// description, it would buy as many casts as the hand had answers, and an Expertise that casts
/// two spells is a strictly better card than the printed one - the direction coverage scores as
/// a win.
/// <para>
/// So it lives here, on the game, with a count of what is left. The shape is
/// <see cref="PreventionEffect.OnlyOnce"/>'s: a permission that ends on something that happened
/// rather than on a duration, recorded by an event emitted by the action that used it, because
/// the state is a fold of the log and "the offer has been taken" is not derivable from the cast
/// alone.
/// </para>
/// <para>
/// It ends two ways and both are events. <see cref="Events.HandCastOfferSpent"/> is the cast
/// taking it; <see cref="Events.HandCastOfferLapsed"/> is the offered player passing priority,
/// which is where every other offer in this family lapses and for the same reason - the printed
/// sentence gives the window inside the resolution, and the next pass is as close as an engine
/// that cannot cast mid-resolution gets to it. An offer that could not be ended either way would
/// be a permanent Omniscience, so there is no third outcome and no duration to forget to sweep.
/// </para>
/// </remarks>
public sealed record HandCastOffer
{
    public required Guid Id { get; init; }

    /// <summary>Whose hand it is, and who may take it.</summary>
    /// <remarks>
    /// An identity rather than a description, for the reason <see cref="PreventionEffect.Source"/>
    /// is one: the player was decided when the spell resolved, and re-deriving them from a
    /// description would move the offer when the board moved.
    /// </remarks>
    public required Guid PlayerId { get; init; }

    /// <summary>A filter the spell has to answer - "instant|sorcery", or null for any.</summary>
    /// <remarks>
    /// Null is "a spell", which is what Maelstrom Archangel and Omnispell Adept's wider siblings
    /// say. A qualifier the filter vocabulary cannot name leaves the sentence unread rather than
    /// widening to null - an offer that covers more spells than the card names is the same
    /// strictly-better-than-printed mistake the count exists to stop.
    /// </remarks>
    public string? SpellFilter { get; init; }

    /// <summary>The most a taken spell may cost, or null for no cap (CR 202.3).</summary>
    /// <remarks>
    /// "With mana value 3 or less" is the commonest printing of this sentence by some way - the
    /// Expertise cycle is five cards of it. A literal only: the printings that read the cap off
    /// the spell's own X or off a board count leave the sentence unread, because a cap that was
    /// not understood must not become no cap at all.
    /// </remarks>
    public int? MaxManaValue { get; init; }

    /// <summary>Whether this offer covers the card being cast (CR 601.2b).</summary>
    /// <remarks>
    /// Asked of the printed card, exactly as every other question about something that is not
    /// yet a permanent is asked: the object is in a hand, and CR 613's layers describe
    /// permanents.
    /// </remarks>
    public bool Covers(CardDefinition casting)
    {
        ArgumentNullException.ThrowIfNull(casting);

        if (MaxManaValue is { } cap && casting.Cmc > cap)
            return false;

        return SpellFilter is not { } filter || SearchFilters.Matches(filter, casting);
    }
}
