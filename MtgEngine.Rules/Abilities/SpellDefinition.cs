using System.Collections.Immutable;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Abilities;

/// <summary>
/// What a card does when it is cast: what it targets, and what happens on resolution.
/// </summary>
/// <remarks>
/// A permanent spell needs none of this — it resolves by becoming a permanent (CR 608.3) — so
/// only instants, sorceries, and permanents with an ETB-style effect need a definition at all.
/// </remarks>
public sealed record SpellDefinition
{
    /// <summary>A definition carrying nothing, to compare candidates against.</summary>
    private static readonly SpellDefinition Nothing = new();

    /// <summary>
    /// Read once. Reflection is what makes this check honest, and it is also what makes it worth
    /// caching - the compiler asks it of every card in a thirty-thousand card corpus.
    /// </summary>
    private static readonly System.Reflection.PropertyInfo[] Fields =
        typeof(SpellDefinition).GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

    /// <summary>Whether this definition says anything at all about how the card is cast.</summary>
    /// <remarks>
    /// A card with no spell-level text gets no definition, and something has to decide which
    /// cards those are. That question used to be a hand-written list of every field - "dash is
    /// null and evoke is null and ..." - and a field left off the list was not a compile error.
    /// It was a cost that was read, assigned, and thrown away, on a card that reported itself
    /// fully compiled. Blitz was lost that way, and the surge/spectacle cost had the same hole
    /// hidden behind an unrelated term that happened to be true for every real card carrying it.
    /// <para>
    /// So the question is asked of the type instead of of a list. A property added to this record
    /// is included the moment it exists, and there is no edit anybody can forget to make.
    /// </para>
    /// </remarks>
    public static bool CarriesNothing(SpellDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        foreach (var field in Fields)
        {
            var carried = field.GetValue(definition);

            // Collections are compared by length rather than by value: two distinct empty
            // ImmutableLists are not Equals to each other, and every definition builds its own.
            if (carried is System.Collections.ICollection list)
            {
                if (list.Count > 0)
                    return false;

                continue;
            }

            if (!Equals(carried, field.GetValue(Nothing)))
                return false;
        }

        return true;
    }

    /// <summary>Targets, in the order the effects index them (CR 601.2c).</summary>
    public ImmutableList<TargetSpec> Targets { get; init; } = [];

    /// <summary>What happens on resolution, in order (CR 608.2c).</summary>
    public ImmutableList<IEffect> Effects { get; init; } = [];

    /// <summary>An additional cost beyond the mana cost printed on the card (CR 601.2f).</summary>
    public ManaCostSpec? AlternateCost { get; init; }

    /// <summary>
    /// Costs paid by moving cards the caster chooses (CR 601.2f-h).
    /// </summary>
    /// <remarks>
    /// "As an additional cost to cast this spell, sacrifice a creature." The same list an
    /// activated ability carries, and read by the same code — an additional cost on a spell and a
    /// cost line on an ability are the same rule (CR 601.2f applies to both), and the compiler had
    /// been able to read one for a while without anywhere to put the other.
    /// <para>
    /// Mandatory, unlike kicker: a spell with an unpayable additional cost cannot be cast at all
    /// (CR 601.2h), which is why this is checked before anything is spent rather than on
    /// resolution.
    /// </para>
    /// </remarks>
    public ImmutableList<ChosenCost> ChosenCosts { get; init; } = [];

    /// <summary>
    /// The optional additional cost bargain charges, when the card has it (CR 702.166a).
    /// </summary>
    /// <remarks>
    /// Apart from <see cref="ChosenCosts"/> because those are charged every time and this one is
    /// charged only when the caster says so - which is the whole of what "bargained" means
    /// (CR 702.166b). Kicker's shape with a sacrifice instead of mana.
    /// </remarks>
    public ChosenCost? BargainCost { get; init; }

    /// <summary>
    /// How much generic mana to knock off this spell's cost, counted as it is cast (CR 601.2f).
    /// </summary>
    /// <remarks>
    /// Affinity, and every "this spell costs {1} less to cast for each ..." beside it. Counted at
    /// cast time rather than stored, because the number changes with the board — a cost reduction
    /// worked out when the card was drawn would be wrong by the time it was cast.
    /// <para>
    /// It reduces the generic part only, which is not a simplification but the rule: cost
    /// reductions never remove coloured mana requirements (CR 601.2f), so a spell costing
    /// {3}{U}{U} with three artifacts out still costs {U}{U}.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Handed the chosen targets as well as the board, because a discount can turn on them:
    /// "costs {2} less to cast if it targets a tapped creature" is answerable only once the
    /// targets are known. They always are by the time this is asked - CR 601.2c chooses targets
    /// before CR 601.2f works out the cost, which is the order the engine follows too.
    /// </remarks>
    public Func<GameState, Guid, IReadOnlyList<Target>, int>? CostReduction { get; init; }

    /// <summary>
    /// What turning this permanent face up costs, when it has morph (CR 702.37a).
    /// </summary>
    /// <remarks>
    /// Kept on the spell definition because morph is a property of the card rather than of any
    /// one permanent: it says both that the card may be cast face down for {3} and what turning
    /// it up later costs. The engine reads it from the card underneath a face-down permanent,
    /// which is the one thing that is still allowed to look at that card.
    /// </remarks>
    public ManaCostSpec? MorphCost { get; init; }

    /// <summary>
    /// An optional extra cost that returns the card to hand instead of the graveyard (CR 702.27a).
    /// </summary>
    /// <remarks>
    /// Kicker's shape with a different reward: paid or not paid as the spell is cast, and what it
    /// buys happens at the very end of the resolution rather than during it.
    /// </remarks>
    public ManaCostSpec? BuybackCost { get; init; }

    /// <summary>
    /// What casting this costs instead of its mana cost, in exchange for losing it (CR 702.74a).
    /// </summary>
    public ManaCostSpec? EvokeCost { get; init; }

    /// <summary>
    /// An optional cost that copies this spell when it is paid (CR 702.78a, 702.153a).
    /// </summary>
    /// <remarks>
    /// Conspire taps two creatures sharing a colour with the spell; casualty sacrifices one big
    /// enough. They are the same mechanism under two names, so they share a field rather than
    /// each getting one - what differs is only which cards may be offered.
    /// <para>
    /// Not in <see cref="ChosenCosts"/>, which is charged on every cast: this is a choice made
    /// as the spell is cast, and a controller who declines casts normally.
    /// </para>
    /// </remarks>
    public ChosenCost? CopyingCost { get; init; }

