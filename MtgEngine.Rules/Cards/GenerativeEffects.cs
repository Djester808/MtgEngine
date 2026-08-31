using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;

namespace MtgEngine.Rules.Cards;

/// <summary>
/// Continuous effects the compiler names rather than registers.
/// </summary>
/// <remarks>
/// A pump is not one effect, it is a family: "+1/+1 until end of turn" and "+7/+7 until end of
/// turn" differ only by two numbers. Registering each one a card mentions would put thousands of
/// near-identical entries in a table, so the compiler emits a <em>name</em> that describes the
/// effect — <c>pump:+3/+3</c> — and this builds the definition when the engine asks for it.
/// <para>
/// The name is what lands in the event log, which is why it is a readable string and not a hash:
/// a stored game has to stay legible, and a log full of GUIDs pointing at a table that has since
/// changed is a game that cannot be replayed.
/// </para>
/// </remarks>
public static partial class GenerativeEffects
{
    /// <summary>The id for "gets +power/+toughness until end of turn" (CR 613.4).</summary>
    public static string PumpId(int power, int toughness) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"pump:{power:+0;-0;+0}/{toughness:+0;-0;+0}");

    /// <summary>The id for "becomes [types] until end of turn" (CR 613.1d, layer 4).</summary>
    /// <remarks>
    /// Animation: a Vehicle that has been crewed, a land that got up and walked. It adds the
    /// types rather than replacing them, because that is what the cards say — a crewed Vehicle is
    /// an artifact <em>creature</em> and is still a Vehicle.
    /// </remarks>
    /// <summary>The id for "gets +N/+N until end of turn for each [group]" (CR 613.4c).</summary>
    public static string PerEachPumpId(int power, int toughness, string group) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"pump-per:{power:+0;-0;+0}/{toughness:+0;-0;+0}:{group}");

    /// <summary>
    /// The id for "gets +N/+N until end of turn for each opponent you attacked with a creature
    /// this combat" (CR 702.121a) - melee.
    /// </summary>
    /// <remarks>
    /// A separate kind from <see cref="PerEachPumpId"/> rather than a group phrase handed to it,
    /// because what is being counted is not permanents. The per-each pump walks the battlefield
    /// through a target filter, and "opponents you attacked" is a fact about the combat that no
    /// filter over permanents can express. Trying to spell it as one would have produced a filter
    /// string that read plausibly and counted something else.
    /// </remarks>
    public static string MeleePumpId(int power, int toughness) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"pump-per-attacked:{power:+0;-0;+0}/{toughness:+0;-0;+0}");

    public static string BecomesId(CardType types) =>
        "becomes:" + ((int)types).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The id for "becomes an Aura enchantment with enchant creature" (CR 613.1d, layer 4).
    /// </summary>
    /// <remarks>
    /// The Licids' half of their own ability, and a <em>replacement</em> of the card types rather
    /// than an addition (CR 205.1b): a Licid enchanting a creature has stopped being one, and an
    /// effect that only added the enchantment type would leave a creature that could still attack
    /// and block while its Aura half was working — strictly better than the printed card.
    /// <para>
    /// The creature subtypes go with the creature type for the same rule: a subtype relevant only
    /// to the card type an effect took away is removed with it, so the permanent is
    /// "Enchantment — Aura" and not "Enchantment — Licid Aura".
    /// </para>
    /// <para>
    /// No parameters, which is what lets the reverse payment find it. The cost of turning back is
    /// printed on the card and lives on the ability that charges it; if it lived in this name the
    /// twelve Licids would have twelve different effects and the ability ending one would have to
    /// be told which. What the effect <em>does</em> is the same on all of them.
    /// </para>
    /// </remarks>
    public static string BecomesAuraId() => "becomes-aura";

    /// <summary>The prefix a copy effect's name carries (CR 707.2).</summary>
    private const string CopyPrefix = "copy:";

    /// <summary>
    /// The id for "becomes a copy of [card]" (CR 613.2a, 707.2).
    /// </summary>
    /// <remarks>
    /// The copied card travels <em>whole</em> inside the name, which is the one id here that
    /// carries more than a few numbers, and CR 707.2b is why: the copiable values are fixed when
    /// the copy is made, so the effect may not go looking for the permanent it copied. That
    /// permanent is very often gone by the next time this effect applies — a token that has
    /// ceased to exist, or a creature whose old id stopped existing when it died (CR 400.7) —
    /// and an effect holding only its id would quietly stop being a copy.
    /// <para>
    /// The fields are the ones a game log already records about a card (see
    /// <c>EventLogSerializer.PrintedCard</c>): what the rules engine reads, and not the prices,
    /// images or flavour text, which are no part of a game. Every copiable value CR 707.2 lists
    /// is among them, and the rules text is what the copy's abilities are compiled from
    /// (CR 707.2a) — a name carrying only power and toughness would produce a permanent the
    /// right size and the wrong card.
    /// </para>
    /// </remarks>
    public static string CopyId(CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);

        return CopyPrefix + JsonSerializer.Serialize(Copiable.Of(card), CopyFormat);
    }

    /// <summary>Whether a generated effect's name is a copy effect (CR 707.2).</summary>
    /// <remarks>
    /// Asked by anything that has to know whether a permanent is <em>already</em> a copy without
    /// working out what it is a copy of. A replacement that turns a permanent into a copy as it
    /// arrives has to stop applying once it has, or the alternatives it did not take are offered
    /// again against the very event it just replaced.
    /// </remarks>
    public static bool IsCopy(string definitionId)
    {
        ArgumentNullException.ThrowIfNull(definitionId);

        return definitionId.StartsWith(CopyPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// The copied card as the copy effect's own exceptions leave it (CR 707.9b).
    /// </summary>
    /// <remarks>
    /// "Except it's an enchantment in addition to its other types", "except it isn't legendary",
    /// "except it's 7/7" — CR 707.9b says the modified value <em>becomes</em> part of the copy's
    /// copiable values, which is exactly what handing a changed card to <see cref="CopyId"/>
    /// says. Nothing else in the engine has to learn what an exception is: the copy is a card,
    /// and this is that card with two words different.
    /// <para>
    /// The oracle id is deliberately the copied card's, unchanged. Abilities are looked up by
    /// card, and CR 707.9b modifies a <em>characteristic</em> — a Copy Artifact copying a
    /// Juggernaut still has Juggernaut's abilities, so the changed card has to lead an ability
    /// source to the same place the unchanged one would.
    /// </para>
    /// <para>
    /// Types are added rather than replaced when the clause says "in addition to its other
    /// types", because that is what it says, and <em>set</em> when it does not — "except it's a
    /// 1/1 green Frog" leaves a token with no other creature type at all. CR 707.9d turns on that
    /// distinction — an exception that adds a type still copies a characteristic-defining
    /// ability, while one that <em>sets</em> a characteristic does not — and the second half of
    /// that rule is not done here: "except it's 7/7" writes the size onto the card and does not
    /// take away a copied ability that defines the size, so a Quicksilver Gargantuan copying a
    /// creature whose power is characteristic-defining ends up the size that ability says. Left
    /// that way deliberately: dropping an ability by <em>what it defines</em> needs the compiler
    /// to say which characteristic each one defines, and it does not.
    /// </para>
    /// <para>
    /// Colour is added or set by the same distinction, and the printed templating is what draws
    /// it: "in addition to its other <em>colors and</em> types" adds the colour, and the same
    /// sentence without those two words replaces it. Ratadrabik of Urborg's Zombie is black
    /// <em>and</em> whatever it was; Croaking Counterpart's Frog is only green.
    /// </para>
    /// </remarks>
    public static CardDefinition Excepting(CardDefinition card, CopyException? except)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (except is null)
            return card;

        var subtypes = except.SetSubtypes.Count > 0
            ? except.SetSubtypes
            : except.AddSubtypes.Count > 0
                ? [.. card.Subtypes.Concat(
                    except.AddSubtypes.Where(
                        s => !card.Subtypes.Contains(s, StringComparer.OrdinalIgnoreCase)))]
                : card.Subtypes;

        var colours = Recolour(card.Colors, except);

        var amended = new CardDefinition
        {
            OracleId = card.OracleId,
            Name = card.Name,
            ManaCost = card.ManaCost,
            ManaCostRaw = card.ManaCostRaw,
            Cmc = card.Cmc,
            CardTypes = card.CardTypes | except.AddTypes,
            Subtypes = subtypes,
            Supertypes = except.DropLegendary
                ? [.. card.Supertypes.Where(
                    s => !string.Equals(s, "Legendary", StringComparison.OrdinalIgnoreCase))]
                : card.Supertypes,
            OracleText = card.OracleText,
            Power = except.Power ?? card.Power,
            Toughness = except.Toughness ?? card.Toughness,
            StartingLoyalty = card.StartingLoyalty,
            Keywords = card.Keywords | except.AddKeywords,

            // The identity moves with the colour, because a token that is black has black in its
            // identity whatever the card it copied cost. Left alone when the clause says nothing
            // about colour, so an ordinary "in addition to its other types" copy keeps the
            // identity its mana cost gave it.
            ColorIdentity = colours is null ? card.ColorIdentity : colours,
            Colors = colours ?? card.Colors,
            Faces = card.Faces,
            ImageUriNormal = card.ImageUriNormal,
            ImageUriLarge = card.ImageUriLarge,
            ImageUriSmall = card.ImageUriSmall,
            ImageUriArtCrop = card.ImageUriArtCrop,
            ImageUriNormalBack = card.ImageUriNormalBack,
        };

        // CR 707.9b: an ability the exception grants is one of the copy's own copiable values, so
        // it goes on the card the copy is made from rather than onto the permanent afterwards -
        // which is also the only place a token's abilities are ever read from. Applied last so
        // the id it re-keys is the id of the fully excepted card.
        return Abilities.TokenCards.Granting(amended, string.Join('\n', except.GrantedText));
    }

    /// <summary>The copy's colours, or null when the clause did not mention any (CR 707.9b).</summary>
    private static IReadOnlyList<ManaColor>? Recolour(
        IReadOnlyList<ManaColor> printed, CopyException except)
    {
        if (except.SetColors.Count > 0)
            return except.SetColors;

        if (except.AddColors.Count == 0)
            return null;

        return [.. printed.Concat(except.AddColors.Where(c => !printed.Contains(c)))];
    }

    /// <summary>Compact, and stable across runs — the id has to compare and to replay.</summary>
    private static readonly JsonSerializerOptions CopyFormat = new()
    {
        WriteIndented = false,
    };

    /// <summary>
    /// A card as a copy effect's name records it — the copiable values, and nothing else.
    /// </summary>
    /// <remarks>
    /// Deliberately the same field list as the one a stored game keeps, for the same reason it
    /// keeps that list: dropping a field here is invisible until the copy is played. The faces
    /// are the field that proves it — a copy of a double-faced permanent uses the face that is
    /// up (CR 707.8), and without them it would come back unable to turn over at all. That is
    /// the exact field a stored game was once caught dropping, and 853 of the playable corpus
    /// carry faces.
    /// </remarks>
    private sealed record Copiable(
        string OracleId,
        string Name,
        string ManaCostRaw,
        int Cmc,
        CardType CardTypes,
        IReadOnlyList<string> Subtypes,
        IReadOnlyList<string> Supertypes,
        string OracleText,
        int? Power,
        int? Toughness,
        int? StartingLoyalty,
        KeywordAbility Keywords,
        IReadOnlyList<ManaColor> ColorIdentity,
        IReadOnlyList<ManaColor> Colors,
        IReadOnlyList<CardFace> Faces)
    {
        public static Copiable Of(CardDefinition card) => new(
            card.OracleId,
            card.Name,
            card.ManaCostRaw,
            card.Cmc,
            card.CardTypes,
            card.Subtypes,
            card.Supertypes,
            card.OracleText,
            card.Power,
            card.Toughness,
            card.StartingLoyalty,
            card.Keywords,
            card.ColorIdentity,
            card.Colors,
            card.Faces);

        public CardDefinition ToDefinition() => new()
        {
            // The copied card's own oracle id, so an ability source keyed by it serves the
            // abilities it has already compiled rather than compiling a second, equal card.
            OracleId = OracleId,
            Name = Name,
            ManaCostRaw = ManaCostRaw,
            Cmc = Cmc,
            CardTypes = CardTypes,
            Subtypes = [.. Subtypes],
            Supertypes = [.. Supertypes],
            OracleText = OracleText,
            Power = Power,
            Toughness = Toughness,
            StartingLoyalty = StartingLoyalty,
            Keywords = Keywords,
            ColorIdentity = [.. ColorIdentity],
            Colors = [.. Colors],
            Faces = [.. Faces],
        };
    }

    /// <summary>
    /// The id for "gain control until end of turn" (CR 613.1b, layer 2).
    /// </summary>
    /// <remarks>
    /// The new controller is baked into the name because a generated effect has nothing else to
    /// carry it: the definition is looked up by id alone, and who took the creature is not
    /// something the layer can work out. It makes the id less pretty than the others and keeps
    /// the log honest, which is the trade this whole naming scheme makes.
    /// <para>
    /// Layer 2, which is why it comes out right when combined with anything else: a creature
    /// stolen and then pumped is pumped for its new controller, because control is settled before
    /// the lord in layer 6 asks whose creatures it sees.
    /// </para>
    /// </remarks>
    public static string ControlId(Guid controllerId) =>
        string.Create(CultureInfo.InvariantCulture, $"control:{controllerId:N}");

    /// <summary>What has to stay true for borrowed control to last (CR 611.2b).</summary>
    public enum ControlHeldWhile
    {
        /// <summary>"…for as long as you control this creature."</summary>
        Controlled,

        /// <summary>"…for as long as this artifact remains tapped."</summary>
        Tapped,

        /// <summary>"…for as long as this creature remains on the battlefield."</summary>
        OnBattlefield,
    }

    /// <summary>
    /// The id for "gain control … for as long as you control [this]" (CR 611.2b).
    /// </summary>
    /// <remarks>
    /// The condition is carried in the id rather than in the state, because the state is a fold
    /// of the event log and a delegate cannot be replayed. Everything the condition needs is two
    /// ids, and both are known when the effect is created.
    /// <para>
    /// The source's id is the one it had when the effect was made. That is exactly right: a
    /// permanent that leaves and comes back is a new object (CR 400.7), so the old id names
    /// nothing, the condition fails, and the effect ends - which is what the card says.
    /// </para>
    /// </remarks>
    public static string ControlWhileId(
        Guid controllerId, State.ObjectId sourceId, ControlHeldWhile until) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"control-while:{controllerId:N}:{sourceId.Value:N}:{Named(until)}");

    /// <summary>
    /// The id for any generated effect that lasts "for as long as …" (CR 611.2b).
    /// </summary>
    /// <remarks>
    /// A wrapper rather than a duration on each id, and that is the whole of why it is worth
    /// having: the corpus prints the clause after a pump, a keyword grant, an animation and a
    /// "doesn't untap", and every one of those already has a name here. Wrapping means the
    /// duration is written once and the vocabulary keeps multiplying instead of being enumerated
    /// again with an "-while" suffix on each entry.
    /// <para>
    /// Both ids the condition needs travel with it: the permanent whose continued presence is the
    /// condition, and the player who has to still control it for "for as long as you control
    /// this" to be true. The player is not always the affected permanent's controller — the whole
    /// point of these cards is that it usually is not.
    /// </para>
    /// <para>
    /// The wrapped id goes last, so it may itself contain colons; nothing else may.
    /// </para>
    /// </remarks>
    public static string HeldWhileId(
        string inner, Guid holderId, State.ObjectId sourceId, ControlHeldWhile until) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"while:{Named(until)}:{holderId:N}:{sourceId.Value:N}:{inner}");

    /// <summary>The id for "all creatures able to block it do so" (CR 509.1c).</summary>
    /// <remarks>
    /// The same requirement a printed lure produces, made by a spell instead and lasting only the
    /// turn. It is a separate id rather than a second reader so the block check keeps answering
    /// one question: a requirement is a requirement whether a static or a sorcery created it.
    /// </remarks>
    public static string LureId() => "lure";

    /// <summary>The id for "at least one creature able to block it has to" (CR 509.1c).</summary>
    /// <remarks>
    /// The singular of <see cref="LureId"/>, and not a weaker version of it: a lure compels every
    /// creature that can block and this compels one, which CR 509.1c counts differently. The two
    /// flags are checked apart in the combat rules for that reason, so an effect answering the
    /// wrong one would either demand blocks the card never asked for or accept a declaration the
    /// card forbids.
    /// <para>
    /// It exists because the static printing of the same requirement had nowhere to put a
    /// duration: "~ must be blocked if able" is a continuous effect the compiler registers on the
    /// card, and "must be blocked <em>this turn</em>" is the same characteristic granted by a
    /// one-shot. Reading the second as the first is what would have made it permanent.
    /// </para>
    /// </remarks>
    public static string MustBeBlockedId() => "must-be-blocked";

    /// <summary>The id for "can block an additional N creatures" (CR 509.1a).</summary>
    /// <remarks>
    /// The count travels in the id because these grants add rather than replace - "an additional
    /// creature" is an amount, so two of them let one creature block three. "Any number" is a
    /// number no board can reach rather than a second unlimited flag, which is the same choice
    /// the static reader makes.
    /// <para>
    /// The static printing's ids carry the card's name as well (<c>extra-blocks:Name:1</c>) and
    /// are registered rather than generated, so nothing here can be confused for one: the
    /// pattern that reads this is anchored on digits alone.
    /// </para>
    /// </remarks>
    public static string ExtraBlocksId(int count) =>
        string.Create(CultureInfo.InvariantCulture, $"extra-blocks:{count}");

    /// <summary>The id for "this creature blocks this turn if able" (CR 509.1a).</summary>
    /// <remarks>
    /// The requirement read from the blocker's side. A lure is the attacker's version of the
    /// same rule and cannot stand in for it: a lure compels everyone who can, and this compels
    /// one named creature, which is a different card entirely.
    /// </remarks>
    public static string MustBlockId() => "must-block";

    /// <summary>The id for "this creature blocks that attacker if able" (CR 509.1a).</summary>
    /// <remarks>
    /// Both halves are named, so the attacker travels in the id - a continuous effect carries no
    /// state of its own and the requirement has to survive until blockers are declared.
    /// </remarks>
    public static string MustBlockAttackerId(State.ObjectId attacker) =>
        string.Create(CultureInfo.InvariantCulture, $"must-block:{attacker.Value:N}");

    /// <summary>The id for "goaded by this player, until their next turn" (CR 701.15a).</summary>
    /// <remarks>
    /// Both the goader and the turn it happened on travel in the id. The goader because the
    /// requirement is about them - the creature must attack somebody else - and the turn because
    /// the duration is "until the next turn of the controller of that spell or ability", which
    /// cannot be answered without knowing which turn it started on.
    /// </remarks>
    public static string GoadedById(Guid goader, int turn) =>
        string.Create(CultureInfo.InvariantCulture, $"goaded:{goader:N}:{turn}");

    /// <summary>The id for "must attack this player, with haste" (CR 702.141a).</summary>
    /// <remarks>
    /// Three things in one because encore gives all three together and nothing else gives any of
    /// them: the token attacks, it attacks that opponent, and it can do so the turn it arrives.
    /// </remarks>
    public static string MustAttackPlayerId(Guid player) =>
        string.Create(CultureInfo.InvariantCulture, $"must-attack:{player:N}");

    /// <summary>The id for "this creature can't block that attacker" (CR 509.1b).</summary>
    public static string CantBlockAttackerId(State.ObjectId attacker) =>
        string.Create(CultureInfo.InvariantCulture, $"cant-block:{attacker.Value:N}");

    /// <summary>The id for "switch power and toughness" (CR 613.4d, layer 7d).</summary>
    /// <remarks>
    /// The last sublayer, and it has to be: a switch swaps whatever the layers before it left,
    /// so a 2/4 that has taken a +1/+1 counter switches to a 5/3 rather than to a 4/2 plus a
    /// counter. The layer and the builder method for it were both here already, and nothing had
    /// ever produced one - the card that says it was simply unread.
    /// </remarks>
    public static string SwitchPowerToughnessId() => "switch-pt";

    /// <summary>The id for "loses all abilities until end of turn" (CR 613.1f, layer 6).</summary>
    /// <remarks>
    /// One name and no parameters, because a removal has nothing to say about itself: every card
    /// that prints it takes <em>all</em> of them, and the only variable — which permanents it
    /// reaches — is the floating effect's own list of affected ids (CR 613.7b), settled when the
    /// spell resolved.
    /// <para>
    /// The definition it resolves to declares <see cref="ContinuousEffectDefinition
    /// .RemovesAllAbilities"/> rather than emptying the keywords in <c>Apply</c>, and that is the
    /// whole reason the flag exists: the silenced permanent's own static abilities have to stop
    /// being <em>offered</em>, which is decided before any effect is applied and to a permanent
    /// that is not the one being computed. A creature stripped this way stops being a lord for
    /// everything else on the board, and no <c>Apply</c> can reach that.
    /// </para>
    /// </remarks>
    public static string LosesAllAbilitiesId() => "lose-abilities";

    /// <summary>
    /// The id for "doesn't untap during its controller's untap step" (CR 502.3).
    /// </summary>
    /// <remarks>
    /// The restriction a printed Aura already carries as a static, made by a resolving ability
    /// instead. It is the same flag on the same builder, so the untap step keeps asking one
    /// question whatever put the restriction there — which is the point of naming it rather than
    /// giving the effect a second path through the untap rules.
    /// <para>
    /// No duration in the name: on its own it is permanent, which is what "that permanent doesn't
    /// untap during its controller's untap step" says when nothing follows it. The cards that
    /// bound it wrap this in <see cref="HeldWhileId"/>.
    /// </para>
    /// </remarks>
    public static string DoesNotUntapId() => "no-untap";

    /// <summary>
    /// The id for "can attack as though it didn't have defender" (CR 609.4, 702.3b).
    /// </summary>
    /// <remarks>
    /// A permission rather than the removal of a keyword, and the difference is not
    /// cosmetic: stripping defender would also shrink every card that counts creatures with
    /// defender, that assigns damage by toughness, or that anthems them - so the permanent
    /// would stop being what it is in order to be allowed to attack, and the card would play
    /// as better than printed on exactly the board it was designed for.
    /// <see cref="State.ComputedCharacteristics.MayAttackAsThoughNoDefender"/> is read
    /// beside the keyword instead, leaving it in place, which is the scope CR 609.4 gives an
    /// "as though" effect.
    /// <para>
    /// No duration in the name. On its own it is permanent, which is what the printed static
    /// says; the cards that print "this turn" reach it through an until-end-of-turn effect,
    /// and the ones that print a condition wrap it in a static of their own. That is why one
    /// name serves five printed spellings, and why there is exactly one of it: two ids for
    /// one rule is how two vocabularies start disagreeing.
    /// </para>
    /// </remarks>
    public static string MayAttackAsThoughNoDefenderId() => "may-attack-defender";

    /// <summary>The id for "becomes the colour of your choice" (CR 613.4d, layer 5).</summary>
    /// <remarks>
    /// Becoming a colour *replaces* what the permanent was, rather than adding to it (CR 202.2b),
    /// which is why this clears before it sets. A card that adds a colour says "in addition to
    /// its other colors" and is a different sentence.
    /// </remarks>
    /// <summary>The id for "becomes the creature type of your choice" (CR 613.4c, layer 4).</summary>
    /// <remarks>
    /// Layer 4 and a replacement rather than an addition: CR 205.1a says a new subtype replaces
    /// the permanent's existing subtypes <em>from the same set</em>, and a card that meant to
    /// keep them says "in addition to its other types" — which is CR 205.1b and
    /// <see cref="GainsCreatureTypeId"/>. The citation here used to name 205.1b for the
    /// replacement, which is the rule that says the opposite.
    /// <para>
    /// "From the same set" is the half that had been dropped. Clearing the whole list took the
    /// land types with it, so a Forest animated into an Elemental stopped being a Forest — and
    /// landwalk reads the computed type, not the printed one.
    /// </para>
    /// </remarks>
    public static string BecomesCreatureTypeId(string type) =>
        string.Create(CultureInfo.InvariantCulture, $"becomes-type:{type}");

    /// <summary>
    /// The id for "becomes [type] in addition to its other types" (CR 205.1b, layer 4).
    /// </summary>
    /// <remarks>
    /// The other half of 205.1: this one keeps what was there. It is a separate id rather than a
    /// flag on the one above because a generated definition is looked up by name alone, and the
    /// two do opposite things to the same list — a Vampire told to become a Demon "in addition to
    /// its other types" is both, and reading it as the replacement would take away the type the
    /// rest of the board is counting.
    /// </remarks>
    public static string GainsCreatureTypeId(string type) =>
        string.Create(CultureInfo.InvariantCulture, $"gains-type:{type}");

    public static string BecomesColorId(ManaColor colour) =>
        string.Create(CultureInfo.InvariantCulture, $"becomes-color:{Named(colour)}");

    /// <summary>
    /// The id for "becomes white and blue" — every colour it becomes, in one effect (CR 105.2).
    /// </summary>
    /// <remarks>
    /// One effect and not one per colour, because becoming a colour <em>sets</em> the colours
    /// rather than adding to them: two effects in the same layer each clear what the last wrote,
    /// so a permanent that printed "a 2/2 white and blue Bird" came out blue. Azorius Keyrune is
    /// the card that showed it.
    /// </remarks>
    public static string BecomesColorsId(IEnumerable<ManaColor> colours) =>
        "becomes-color:" + string.Join(',', colours.Select(Named));

    /// <summary>The condition's name as it appears in an id — lower case, so ids compare.</summary>
    private static string Named(ControlHeldWhile until) =>
        until.ToString().ToLowerInvariant();

    /// <summary>Whether a "for as long as …" duration is still running (CR 611.2b).</summary>
    /// <remarks>
    /// One question with two callers — the control effect that needed it first and the wrapper
    /// that gives the same duration to everything else. Written twice it would have drifted, and
    /// the half that would have drifted is the one that matters: an effect whose condition is
    /// answered too generously never ends.
    /// <para>
    /// Every spelling first requires the source to still be there. A permanent that has left is a
    /// different object (CR 400.7), so the id names nothing and the condition is false whatever
    /// else it asks — which is what "for as long as this creature remains on the battlefield"
    /// says in so many words, and what the other two need before they can ask anything.
    /// </para>
    /// </remarks>
    private static bool StillHolds(
        State.GameState state,
        IAbilitySource abilities,
        State.ObjectId keeper,
        Guid holder,
        ControlHeldWhile until) =>
        state.TryGetObject(keeper, out var source)
        && source.Zone == State.Zone.Battlefield
        && until switch
        {
            // Control is layer 2, so who controls the *keeper* is itself a computed answer - a
            // thief that has been stolen no longer keeps what it took.
            ControlHeldWhile.Controlled =>
                State.Characteristics.Of(state, abilities, source).ControllerId == holder,

            ControlHeldWhile.Tapped => source.Permanent?.IsTapped == true,

            // Nothing more to ask: being on the battlefield is the whole condition, and the guard
            // above has already answered it.
            _ => true,
        };

    /// <summary>A colour's name as it appears in an id.</summary>
    private static string Named(ManaColor colour) =>
        colour.ToString().ToLowerInvariant();

    /// <summary>The id for "becomes a P/T" — setting, not modifying (CR 613.4b, layer 7b).</summary>
    /// <remarks>
    /// Layer 7b, and the distinction from a pump matters: a land that becomes a 3/3 and then
    /// takes a +1/+1 counter is a 4/4, because setting happens first and the counter modifies
    /// what setting produced. A pump of +3/+3 on a land with no printed power would be nothing
    /// at all.
    /// </remarks>
    public static string SetPowerToughnessId(int power, int toughness) =>
        string.Create(CultureInfo.InvariantCulture, $"becomes-pt:{power}/{toughness}");

    /// <summary>The id for "has base power N" — the same layer, one half of the size.</summary>
    /// <remarks>
    /// Its own name rather than a toughness of null inside the pair, because the id is what lands
    /// in the log and "becomes-pt:0/" would read as a half-written one.
    /// </remarks>
    public static string SetPowerId(int power) =>
        string.Create(CultureInfo.InvariantCulture, $"becomes-power:{power}");

    /// <summary>The id for "gains [keywords] until end of turn" (CR 613.1f, layer 6).</summary>
    /// <remarks>
    /// Sixty-four bits, not thirty-two. The keyword flags run past bit 31 - the five protection
    /// colours live at 30 through 34 - so casting to <see langword="int"/> truncated the top
    /// ones to nothing and produced "grant:0", an effect that granted precisely nothing while
    /// looking entirely correct in the log and in the state.
    /// </remarks>
    public static string GrantId(KeywordAbility keywords) =>
        "grant:" + ((long)keywords).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The id for "gains [an ability written out in quotation marks] until end of turn".
    /// </summary>
    /// <remarks>
    /// The ability's own text rides in the id, exactly as a group phrase does for a counted pump:
    /// the id is all a generated definition gets, and the text is parsed back through the same
    /// readers a printed ability goes through. So an ability the compiler understands on a card
    /// is understood when it is handed to a creature for a turn.
    /// </remarks>
    public static string GrantAbilityId(string abilityText) => "grant-ability:" + abilityText;

    /// <summary>
    /// Builds the definition a generated name describes, or null if it names nothing.
    /// </summary>
    public static ContinuousEffectDefinition? Resolve(string definitionId)
    {
        ArgumentNullException.ThrowIfNull(definitionId);

        // First, and by prefix rather than by regex: the payload is a card, not a number, and
        // no pattern below could match it anyway.
        if (definitionId.StartsWith(CopyPrefix, StringComparison.Ordinal))
        {
            if (CopiedCard(definitionId[CopyPrefix.Length..]) is not { } copied)
                return null;

            return new ContinuousEffectDefinition
            {
                Id = definitionId,

                // CR 613.2a. The copy is *declared* rather than applied - see
                // ContinuousEffectDefinition.Copies: the copied card's static abilities have to
                // start being offered from the battlefield, which nothing inside an Apply can
                // reach. Everything a copy does to the object it applies to is done from there.
                Layer = EffectLayer.Copy,
                Copies = copied,
                Applies = (_, _, _) => true,
                Apply = (_, _, _) => { },
            };
        }

        var granted = GrantName().Match(definitionId);
        if (granted.Success)
        {
            var keywords = (KeywordAbility)long.Parse(
                granted.Groups["k"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);

            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                // Adding an ability is layer 6, and P/T modification is 7c: a pump that also
                // grants trample is two effects in two layers, not one (CR 613.1f, 613.4c).
                Layer = EffectLayer.Ability,
                Applies = (_, _, _) => true,
                Apply = (_, _, builder) => builder.Keywords |= keywords,
            };
        }

        if (definitionId.StartsWith("grant-ability:", StringComparison.Ordinal)
            && CardCompiler.TryQuotedAbility(
                definitionId["grant-ability:".Length..],
                out var grantedActivated,
                out var grantedTriggers))
        {
            return new ContinuousEffectDefinition
            {
                Id = definitionId,

                // Layer 6, like every other ability grant (CR 613.1f).
                Layer = EffectLayer.Ability,
                Applies = (_, _, _) => true,
                Apply = (_, _, builder) =>
                {
                    builder.GrantedActivated.AddRange(grantedActivated);
                    builder.GrantedTriggers.AddRange(grantedTriggers);
                },
            };
        }

        // "Gets +1/+1 until end of turn for each creature you control" — a pump whose size is
        // not known until it applies. The group phrase rides in the id because the id is all a
        // generated definition gets, and it is read back through the compiler's own counting
        // vocabulary, so every phrase that vocabulary knows — a board group, a player count, a
        // pile, a party — works here without this knowing how any of them are counted.
        //
        // Asked without a source, and that is the honest answer rather than a convenience: a
        // floating effect made by a spell is handed a null source when characteristics are
        // computed (see Characteristics.CandidatesFor), so a phrase reading "it" has nothing
        // to read. Those are refused at compile time by the same call, which is what keeps the
        // two ends in step: a sentence this cannot count is a sentence the compiler leaves
        // unread, rather than one that compiles and then quietly pumps by zero.
        var perEach = PerEachPumpName().Match(definitionId);
        if (perEach.Success
            && EffectPhrase.Counting(perEach.Groups["group"].Value, hasSource: false) is
            { } counted)
        {
            var perPower = int.Parse(
                perEach.Groups["p"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var perToughness = int.Parse(
                perEach.Groups["t"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.PowerToughnessModify,
                Applies = (_, _, _) => true,
                Apply = (state, _, builder) =>
                {
                    var many = counted(
                        state, EmptyAbilities.Instance, builder.ControllerId, default, null);

                    builder.Modify(perPower * many, perToughness * many);
                },
            };
        }

        // Counted off the combat rather than the board. Distinct defending players, which is
        // the right count even when the attack was aimed at a planeswalker: an attack target
        // carries the player whose planeswalker it is (CR 506.2), so attacking somebody's
        // planeswalker is attacking them for this purpose and is counted once either way.
        var melee = MeleePumpName().Match(definitionId);
        if (melee.Success)
        {
            var perPower = int.Parse(
                melee.Groups["p"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var perToughness = int.Parse(
                melee.Groups["t"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.PowerToughnessModify,
                Applies = (_, _, _) => true,
                Apply = (state, _, builder) =>
                {
                    var many = state.Combat.Attackers
                        .Where(attacker =>
                            state.TryGetObject(attacker.Key, out var creature)
                            && creature.ControllerId == builder.ControllerId)
                        .Select(attacker => attacker.Value.DefendingPlayer)
                        .Distinct()
                        .Count(defender => defender != builder.ControllerId);

                    builder.Modify(perPower * many, perToughness * many);
                },
            };
        }

        var control = ControlName().Match(definitionId);
        if (control.Success)
        {
            var newController = Guid.ParseExact(control.Groups["p"].Value, "N");

            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.Control,
                Applies = (_, _, _) => true,
                Apply = (_, _, builder) => builder.ControllerId = newController,
            };
        }

        var controlWhile = ControlWhileName().Match(definitionId);
        if (controlWhile.Success)
        {
            var taker = Guid.ParseExact(controlWhile.Groups["p"].Value, "N");
            var keeper = new State.ObjectId(Guid.ParseExact(controlWhile.Groups["s"].Value, "N"));
            var until = Enum.Parse<ControlHeldWhile>(controlWhile.Groups["k"].Value, true);

            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.Control,
                Applies = (_, _, _) => true,
                Apply = (_, _, builder) => builder.ControllerId = taker,
                While = (state, abilities) => StillHolds(state, abilities, keeper, taker, until),
            };
        }

        // "… for as long as ~ remains tapped" wrapped round any of the names below it
        // (CR 611.2b). The inner effect is built by this same method, so a duration is available
        // on every generated effect that exists rather than on the handful somebody remembered
        // to give one: the pump, the grant, the animation and the "doesn't untap" all arrive
        // here already working.
        var lasting = HeldWhileName().Match(definitionId);
        if (lasting.Success && Resolve(lasting.Groups["inner"].Value) is { } held)
        {
            var holder = Guid.ParseExact(lasting.Groups["p"].Value, "N");
            var kept = new State.ObjectId(Guid.ParseExact(lasting.Groups["s"].Value, "N"));
            var ends = Enum.Parse<ControlHeldWhile>(lasting.Groups["k"].Value, true);

            // The wrapped definition keeps its own layer and its own Apply, and gains nothing but
            // the condition. An inner effect that already carried one would have it replaced,
            // which is why nothing this wraps has one: the only definitions with a While of their
            // own are the two above, and neither is reachable through here.
            return held with
            {
                Id = definitionId,
                While = (state, abilities) => StillHolds(state, abilities, kept, holder, ends),
            };
        }

        if (string.Equals(definitionId, "must-block", StringComparison.Ordinal))
        {
            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.Ability,
                Applies = (_, _, _) => true,
                Apply = (_, _, builder) => builder.MustBlock = true,
            };
        }

        var aimedAtPlayer = MustAttackPlayerName().Match(definitionId);
        if (aimedAtPlayer.Success && Guid.TryParse(aimedAtPlayer.Groups["p"].Value, out var atPlayer))
        {
            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.Ability,
                Applies = (_, _, _) => true,
                Apply = (_, _, builder) =>
                {
                    builder.MustAttackPlayer = atPlayer;
                    builder.Keywords |= KeywordAbility.MustAttack | KeywordAbility.Haste;
                },
            };
        }

        var goaded = GoadedByName().Match(definitionId);
        if (goaded.Success
            && Guid.TryParse(goaded.Groups["p"].Value, out var goader)
            && int.TryParse(
                goaded.Groups["t"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var since))
        {
            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.Ability,
                Applies = (_, _, _) => true,

                // CR 701.15b: a goaded creature attacks each combat if able. The other half of
                // the rule - who it may attack - is a restriction and lives in the combat rules,
                // because only there is the defending player known.
                Apply = (_, _, builder) =>
                {
                    builder.GoadedBy.Add(goader);
                    builder.Keywords |= KeywordAbility.MustAttack;
                },

                // CR 701.15a: "until the next turn of the controller of that spell or ability".
                // The goader's own turn is the one that ends it, and only a later one - goading
                // on your own turn lasts through everybody else's and ends when yours comes back.
                While = (state, _) =>
                    !(state.ActivePlayerId == goader && state.TurnNumber > since),
            };
        }

        var barred = CantBlockAttackerName().Match(definitionId);
        if (barred.Success && Guid.TryParse(barred.Groups["a"].Value, out var forbidden))
        {
            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.Ability,
                Applies = (_, _, _) => true,
                Apply = (_, _, builder) =>
                    builder.CantBlockAttacker = new State.ObjectId(forbidden),
            };
        }

        var told = MustBlockAttackerName().Match(definitionId);
        if (told.Success && Guid.TryParse(told.Groups["a"].Value, out var attacker))
        {
            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.Ability,
                Applies = (_, _, _) => true,
                Apply = (_, _, builder) =>
                    builder.MustBlockAttacker = new State.ObjectId(attacker),
            };
        }

        if (string.Equals(definitionId, "lure", StringComparison.Ordinal))
        {
            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.Ability,
                Applies = (_, _, _) => true,
                Apply = (_, _, builder) => builder.MustBeBlockedByAll = true,
            };
        }

        if (string.Equals(definitionId, "must-be-blocked", StringComparison.Ordinal))
        {
            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.Ability,
                Applies = (_, _, _) => true,
                Apply = (_, _, builder) => builder.MustBeBlocked = true,
            };
        }

        var extraBlocks = ExtraBlocksName().Match(definitionId);
        if (extraBlocks.Success)
        {
            var more = int.Parse(
                extraBlocks.Groups["n"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture);

            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.Ability,
                Applies = (_, _, _) => true,

                // Added rather than assigned, exactly as the static reader adds: the rule says
                // "an additional", which is an amount and not a state.
                Apply = (_, _, builder) => builder.ExtraBlocks += more,
            };
        }

        if (string.Equals(definitionId, "switch-pt", StringComparison.Ordinal))
        {
            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.PowerToughnessSwitch,
                Applies = (_, _, _) => true,
                Apply = (_, _, builder) => builder.Switch(),
            };
        }

        // "Loses all abilities until end of turn" (CR 613.1f). Nothing to apply: the flag is
        // read on the way in, before any effect runs, and <see cref="CharacteristicsBuilder
        // .LoseAllAbilities"/> is called when layer 6 is reached. Writing the removal into
        // <c>Apply</c> instead would take the keywords off and leave the permanent's own statics
        // still being offered, which is half a removal and looks like a working card.
        //
        // Applies to whatever it was aimed at and asks nothing: a floating effect carries the
        // ids it affects, fixed when it was created (CR 611.2c, 613.7b).
        if (string.Equals(definitionId, "lose-abilities", StringComparison.Ordinal))
        {
            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.Ability,
                RemovesAllAbilities = true,
                Applies = (_, _, _) => true,
                Apply = (_, _, _) => { },
            };
        }

        if (string.Equals(definitionId, "no-untap", StringComparison.Ordinal))
        {
            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.Ability,
                Applies = (_, _, _) => true,
                Apply = (_, _, builder) => builder.DoesNotUntap = true,
            };
        }

        if (string.Equals(definitionId, "may-attack-defender", StringComparison.Ordinal))
        {
            return new ContinuousEffectDefinition
            {
                Id = definitionId,

                // Layer 6 is where the engine keeps every permission of this shape, and an
                // "as though" effect (CR 609.4) is not a characteristic change at all - it
                // modifies a rule. The builder flag is the seam, so it rides in the layer that
                // already carries the rest of what an ability grant does.
                Layer = EffectLayer.Ability,
                Applies = (_, _, _) => true,
                Apply = (_, _, builder) => builder.MayAttackAsThoughNoDefender = true,
            };
        }

        var retyped = BecomesCreatureTypeName().Match(definitionId);
        if (retyped.Success)
        {
            var named = retyped.Groups["t"].Value;
            var displaces = EffectPhrase.SubtypeSetOf(named);

            // Whether CR 305.7's second sentence is in play, read once here rather than on every
            // recomputation: the rule names the *basic* land types alone, so a land told it is a
            // Gate or a Locus keeps its text and only a Plains, Island, Swamp, Mountain or
            // Forest takes it away.
            var retypesALand = EffectPhrase.Specs.IsBasicLandType(named);

            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.Type,
                Applies = (_, _, _) => true,

                // CR 205.1a: the new subtype replaces the existing subtypes *from the appropriate
                // set* - creature types, land types, artifact types, and so on are separate sets
                // and a change to one leaves the others alone. Clearing the list wholesale meant
                // an animated basic land stopped being a Forest.
                Apply = (_, _, builder) =>
                {
                    builder.Subtypes.RemoveAll(
                        had => EffectPhrase.SubtypeSetOf(had) == displaces);

                    builder.Subtypes.Add(named);

                    // CR 305.7: setting a land's subtype to a basic land type is the one type
                    // change that takes the permanent's own text away as well - "it loses all
                    // abilities generated from its rules text, its old land types, and any
                    // copiable effects affecting that land". Marked here rather than worked out
                    // later because only this effect knows which half of the rule it is: the
                    // "in addition to its other types" spelling next door keeps every word, and
                    // the two ids exist to tell them apart.
                    if (retypesALand && builder.CardTypes.HasFlag(CardType.Land))
                        builder.LoseAbilitiesFromRulesText();
                },
            };
        }

        var alsoTyped = GainsCreatureTypeName().Match(definitionId);
        if (alsoTyped.Success)
        {
            var extra = alsoTyped.Groups["t"].Value;

            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.Type,
                Applies = (_, _, _) => true,

                // CR 205.1b: everything the permanent already was, plus this. Guarded against
                // saying it twice, because a permanent that already has the type reads the same
                // whether the effect is applying or not and a doubled entry would show in a view.
                Apply = (_, _, builder) =>
                {
                    if (!builder.Subtypes.Contains(extra, StringComparer.OrdinalIgnoreCase))
                        builder.Subtypes.Add(extra);
                },
            };
        }

        var recoloured = BecomesColorName().Match(definitionId);
        if (recoloured.Success)
        {
            var become = new List<ManaColor>();
            var readable = true;

            foreach (var name in recoloured.Groups["c"].Value.Split(
                ',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Enum.TryParse<ManaColor>(name, true, out var one))
                    become.Add(one);
                else
                    readable = false;
            }

            if (readable && become.Count > 0)
            {
                return new ContinuousEffectDefinition
                {
                    Id = definitionId,
                    Layer = EffectLayer.Color,
                    Applies = (_, _, _) => true,

                    // Setting, not adding (CR 105.2), so the whole set arrives at once. Written as
                    // one effect for that reason: a colour per effect meant the second cleared
                    // what the first wrote and a two-colour animation came out one colour.
                    Apply = (_, _, builder) =>
                    {
                        builder.Colors.Clear();

                        foreach (var colour in become)
                            builder.Colors.Add(colour);
                    },
                };
            }
        }

        var setPower = SetPowerName().Match(definitionId);
        if (setPower.Success)
        {
            var only = int.Parse(
                setPower.Groups["p"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);

            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.PowerToughnessSet,
                Applies = (_, _, _) => true,
                Apply = (_, _, builder) => builder.SetPower(only),
            };
        }

        var setPt = SetPowerToughnessName().Match(definitionId);
        if (setPt.Success)
        {
            var setP = int.Parse(setPt.Groups["p"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            var setT = int.Parse(setPt.Groups["t"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);

            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.PowerToughnessSet,
                Applies = (_, _, _) => true,
                Apply = (_, _, builder) => builder.Set(setP, setT),
            };
        }

        // Layer 4 again, and the other half of 205.1: this one *replaces* the types instead of
        // adding to them, which is the difference between a Licid that has become an Aura and a
        // Licid that is an Aura and a creature at once.
        if (string.Equals(definitionId, BecomesAuraId(), StringComparison.Ordinal))
        {
            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                Layer = EffectLayer.Type,
                Applies = (_, _, _) => true,
                Apply = (_, _, builder) =>
                {
                    builder.CardTypes = CardType.Enchantment;

                    // CR 205.1b: the subtypes that belonged to the card type this took away go
                    // with it. Cleared wholesale rather than filtered because a Licid has only
                    // creature subtypes to lose, and "Aura" is the one the new type brings.
                    builder.Subtypes.Clear();
                    builder.Subtypes.Add("Aura");
                },
            };
        }

        var becomes = BecomesName().Match(definitionId);
        if (becomes.Success)
        {
            var types = (CardType)int.Parse(
                becomes.Groups["t"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);

            return new ContinuousEffectDefinition
            {
                Id = definitionId,
                // Type-changing is layer 4, and it has to come before layer 6 and layer 7 so that
                // a lord pumping creatures sees the thing that has just become one (CR 613.1d).
                Layer = EffectLayer.Type,
                Applies = (_, _, _) => true,
                Apply = (_, _, builder) => builder.CardTypes |= types,
            };
        }

        var m = PumpName().Match(definitionId);
        if (!m.Success)
            return null;

        var power = int.Parse(m.Groups["p"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        var toughness = int.Parse(m.Groups["t"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);

        return new ContinuousEffectDefinition
        {
            Id = definitionId,
            Layer = EffectLayer.PowerToughnessModify,
            Applies = (_, _, _) => true,
            Apply = (_, _, builder) => builder.Modify(power, toughness),
        };
    }

    /// <summary>The card a copy effect's name carries, or null if it carries no readable one.</summary>
    /// <remarks>
    /// Null rather than a throw, because <see cref="Resolve"/>'s contract is that an id naming
    /// nothing resolves to nothing: an effect whose definition cannot be found simply does not
    /// apply, and a game that cannot be replayed at all is a worse answer than a permanent that
    /// is not a copy.
    /// </remarks>
    private static CardDefinition? CopiedCard(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<Copiable>(payload, CopyFormat)?.ToDefinition();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^pump:(?<p>[+-]\d+)/(?<t>[+-]\d+)$")]
    private static partial Regex PumpName();

    [GeneratedRegex(@"^pump-per:(?<p>[+-]\d+)/(?<t>[+-]\d+):(?<group>.+)$")]
    private static partial Regex PerEachPumpName();

    [GeneratedRegex(@"^pump-per-attacked:(?<p>[+-]\d+)/(?<t>[+-]\d+)$")]
    private static partial Regex MeleePumpName();

    [GeneratedRegex(@"^grant:(?<k>\d+)$")]
    private static partial Regex GrantName();

    [GeneratedRegex(@"^becomes:(?<t>\d+)$")]
    private static partial Regex BecomesName();

    [GeneratedRegex(@"^becomes-pt:(?<p>\d+)/(?<t>\d+)$")]
    private static partial Regex SetPowerToughnessName();

    [GeneratedRegex(@"^becomes-power:(?<p>\d+)$")]
    private static partial Regex SetPowerName();

    [GeneratedRegex(@"^becomes-color:(?<c>[a-z]+(,[a-z]+)*)$")]
    private static partial Regex BecomesColorName();

    [GeneratedRegex(@"^becomes-type:(?<t>[A-Za-z' -]+)$")]
    private static partial Regex BecomesCreatureTypeName();

    [GeneratedRegex(@"^gains-type:(?<t>[A-Za-z' -]+)$")]
    private static partial Regex GainsCreatureTypeName();

    [GeneratedRegex(@"^extra-blocks:(?<n>\d+)$")]
    private static partial Regex ExtraBlocksName();

    [GeneratedRegex(@"^must-block:(?<a>[0-9a-f]{32})$")]
    private static partial Regex MustBlockAttackerName();

    [GeneratedRegex(@"^cant-block:(?<a>[0-9a-f]{32})$")]
    private static partial Regex CantBlockAttackerName();

    [GeneratedRegex(@"^goaded:(?<p>[0-9a-f]{32}):(?<t>\d+)$")]
    private static partial Regex GoadedByName();

    [GeneratedRegex(@"^must-attack:(?<p>[0-9a-f]{32})$")]
    private static partial Regex MustAttackPlayerName();

    [GeneratedRegex(@"^control-while:(?<p>[0-9a-f]{32}):(?<s>[0-9a-f]{32}):(?<k>[a-z]+)$")]
    private static partial Regex ControlWhileName();

    /// <remarks>
    /// The wrapped id is the whole of the tail, colons and all — a <c>pump:+2/+2</c> or a
    /// serialised copied card go through here unchanged.
    /// </remarks>
    [GeneratedRegex(
        @"^while:(?<k>[a-z]+):(?<p>[0-9a-f]{32}):(?<s>[0-9a-f]{32}):(?<inner>.+)$")]
    private static partial Regex HeldWhileName();

    [GeneratedRegex(@"^control:(?<p>[0-9a-f]{32})$")]
    private static partial Regex ControlName();
}

/// <summary>
/// What an "except" clause changes about a copied card (CR 707.9b).
/// </summary>
/// <remarks>
/// Data rather than a <c>Func&lt;CardDefinition, CardDefinition&gt;</c>, because two different
/// kinds of thing have to hold one of these: the replacement effect that turns an arriving
/// permanent into a copy, which is built once at compile time and may hold a delegate quite
/// happily, and <c>CreateTokenCopy</c>, which is a record in a compiled spell's effect list and
/// has to compare like one. One shape for both is the whole point — the alternative was
/// <c>CreateTokenCopy</c> growing a private <c>ExceptNotLegendary</c> flag beside a full
/// exception parser it could not reach, which is the "vocabulary restated in a second pattern"
/// bug this codebase keeps paying for.
/// <para>
/// It is applied by <see cref="GenerativeEffects.Excepting"/> and nowhere else: an exception is
/// not a rule the engine enforces, it is two words different on the card the copy machinery is
/// handed.
/// </para>
/// </remarks>
public sealed record CopyException
{
    /// <summary>Card types the clause adds — "an artifact in addition to its other types".</summary>
    public CardType AddTypes { get; init; }

    /// <summary>Subtypes the clause adds, keeping the copied card's own.</summary>
    public IReadOnlyList<string> AddSubtypes { get; init; } = [];

    /// <summary>Subtypes the clause replaces the copied card's with — "it's a 1/1 green Frog".</summary>
    public IReadOnlyList<string> SetSubtypes { get; init; } = [];

    /// <summary>Whether the copy loses the legendary supertype (CR 707.9b).</summary>
    public bool DropLegendary { get; init; }

    public int? Power { get; init; }

    public int? Toughness { get; init; }

    /// <summary>Keywords the clause grants — "except it has haste".</summary>
    public KeywordAbility AddKeywords { get; init; }

    /// <summary>
    /// Rules text the clause grants — "except it has 'At the beginning of the end
    /// step, sacrifice this token'" (CR 707.9b).
    /// </summary>
    /// <remarks>
    /// A quoted ability is not a keyword and there is no flag for it, so it travels as the words
    /// the card printed and is compiled from the amended card's own text, exactly as the quoted
    /// ability on a minted token is. <see cref="Abilities.TokenCards.Granting"/> is what puts it
    /// there, and why the amended card needs an id of its own.
    /// <para>
    /// Held as lines rather than one string because a clause may grant more than one ability, and
    /// a card's rules text is a list of lines everywhere else in this compiler.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> GrantedText { get; init; } = [];

    /// <summary>Colours added by "in addition to its other colors and types".</summary>
    public IReadOnlyList<ManaColor> AddColors { get; init; } = [];

    /// <summary>Colours that replace the copied card's, which is the commoner templating.</summary>
    public IReadOnlyList<ManaColor> SetColors { get; init; } = [];
}