    /// <summary>
    /// Whether nobody may respond to this while it is on the stack (CR 702.18a).
    /// </summary>
    /// <remarks>
    /// A property of the spell rather than a keyword on the permanent, because it only ever means
    /// anything while the card is a spell - and what it restricts is everybody else, not the card
    /// it is printed on.
    /// </remarks>
    public bool HasSplitSecond { get; init; }

    /// <summary>When this spell may be cast, beyond its ordinary timing (CR 601.3e).</summary>
    /// <remarks>
    /// A restriction on top of the type's own timing rather than a replacement for it: an instant
    /// that says "cast this only during combat" is still an instant, and a sorcery that says it
    /// is still bound by sorcery timing as well. Null means no extra restriction, which is almost
    /// every card.
    /// </remarks>
    public Func<GameState, Guid, bool>? CastOnlyWhen { get; init; }

    /// <summary>
    /// What casting this as an Aura costs (CR 702.103a) - bestow.
    /// </summary>
    /// <remarks>
    /// The mirror of overload: overload takes a spell's target away, bestow gives it one. A card
    /// with bestow is a creature spell as printed and an Aura spell when this cost is paid, and
    /// which it was decides what it targets and what it becomes on the battlefield.
    /// </remarks>
    public ManaCostSpec? BestowCost { get; init; }

    /// <summary>What a bestowed casting enchants, which a normal one does not target.</summary>
    public TargetSpec? BestowTarget { get; init; }

    /// <summary>
    /// What casting this as a mutating creature spell costs (CR 702.140a).
    /// </summary>
    /// <remarks>
    /// Bestow's shape one keyword along: an alternative cost that also gives the spell a target
    /// it does not otherwise have, and changes what it becomes when it resolves. Where bestow
    /// makes the card an Aura on the battlefield, this stops it reaching the battlefield at all —
    /// it merges with what it targeted and the two are one permanent afterwards (CR 702.140c).
    /// </remarks>
    public ManaCostSpec? MutateCost { get; init; }

    /// <summary>What a mutating casting merges with, which a normal one does not target.</summary>
    /// <remarks>
    /// "A non-Human creature with the same owner as this spell" (CR 702.140a). The owner half is
    /// a <see cref="TargetSpec.SourceFilter"/> rather than an object filter because it is a
    /// question about the spell as well as the candidate, and only the caller that knows which
    /// spell is asking can answer it.
    /// </remarks>
    public TargetSpec? MutateTarget { get; init; }

    /// <summary>What casting this for its overload cost costs (CR 702.96a).</summary>
    public ManaCostSpec? OverloadCost { get; init; }

    /// <summary>
    /// What it does when overloaded, which is the same thing to everything (CR 702.96a).
    /// </summary>
    /// <remarks>
    /// A second effect list rather than a rewrite of the first, because overload does not change
    /// what a spell does - it changes who it does it to, from one chosen thing to every thing
    /// that answers the description. The two lists come from the same printed sentences read two
    /// ways, and the spell records which reading was paid for.
    /// </remarks>
    public ImmutableList<IEffect> OverloadEffects { get; init; } = [];

    /// <summary>What casting this for its awaken cost costs (CR 702.113a).</summary>
    public ManaCostSpec? AwakenCost { get; init; }

    /// <summary>What casting this for its sneak cost costs in mana (CR 702.190a).</summary>
    /// <remarks>
    /// Only half the price. The rest is <see cref="SneakReturn"/>, and the two are paid together
    /// or not at all - which is why the mana cost alone would be a discount rather than an
    /// alternative cost.
    /// </remarks>
    public ManaCostSpec? SneakCost { get; init; }

    /// <summary>
    /// The attacker given up to pay a sneak cost (CR 702.190a).
    /// </summary>
    /// <remarks>
    /// Ninjutsu's cost on a spell instead of an activated ability, and it buys the same thing:
    /// what the returned creature was attacking is what the permanent this becomes arrives
    /// attacking (CR 702.190b).
    /// </remarks>
    public ChosenCost? SneakReturn { get; init; }

    /// <summary>
    /// The optional additional cost teamwork charges (CR 702.194a).
    /// </summary>
    /// <remarks>
    /// "You may tap any number of creatures you control with total power N or more" - crew's cost
    /// offered rather than demanded, which is why it is kept apart from
    /// <see cref="ChosenCosts"/> the same way bargain's is: those are charged every time and this
    /// one only when the caster says so.
    /// <para>
    /// What paying it buys is on the card's other lines - "if this spell was cast using teamwork,
    /// ..." - and those sentences are not read yet, so <see cref="GameObject.WasTeamwork"/> is
    /// recorded and nothing consults it. The alternative was to leave the keyword unread, which
    /// would be reading a cost as though the card did not offer it.
    /// </para>
    /// </remarks>
    public ChosenCost? TeamworkCost { get; init; }

    /// <summary>
    /// The land the awaken half animates, targeted only when awaken was paid (CR 702.113b).
    /// </summary>
    /// <remarks>
    /// Added to the spell's own targets as it is cast, exactly as bestow's is, and added
    /// <em>last</em> so that <see cref="AwakenEffects"/> can find it as the final entry however
    /// many the card printed. The rule is emphatic that it is not a target otherwise: a spell
    /// cast for its printed cost "is cast as if it didn't have that target", so nothing about it
    /// can be countered for having no legal target.
    /// </remarks>
    public TargetSpec? AwakenTarget { get; init; }

    /// <summary>
    /// What the awaken half does, on top of everything the card already says (CR 702.113a).
    /// </summary>
    /// <remarks>
    /// Added to the printed effects rather than replacing them, which is the difference from
    /// overload: an awakened spell still does what it says, and then puts counters on a land and
    /// stands it up. Its effects index into their own one-target slice, the way a mode's do.
    /// </remarks>
    public ImmutableList<IEffect> AwakenEffects { get; init; } = [];

    /// <summary>
    /// What one extra copy of this spell costs (CR 702.55a) - replicate.
    /// </summary>
    /// <remarks>
    /// Unlike every other additional cost, this one is paid a number of times the caster chooses,
    /// and the number is what the ability does rather than a condition on it.
    /// </remarks>
    public ManaCostSpec? ReplicateCost { get; init; }

    /// <summary>
    /// What paying for a small copy of this creature costs (CR 702.171a) - offspring.
    /// </summary>
    public ManaCostSpec? OffspringCost { get; init; }

    /// <summary>What plotting this costs (CR 702.169a).</summary>
    public ManaCostSpec? PlotCost { get; init; }

    /// <summary>What suspending this costs, and how long the wait is (CR 702.62a).</summary>
    /// <remarks>
    /// Not an alternative way to cast it: suspending is a special action that exiles the card,
    /// and the cast comes later and for nothing. The two halves are far enough apart in time that
    /// they cannot share the machinery every other alternative cost uses.
    /// </remarks>
    public ManaCostSpec? SuspendCost { get; init; }

    /// <summary>
    /// What it costs on top to cast this as though it had flash, if the card offers that.
    /// </summary>
    /// <remarks>
    /// Not an alternative cost: the printed cost is still paid and this is added to it, which is
    /// why it cannot go through the machinery warp and flashback share. What it buys is timing.
    /// </remarks>
    public ManaCostSpec? FlashSurcharge { get; init; }

    /// <summary>
    /// The alternative cost a prototype card may be cast for, if it has one (CR 718.2).
    /// </summary>
    /// <summary>
    /// What this card may be cast for when it is revealed as it is drawn (CR 702.94a).
    /// </summary>
    public ManaCostSpec? MiracleCost { get; init; }

    public ManaCostSpec? PrototypeCost { get; init; }

    /// <summary>The alternative power a prototyped spell has, if it was cast that way.</summary>
    public int? PrototypePower { get; init; }

    /// <summary>The alternative toughness a prototyped spell has.</summary>
    public int? PrototypeToughness { get; init; }

    /// <summary>How many time counters suspending puts on (CR 702.62a).</summary>
    public int SuspendCount { get; init; }

    /// <summary>
    /// What casting this costs instead of its mana cost, in exchange for keeping it (CR 702.109a).
    /// </summary>
    /// <remarks>
    /// An alternative cost rather than an additional one, which is the difference from
    /// <see cref="BuybackCost"/>: dash replaces the mana cost, buyback is added to it.
    /// </remarks>
    public ManaCostSpec? DashCost { get; init; }

    /// <summary>
    /// What casting this costs in exchange for keeping it only until the end of the turn
    /// (CR 702.152a).
    /// </summary>
    /// <remarks>
    /// Dash's shape with a harsher end and a consolation: the permanent is sacrificed rather than
    /// returned to hand, and drawing a card when it dies is what pays for that. Both belong to the
    /// permanent, which is why the fact that this cost was paid has to survive the spell.
    /// </remarks>
    public ManaCostSpec? BlitzCost { get; init; }

    /// <summary>
    /// A kicker cost that may be paid any number of times (CR 702.33c).
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="KickerCost"/> because the two are asked differently: one is a
    /// yes-or-no question at cast time and the other is a number. A card printing both does not
    /// exist, and if one did, this shape would refuse it rather than silently pick.
    /// </remarks>
    public ManaCostSpec? MultikickerCost { get; init; }

    /// <summary>
    /// A cost the card offers instead of its mana cost, but only while the game says so
    /// (CR 601.2f).
    /// </summary>
    /// <remarks>
    /// Surge and spectacle are one shape printed twice: an alternative cost, and a question about
    /// the board that decides whether it is on offer at all. Dash and evoke need no such record
    /// because their offer is unconditional - the cost is always available and only the mana
    /// decides. The two halves are kept together because a cost that could be taken when its
    /// condition is false is not a discount, it is a different card.
    /// <para>
    /// The keyword and its rule travel with the cost so that a refusal can name what was refused.
    /// "Boulder Salvo cannot be cast for its surge cost" tells the player which permission they
    /// reached for; "cost not available" does not.
    /// </para>
    /// </remarks>
    public ConditionalCost? ConditionalAlternativeCost { get; init; }

    /// <summary>
    /// What casting this costs once it has been foretold (CR 702.143a).
    /// </summary>
    /// <remarks>
    /// Not an <see cref="AlternativeCastZone"/>, though it looks like one: flashback's permission
    /// belongs to the card and applies whenever the card is in that zone, while this one belongs
    /// to a particular exiled card that somebody paid {2} to put there. The card being in exile
    /// is not enough — it has to have been foretold, and on an earlier turn.
    /// </remarks>
    public ManaCostSpec? ForetellCost { get; init; }

    /// <summary>
    /// What casting this costs after it has been discarded (CR 702.35a).
    /// </summary>
    /// <remarks>
    /// The printed string as well as the parsed cost, because the offer that madness makes
    /// carries its cost to the exiled card and back through the log, where a parsed cost cannot
    /// go without breaking replay equality.
    /// </remarks>
    public string? MadnessCost { get; init; }

    /// <summary>
    /// Whether this spell has delve — graveyard cards may pay for generic mana (CR 702.66a).
    /// </summary>
    /// <remarks>
    /// Convoke's shape, with a different currency: the exiled cards do not reduce the cost, they
    /// <em>pay</em> part of it, which is why the caster names them as they cast rather than the
    /// engine working out a number. It matters for anything that asks what the spell cost.
    /// </remarks>
    public bool HasDelve { get; init; }

    /// <summary>
    /// Whether turning it face up puts a +1/+1 counter on it — megamorph (CR 702.37e).
    /// </summary>
    /// <remarks>
    /// The only difference between morph and megamorph, which is why it is a flag beside the cost
    /// rather than a second mechanic. Reading megamorph as plain morph would have compiled and
    /// played and been quietly wrong by exactly one counter.
    /// </remarks>
    public bool MorphAddsCounter { get; init; }

    /// <summary>
    /// What the face-down permanent wards for, when the card has disguise (CR 702.168a).
    /// </summary>
    /// <remarks>
    /// Disguise is morph whose face-down side has ward. It is the one thing a face-down permanent
    /// has besides being a 2/2 — an exception written into the keyword itself, not something the
    /// card underneath grants, which is why the engine has to be told about it separately from
    /// the card's own abilities.
    /// </remarks>
    public ManaCostSpec? FaceDownWard { get; init; }

    /// <summary>
    /// The modes this spell offers, if it is modal (CR 700.2).
    /// </summary>
    /// <remarks>
    /// Each mode carries its own targets and effects, and the effects index into <em>their own
    /// mode's</em> targets rather than into one flat list — because which targets exist depends on
    /// which modes were chosen, and modes are chosen before targets are (CR 601.2b before
    /// 601.2c). A flat list would renumber itself depending on the choice.
    /// </remarks>
    public ImmutableList<SpellMode> Modes { get; init; } = [];

    /// <summary>How many modes must be chosen (CR 700.2d). Zero when the spell is not modal.</summary>
    public int ModesToChoose { get; init; }

    /// <summary>
    /// Whether the same mode may be taken more than once (CR 700.2d).
    /// </summary>
    /// <remarks>
    /// The default is that it may not, and that default is a rule rather than a convenience -
    /// which is why the permission has to be printed to exist. "Choose three. You may choose the
    /// same mode more than once" is a different card from "choose three": the second cannot aim
    /// nine damage at one creature and the first can.
    /// <para>
    /// Nothing else changes. CR 700.2d says a mode chosen twice is treated as though it appeared
    /// twice in sequence, which is exactly what the resolution loop already does with a repeated
    /// index — each occurrence gets its own slice of the chosen targets and runs its own effects.
    /// </para>
    /// </remarks>
    public bool ModesMayRepeat { get; init; }

    /// <summary>
    /// The most modes that may be chosen, when the card offers a range (CR 700.2d).
    /// </summary>
    /// <remarks>
    /// "Choose one or both" and "choose one or more" are as common as they are because they read
    /// as flavour text, and they are not: they are a different number. Held as a maximum beside
    /// the minimum rather than as a flag, because the two wordings differ only in what the
    /// maximum is - two, or every mode the card has.
    /// <para>
    /// Zero means the spell takes exactly <see cref="ModesToChoose"/>, which is what almost every
    /// modal card says.
    /// </para>
    /// </remarks>
    public int ModesMax { get; init; }

    /// <summary>
    /// A wider mode allowance the card offers only while the board says so (CR 700.2d).
    /// </summary>
    /// <remarks>
    /// "Choose one. If you control a commander as you cast this spell, you may choose both
    /// instead." The maximum is not a property of the card alone, so it cannot live in
    /// <see cref="ModesMax"/>: a card compiled with a maximum of two would let anybody take both
    /// modes, which is the strictly better card, and one compiled with a maximum of one would
    /// never offer what it prints.
    /// </remarks>
    public ConditionalModes? ExtraModes { get; init; }

    /// <summary>
    /// What choosing every mode costs on top of the mana cost (CR 702.42a) - entwine.
    /// </summary>
    public ManaCostSpec? EntwineCost { get; init; }

    /// <summary>
    /// What each extra copy of this creature costs, paid any number of times (CR 702.157a).
    /// </summary>
    /// <remarks>
    /// Squad, and it is replicate's shape one zone along: replicate buys another copy of the
    /// spell, squad buys a token copy of the permanent the spell becomes. So the count is
    /// charged the same way and spent in a different place.
    /// </remarks>
    public ManaCostSpec? SquadCost { get; init; }

    /// <summary>
    /// Whether another player may pay part of the generic cost (CR 702.132a) - assist.
    /// </summary>
    /// <remarks>
    /// A flag rather than a cost, because assist charges nothing: it changes who is allowed to
    /// pay what the spell already costs.
    /// </remarks>
    public bool HasAssist { get; init; }

    /// <summary>
    /// What adding this card's text to an Arcane spell costs (CR 702.47a) - splice.
    /// </summary>
    /// <remarks>
    /// The card itself is never cast when it is spliced: it is revealed from hand, paid for, and
    /// its <see cref="Effects"/> are appended to somebody else's spell. So a splice card needs
    /// its own effects compiled exactly as if it were being cast, which is why this is a cost on
    /// the spell definition rather than a separate kind of object.
    /// </remarks>
    public ManaCostSpec? SpliceCost { get; init; }

    /// <summary>
    /// What each mode beyond the first costs on top of the mana cost (CR 702.101a) - escalate.
    /// </summary>
    /// <remarks>
    /// Charged per extra mode rather than once, which is the whole difference from entwine: an
    /// escalate card with four modes can be cast for one, two, three or all four, and each step
    /// up is paid for separately.
    /// </remarks>
    public ManaCostSpec? EscalateCost { get; init; }

    /// <summary>
    /// An optional additional cost, paid or not as the spell is cast (CR 702.33a).
    /// </summary>
    /// <remarks>
    /// Kicker. Not an alternative cost — the mana cost is still paid in full and this is paid on
    /// top — and not a chosen cost either, because nothing is chosen but yes or no. Like the
    /// modes, the decision arrives with the cast rather than being asked for: it is part of
    /// casting (CR 601.2b), so the player decides before committing.
    /// </remarks>
    public ManaCostSpec? KickerCost { get; init; }

    /// <summary>
    /// A cost reduction paid by tapping permanents as the spell is cast (CR 702.51a, 702.56a).
    /// </summary>
    /// <remarks>
    /// Convoke and improvise. Not an alternative cost and not an additional one: the cost is
    /// unchanged and some of it is paid with something other than mana, which is why it takes the
    /// same <see cref="ChosenCost"/> the activated abilities use rather than a mechanism of its
    /// own. The permanents arrive with the cast for the same reason a sacrifice does — paying is
    /// part of an action the player is taking, so they can choose before they commit.
    /// </remarks>
    public TapToPay? TapToPayCost { get; init; }

    /// <summary>
    /// A way to cast this card from somewhere other than hand, for a different cost (CR 702.34a).
    /// </summary>
    /// <remarks>
    /// Flashback. Two static abilities in one field: permission to cast from the graveyard for
    /// the stated cost, and the requirement that the card be exiled afterwards rather than going
    /// back to the graveyard. The second half is what stops it being cast again, and leaving it
    /// out would turn every flashback card into an engine.
    /// </remarks>
    public AlternativeCastZone? CastFrom { get; init; }

}

/// <summary>One mode of a modal spell (CR 700.2).</summary>
/// <param name="Text">What the bullet says, for the button that offers it.</param>
/// <param name="Targets">What this mode targets, chosen only if this mode is chosen.</param>
/// <param name="Effects">What this mode does, indexed into its own targets.</param>
public sealed record SpellMode(
    string Text, ImmutableList<TargetSpec> Targets, ImmutableList<IEffect> Effects)
{
    /// <summary>
    /// What choosing this mode costs on top of the spell's own cost (CR 700.2h) - spree.
    /// </summary>
    /// <remarks>
    /// On the mode rather than on the spell, which is the whole difference between spree and
    /// escalate: escalate charges one flat price for every mode past the first, so the spell can
    /// hold it, while a spree card's modes each name their own price and what is owed depends on
    /// which ones were taken.
    /// </remarks>
    public ManaCostSpec? Cost { get; init; }
}

/// <summary>
/// A larger number of modes a card offers only under a condition (CR 700.2d).
/// </summary>
/// <remarks>
/// Shaped like <see cref="ConditionalCost"/> and for the same reason: the permission and the
/// question that gates it are one fact, and a maximum that could be taken while its condition is
/// false is not a bonus, it is a different card. The rule travels with it so a refusal can name
/// what was reached for.
/// </remarks>
/// <param name="Max">The most modes that may be chosen while the condition holds.</param>
/// <param name="Rule">The rule the wider allowance comes from, cited in a refusal.</param>
/// <param name="IsAvailable">Whether the game currently permits it.</param>
public sealed record ConditionalModes(
    int Max, string Rule, Func<GameState, IAbilitySource, Guid, bool> IsAvailable);

/// <summary>Permission to cast a card from another zone for another cost (CR 702.34a).</summary>
/// <param name="Zone">Where it may be cast from.</param>
/// <param name="Cost">What is paid instead of the mana cost.</param>
/// <param name="ExileOnResolve">
/// Whether the card is exiled as it leaves the stack rather than going to the graveyard. True for
/// flashback, and the reason a flashback card can only be cast this way once.
/// </param>
/// <param name="OnlyIfDiscardedThisTurn">
/// Whether the permission depends on the card having been discarded this turn. Mayhem's, and the
/// half of it that matters: without the condition the card could be cast out of the graveyard at
/// any time ever after, which is a strictly better card than the one printed.
/// </param>
/// <param name="Extra">
/// Costs paid only when the spell is cast this way (CR 702.82a). Retrace's land discard is one:
/// the same card cast from hand costs nothing extra, so the cost belongs to the zone rather than
/// to the spell, where it would be charged on every cast.
/// </param>
/// <param name="Keyword">
/// Which keyword this permission came from, for the cards that ask afterwards. Escape is the one
/// that does - "this creature escapes with a +1/+1 counter on it" is a different thing from
/// entering with one, and only the cast knows which happened.
/// </param>
/// <param name="Transformed">
/// Whether the permanent arrives with its back face up (CR 702.146a).
/// </param>
public sealed record AlternativeCastZone(
    State.Zone Zone,
    ManaCostSpec Cost,
    bool ExileOnResolve,
    ImmutableList<ChosenCost>? Extra = null,
    bool OnlyIfDiscardedThisTurn = false,
    string Keyword = "",
    bool Transformed = false)
{
    /// <summary>Life paid as part of this permission's cost (CR 118.8, 601.2h).</summary>
    /// <remarks>
    /// "Flashback—{1}{U}, Pay 3 life." Life is not mana and not a card, so it fits neither
    /// <see cref="Cost"/> nor <see cref="Extra"/> — the same hole
    /// <see cref="ConditionalCost.LifeCost"/> fills for the alternative costs a spell offers from
    /// hand, and it is charged on the same footing: checked with the rest of the cost before any
    /// of it is paid, so a caster who cannot afford it is refused having spent nothing, and paid
    /// as a plain change rather than as damage — nothing prevents it and no lifelink sees it.
    /// <para>
    /// It belongs to the <em>permission</em> rather than to the spell, for the reason retrace's
    /// land discard does: the same card cast from hand pays no life, so a cost hung on the spell
    /// would be charged on every cast.
    /// </para>
    /// </remarks>
    public int LifeCost { get; init; }
}

/// <summary>
/// Tapping permanents to pay part of a spell's cost (CR 702.51a, 702.56a).
/// </summary>
/// <param name="What">Which permanents may be tapped.</param>
/// <param name="ColorMatters">
/// True for convoke, where a tapped creature pays {1} <em>or</em> one mana of its own colour;
/// false for improvise, where an artifact only ever pays {1}.
/// </param>
public sealed record TapToPay(TargetSpec What, bool ColorMatters);

/// <summary>
/// An ability a player can activate (CR 602.1): a cost, then an effect.
/// </summary>
/// <remarks>
/// A mana ability is one that could add mana, has no target, and is not a loyalty ability
/// (CR 605.1a). It does not use the stack and nobody can respond to it (CR 605.3b), which is why
/// it is a flag here rather than a separate kind — everything else about it is the same.
/// </remarks>
/// <summary>
/// A standing discount a permanent gives its controller's spells (CR 601.2f).
/// </summary>
/// <param name="FilterId">
/// Which spells it applies to, in the vocabulary searching and digging already use - a card type,
/// a subtype, or several joined by a separator.
/// </param>
/// <param name="Amount">How much generic mana comes off.</param>
/// <remarks>
/// A discount is not a continuous effect in the layer sense: it changes what a spell costs to
/// cast, which is worked out once as the spell is cast (CR 601.2f) and never recomputed. So it
/// lives beside the abilities rather than among them, and is read at cast time from whatever the
/// caster controls at that moment.
/// <para>
/// It comes off the generic part only. Reducing a coloured pip is not something a cost reduction
/// can do (CR 601.2f), and letting it would make a Dragon castable off two Islands.
/// </para>
/// </remarks>
public sealed record CostReducer(string FilterId, int Amount);

/// <summary>
/// One separately castable half of a card that has more than one (CR 709.4, 712.4).
/// </summary>
/// <remarks>
/// A split card prints two spells on one card and a player chooses between them as they cast it.
/// The discriminator is the printed cost: a face with one is a face somebody can pay for, which
/// is what separates a split card's halves from the back of a transforming card - that one has
/// no cost and is reached only by turning the permanent over.
/// </remarks>
/// <param name="Index">Which face this is, counting from zero.</param>
public sealed record CardHalf(
    int Index,
    string Name,
    string ManaCostRaw,
    Domain.Enums.CardType CardTypes,
    SpellDefinition? Spell)
{
    /// <summary>
    /// Whether this half has aftermath, and so is cast only from a graveyard (CR 702.127a).
    /// </summary>
    /// <remarks>
    /// Three static abilities in one word: it may be cast from a graveyard, it may be cast from
    /// nowhere else, and it is exiled rather than put anywhere else when it leaves the stack.
    /// The middle one is the half that is easy to skip and the one that makes the card a
    /// two-part card rather than a better one.
    /// </remarks>
    public bool HasAftermath { get; init; }
}

public sealed record ActivatedAbilityDefinition
{
    public required string Id { get; init; }

    public required string Text { get; init; }

    public ManaCostSpec ManaCost { get; init; } = ManaCostSpec.Free;

    /// <summary>Whether {T} is part of the cost (CR 602.5b).</summary>
    public bool RequiresTap { get; init; }

    /// <summary>
    /// A cost paid with the card itself, beyond mana and tapping (CR 601.2f).
    /// </summary>
    /// <remarks>
    /// Cycling discards the card; a great many abilities sacrifice the permanent. Both put the
    /// source somewhere else as part of the cost, which is why they are one field: the ability
    /// still resolves afterwards, from a source that has already gone.
    /// </remarks>
    public SelfCost SelfCost { get; init; } = SelfCost.None;

    /// <summary>
    /// A board question that has to answer yes before this can be activated (CR 602.5b).
    /// </summary>
    /// <remarks>
    /// "Activate only if you control a Plains or a Swamp" is a restriction rather than a cost:
    /// nothing is paid for it and nothing can be paid instead of it. Checked as the ability is
    /// activated, and re-checked nowhere, because CR 602.5b is about announcing the ability and
    /// not about it resolving.
    /// </remarks>
    public Func<State.GameState, IAbilitySource, State.GameObject, bool>? ActivateOnlyIf
    {
        get;
        init;
    }

    /// <summary>
    /// Life paid as part of the cost (CR 118.8, 601.2h).
    /// </summary>
    /// <remarks>
    /// A player may pay life only down to zero, and paying it is not damage — nothing prevents
    /// it and no lifelink triggers off it. It is checked like mana is: before anything else is
    /// paid, so a refusal costs nothing.
    /// </remarks>
    public int LifeCost { get; init; }

    /// <summary>
    /// Counters removed from the source to pay for this, if any (CR 118.3).
    /// </summary>
    /// <remarks>
    /// The charge/storage/depletion pattern: a permanent that stores something up and spends it.
    /// The engine has always been able to hold a counter of any name — <c>CountersChanged</c>
    /// takes the kind as a string — so this needed no new state, only a way to charge it.
    /// </remarks>
    public (string Kind, int Count)? CounterCost { get; init; }

    /// <summary>
    /// Whether the counter cost is a number the player names as they activate it (CR 601.2b).
    /// </summary>
    /// <remarks>
    /// "Remove any number of storage counters from this land" — the count is chosen the way X is
    /// chosen, so it arrives with the activation rather than being fixed here, and
    /// <see cref="CounterCost"/>'s own count is the minimum rather than the price.
    /// </remarks>
    public bool CounterCostIsChosen { get; init; }

    /// <summary>
    /// Energy counters this costs to activate (CR 107.4c, 118.3).
    /// </summary>
    /// <remarks>
    /// Separate from the mana cost because energy is not mana: it is not in the pool, it is not
    /// produced by lands, and a cost of "{E}{E}" is paid by having two energy counters and
    /// removing them. Lumping it into the mana cost would have made every energy card castable
    /// with two Forests.
    /// </remarks>
    public int EnergyCost { get; init; }

    /// <summary>
    /// Costs paid by moving cards the player chooses (CR 601.2f-h).
    /// </summary>
    /// <remarks>
    /// "Sacrifice a creature" and "Discard a card" name a set and leave the pick to the player.
    /// The engine cannot make that pick and must not: which creature you feed to the altar is the
    /// decision the card is about.
    /// <para>
    /// The pick arrives <em>with</em> the activation rather than being asked for during it. That
    /// is the whole design: paying a cost is part of an action the player is taking, so they can
    /// choose before they commit, and the engine never has to suspend a cost payment — which it
    /// deliberately cannot do, because a suspended payment is a continuation and a log cannot
    /// rebuild one.
    /// </para>
    /// </remarks>
    public ImmutableList<ChosenCost> ChosenCosts { get; init; } = [];

    /// <summary>
    /// How many times this may be activated each turn, or null for no limit (CR 602.5b).
    /// </summary>
    /// <remarks>
    /// "Activate only once each turn" is a restriction on activating, not a cost, so it is
    /// checked before anything is paid — a player who tries a second time has spent nothing.
    /// </remarks>
    public int? MaxActivationsPerTurn { get; init; }

    /// <summary>When this may be activated, beyond "any time you have priority" (CR 602.5d).</summary>
    public ActivationTiming Timing { get; init; } = ActivationTiming.AnyTime;

    /// <summary>
    /// Whether anyone may activate this, not only its controller (CR 602.2).
    /// </summary>
    /// <remarks>
    /// The default is that only the permanent's controller may (CR 602.1a). A handful of cards
    /// say otherwise, and the difference is the whole card — a public machine anyone can use is
    /// not the same object as one only you can.
    /// </remarks>
    public bool AnyPlayerMayActivate { get; init; }

    /// <summary>
    /// Loyalty counters added or removed to pay for this, or null if it is not a loyalty
    /// ability (CR 606.1).
    /// </summary>
    /// <remarks>
    /// Zero is a real cost and a common one, which is why this is nullable rather than defaulting
    /// to zero: "0: Draw a card" costs nothing and is still a loyalty ability, subject to the
    /// once-per-turn limit and the sorcery timing that come with being one.
    /// <para>
    /// Everything else a planeswalker needs — entering with its starting loyalty, damage removing
    /// counters, and dying at zero (CR 704.5i) — was already in the engine. This is the one piece
    /// that was missing, and without it not one of the 320 playable planeswalkers could do
    /// anything at all.
    /// </para>
    /// </remarks>
    public int? LoyaltyCost { get; init; }

    public ImmutableList<TargetSpec> Targets { get; init; } = [];

    public ImmutableList<IEffect> Effects { get; init; } = [];

    /// <summary>
    /// Mana this ability adds, if it is a mana ability (CR 605.1a). Non-empty means it bypasses
    /// the stack entirely.
    /// </summary>
    public ImmutableList<ManaProduction> Produces { get; init; } = [];

    public bool IsManaAbility => !Produces.IsEmpty && Targets.IsEmpty;

    /// <summary>Where the source has to be for the ability to be activatable (CR 602.5).</summary>
    public Zone FunctionsFrom { get; init; } = Zone.Battlefield;
}

/// <summary>
/// A cost the player pays by choosing cards (CR 601.2f).
/// </summary>
/// <param name="Kind">Where the cards come from and where they go.</param>
/// <param name="Count">How many.</param>
/// <param name="What">
/// Which cards qualify, for a sacrifice. Null accepts any card of the right kind — which is what
/// "Discard a card" means, since a hand has no further filter.
/// </param>
/// <param name="ExcludesSource">
/// True for "Sacrifice <em>another</em> creature". The difference matters: an altar that can eat
/// itself is a different card from one that cannot.
/// </param>
/// <param name="MinTotalPower">
/// For crew: the combined power the tapped creatures must reach (CR 702.122a). Zero means the
/// count is what matters instead.
/// </param>
public sealed record ChosenCost(
    ChosenCostKind Kind,
    int Count,
    TargetSpec? What = null,
    bool ExcludesSource = false,
    int MinTotalPower = 0);

/// <summary>What a chosen cost does with the cards it takes.</summary>
public enum ChosenCostKind
{
    /// <summary>Sacrifice permanents you control (CR 701.21a).</summary>
    SacrificePermanents,

    /// <summary>Discard cards from your hand (CR 701.9a).</summary>
    DiscardCards,

    /// <summary>
    /// Exile cards from your graveyard (CR 701.13a).
    /// </summary>
    /// <remarks>
    /// The one chosen cost that takes cards from a graveyard rather than from the hand or the
    /// battlefield, which is why it needs its own kind: where the cards come from decides both
    /// what may be offered and what happens to them.
    /// </remarks>
    ExileFromGraveyard,

    /// <summary>
    /// Return permanents you control to their owner's hands (CR 702.49a).
    /// </summary>
    /// <remarks>
    /// Ninjutsu's cost, and the only one that takes a permanent without destroying it. It has to
    /// be its own kind rather than a sacrifice with a different destination, because what it
    /// leaves behind - a card in hand and an empty attack - is the whole point of the ability.
    /// </remarks>
    ReturnToHand,

    /// <summary>Discard cards chosen at random rather than by the player (CR 701.9b).</summary>
    DiscardAtRandom,

    /// <summary>
    /// Exile cards from your hand (CR 701.13a).
    /// </summary>
    /// <remarks>
    /// The pitch spells' price - "you may exile a blue card from your hand rather than pay this
    /// spell's mana cost". A kind of its own rather than a discard, and the difference is the
    /// whole reason those cards are what they are: a card exiled this way is gone, while a card
    /// in the graveyard is still somewhere a dozen mechanics can reach it.
    /// </remarks>
    ExileFromHand,

    /// <summary>
    /// Tap untapped permanents you control (CR 118.12).
    /// </summary>
    /// <remarks>
    /// The cards are not spent, only turned sideways — so unlike a sacrifice or a discard this
    /// leaves them on the battlefield, and summoning sickness does not stop it: tapping a
    /// creature to <em>pay</em> is not the same as its own {T} ability (CR 302.6 applies only to
    /// the latter). Crew is this cost with a total-power requirement instead of a count.
    /// </remarks>
    TapPermanents,
}

/// <summary>
/// When an activated ability may be activated (CR 602.5d).
/// </summary>
/// <remarks>
/// The default is "any time you have priority" (CR 602.5a); everything here is a printed
/// restriction narrowing that. They are a closed set rather than a predicate because there are
/// only a handful of phrasings across the whole card pool, and a closed set is the difference
/// between the board being able to grey out a button and having to try the activation to find out.
/// </remarks>
public enum ActivationTiming
{
    /// <summary>Any time its controller has priority (CR 602.5a).</summary>
    AnyTime,

    /// <summary>
    /// "Activate only as a sorcery": a main phase of your turn with an empty stack (CR 602.5d).
    /// </summary>
    SorceryOnly,

    /// <summary>"Activate only during your turn."</summary>
    YourTurnOnly,

    /// <summary>"Activate only during your turn, before attackers are declared."</summary>
    BeforeAttackersDeclared,

    /// <summary>"Activate only during combat."</summary>
    CombatOnly,

    /// <summary>"Activate only during your upkeep."</summary>
    YourUpkeepOnly,
}

/// <summary>A cost paid by moving the source itself (CR 601.2f).</summary>
public enum SelfCost
{
    None,

    /// <summary>Discard the card, which is how cycling is paid (CR 702.29a).</summary>
    DiscardSelf,

    /// <summary>Sacrifice the permanent (CR 701.21).</summary>
    SacrificeSelf,

    /// <summary>
    /// Return this permanent to its owner's hand as a cost (CR 118.3c).
    /// </summary>
    /// <remarks>
    /// A cost rather than an effect, which is the whole of what makes these abilities work: the
    /// permanent has left by the time the ability resolves, so nothing can respond by killing it
    /// in reply and stopping the payment.
    /// </remarks>
    ReturnSelfToHand,

    /// <summary>
    /// Exile the card from the graveyard, which is where the ability functions (CR 701.13a).
    /// </summary>
    /// <remarks>
    /// The whole point of these abilities: the card is in the graveyard, and paying the cost is
    /// what takes it out of the game. An ability like this has
    /// <see cref="ActivatedAbilityDefinition.FunctionsFrom"/> set to the graveyard to match, and
    /// the two have to agree or the ability is unreachable.
    /// </remarks>
    ExileSelfFromGraveyard,
}

/// <summary>
/// What a mana may be spent on, when the card says it may not be spent on everything (CR 106.6).
/// </summary>
/// <remarks>
/// Two questions, because the cards ask two: what kind of thing is being paid for, and what it
/// is made of. "Spend this mana only to cast artifact spells or activate abilities of artifacts"
/// is one restriction with both purposes and one type, not two restrictions - which is why the
/// purposes are flags and the type is a filter over the same entry.
/// <para>
/// <see cref="Domain.Enums.CardType"/> of nothing means any type: "spend this mana only to
/// activate abilities" cares which purpose it is and nothing about what.
/// </para>
/// </remarks>
public readonly record struct ManaRestriction(ManaPurpose Purposes, Domain.Enums.CardType Types)
{
    /// <summary>
    /// Whether <see cref="Types"/> narrows only the casting of spells, leaving every other use
    /// of the mana alone.
    /// </summary>
    /// <remarks>
    /// The difference between "spend this mana only to cast artifact spells" and "this mana can't
    /// be spent to cast a nonartifact spell". The first forbids activating a creature's ability
    /// with it; the second does not, and a Powerstone that could not pay an equip cost would be a
    /// meaningfully worse token than the one CR 111.10h describes.
    /// </remarks>
    public bool TypesOnlyWhenCasting { get; init; }

    /// <summary>Whether this mana may pay for what is being paid for.</summary>
    public bool Allows(ManaPurpose purpose, Domain.Enums.CardType types) =>
        Purposes.HasFlag(purpose)
        && (Types == default
            || (TypesOnlyWhenCasting && purpose != ManaPurpose.CastSpell)
            || (types & Types) != default);
}

/// <summary>What a permanent chooses as it enters, if anything (CR 614.12).</summary>
public enum ChoiceOnEntry
{
    None = 0,

    /// <summary>"As this enters, choose a color."</summary>
    Color,

    /// <summary>"As this enters, choose a creature type."</summary>
    CreatureType,
}

/// <summary>What a payment is for (CR 106.6).</summary>
[Flags]
public enum ManaPurpose
{
    /// <summary>Anything else: a cost that is neither a spell nor an activation.</summary>
    Other = 0,

    CastSpell = 1,

    ActivateAbility = 2,
}

/// <summary>Mana a mana ability adds (CR 106.1).</summary>
public readonly record struct ManaProduction(
    Domain.Enums.ManaColor? Color,
    int Amount = 1,
    ManaRestriction? Restriction = null,
    bool FromChosenColor = false,
    bool FromCounterCost = false)
{
    /// <summary>
    /// Mana of whatever colour the permanent named as it entered (CR 614.12).
    /// </summary>
    /// <remarks>
    /// A flag rather than a colour, because <see cref="Color"/> being null already means
    /// colourless — a real kind of mana and not an absence (CR 106.1b) — so there was no spare
    /// value to mean "ask later".
    /// </remarks>
    public static ManaProduction Chosen() => new(null, 1, null, FromChosenColor: true);

    /// <summary>Colourless mana, which is a kind of its own (CR 106.1b).</summary>
    public static ManaProduction Colorless(int amount = 1) => new(null, amount);
}

/// <summary>
/// Where the engine finds out what a card's spell and activated abilities do.
/// </summary>
/// <remarks>
/// Extends <see cref="IAbilitySource"/> rather than replacing it, so a card pool can grow into
/// this a piece at a time. Everything defaults to "nothing", which is what a card the engine
/// does not implement looks like — and slice 8's legality gate is what stops such a card
/// reaching a game in the first place.
/// </remarks>
public interface ISpellSource
{
    /// <summary>What the card does when it resolves as a spell, or null if it just becomes a permanent.</summary>
    SpellDefinition? SpellOf(CardDefinition card) => null;

    /// <summary>The abilities a player can activate from this card (CR 602.1).</summary>
    IReadOnlyList<ActivatedAbilityDefinition> ActivatedOf(CardDefinition card) => [];
}

/// <summary>
/// An alternative cost that is only on offer while something is true of the game (CR 601.2f).
/// </summary>
/// <remarks>
/// Written once for surge and spectacle, which differ only in the question they ask, and shaped
/// so that the keywords with the same form - prowl, freerunning - are a condition rather than a
/// mechanism. <see cref="IsAvailable"/> is asked of the state as it stands at the moment of
/// casting, before the spell itself has been counted: surge wants <em>another</em> spell this
/// turn, and the spell being cast does not reach the log until after its cost is paid.
/// </remarks>
/// <param name="Keyword">The word printed on the card, for a refusal that names itself.</param>
/// <param name="Rule">The rule the offer comes from, cited in that refusal.</param>
/// <param name="Cost">What is paid instead of the mana cost.</param>
/// <param name="IsAvailable">Whether the game currently permits the offer.</param>
public sealed record ConditionalCost(
    string Keyword,
    string Rule,
    ManaCostSpec Cost,
    Func<GameState, Guid, bool> IsAvailable)
{
    /// <summary>
    /// What the caster gives up as well as the mana, when the offer asks for something
    /// (CR 118.9a, 601.2f-h).
    /// </summary>
    /// <remarks>
    /// This is the field that makes an alternative cost more than a discount. "You may sacrifice
    /// two Mountains rather than pay this spell's mana cost", "you may return two Islands you
    /// control to their owner's hand", "you may tap an untapped creature you control", emerge's
    /// creature — all of them are one offer whose price is paid in something other than mana, and
    /// a record that could hold only a <see cref="ManaCostSpec"/> could hold them only by
    /// dropping the payment, which is a strictly cheaper card than the one printed.
    /// <para>
    /// A list rather than one entry, because several cards name two prices at once — Force of
    /// Will's life and card, a Borderpost's mana and land. Chosen as the offer is taken
    /// (CR 601.2b) and paid on the same footing as every other chosen cost, so it rides the
    /// cast's existing cost-payment list rather than needing a channel of its own.
    /// </para>
    /// </remarks>
    public ImmutableList<ChosenCost> Payments { get; init; } = [];

    /// <summary>Life paid as part of taking the offer (CR 118.8).</summary>
    /// <remarks>
    /// "If you control a Swamp, you may pay 4 life rather than pay this spell's mana cost." Life
    /// is not mana and not a card, so it has nowhere else to go — and like the mana it is checked
    /// before anything is spent, so a caster who cannot afford it has lost nothing.
    /// </remarks>
    public int LifeCost { get; init; }

    /// <summary>
    /// Whether what was sacrificed takes its own mana value off the cost (CR 702.119a).
    /// </summary>
    /// <remarks>
    /// A flag beside the payments rather than a second reduction hook, because the two are the
    /// same sentence: emerge's discount is the mana value of the creature its cost already made
    /// you give up. It comes off the generic part only, which is not a simplification but what
    /// the rule says — "reduced by an amount of generic mana equal to the sacrificed creature's
    /// mana value".
    /// </remarks>
    public bool ReducedByManaValueSacrificed { get; init; }
}
