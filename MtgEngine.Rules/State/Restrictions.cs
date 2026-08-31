using System.Collections.Immutable;
using MtgEngine.Rules.Abilities;

namespace MtgEngine.Rules.State;

/// <summary>
/// A ban on gaining life (CR 119.7).
/// </summary>
/// <remarks>
/// Not a replacement effect and deliberately not modelled as one. CR 119.7 says a replacement
/// effect that would replace a life gain event affecting a banned player "won't do anything" —
/// so a ban sits <em>outside</em> the replacement pass rather than competing inside it. Modelled
/// as a candidate there it would be offered to CR 616.1's ordering question, and a prohibition
/// is not something a player chooses the order of.
/// <para>
/// The same record serves both lifetimes, exactly as <see cref="PreventionEffect"/> does:
/// "Players can't gain life this turn" is a one-shot a resolving spell creates and the cleanup
/// step sweeps, while "Players can't gain life" printed on a permanent is a static ability read
/// off the battlefield every time the question is asked. Only <see cref="UntilEndOfTurn"/>
/// differs, and a static one never reaches this list at all.
/// </para>
/// </remarks>
public sealed record LifeGainBan
{
    public required Guid Id { get; init; }

    /// <summary>Whoever created it, which is who "your opponents" is read around.</summary>
    public required Guid ControllerId { get; init; }

    /// <summary>A described set of players — "players", "your opponents".</summary>
    public PlayerScope? Players { get; init; }

    /// <summary>One named player, for a ban that landed on somebody in particular.</summary>
    /// <remarks>
    /// An identity rather than a description for the reason <see cref="PreventionEffect.Source"/>
    /// is one: the player was decided when the ban was made, and re-deriving them from a
    /// description would move the ban when the board moved.
    /// </remarks>
    public Guid? Player { get; init; }

    /// <summary>The turn it ends at the end of, or null for one with no duration.</summary>
    public int? UntilEndOfTurn { get; init; }
}

/// <summary>
/// Damage a permanent's static ability makes unpreventable (CR 615.12).
/// </summary>
/// <remarks>
/// A template rather than a finished <see cref="PreventionEffect"/>, because the two things it
/// can say about the source are questions about the permanent that prints it: Excruciator's
/// "damage that would be dealt by ~" is an identity that is only known once there is an object,
/// and Questing Beast's "creatures you control" is read around that object's controller
/// (CR 613.1b — the controller now, not the one it entered under). Bound to a host at the moment
/// the question is asked, which is what makes the ban stop when the permanent does (CR 611.2c).
/// </remarks>
public sealed record UnpreventableStatic
{
    public required string Id { get; init; }

    /// <summary>Which damage it unshields — all of it, or combat damage only.</summary>
    public DamageKind Kind { get; init; }

    /// <summary>True for "damage that would be dealt by ~": the host is the source.</summary>
    public bool DealtBySource { get; init; }

    /// <summary>A filter the damage's source has to answer — "dealt by creatures".</summary>
    public string? SourceFilter { get; init; }

    /// <summary>Whose sources <see cref="SourceFilter"/> counts, or null for anyone's.</summary>
    public PlayerScope? SourceController { get; init; }
}

/// <summary>
/// A ban on countering a described group of spells (CR 701.6a).
/// </summary>
/// <remarks>
/// The other half of the parameterised-keyword problem, and it needs a different answer from the
/// one <see cref="TargetRestriction"/> gives. There, the parameter describes the <em>source</em>
/// doing the targeting, so it cannot be a characteristic of the thing being protected and has to
/// be a predicate. Here the parameter describes the protected thing itself — "creature spells you
/// control" — and the engine already has a keyword saying exactly what happens to it. So nothing
/// new is needed on the spell at all: what was missing was a way for one permanent to say the
/// keyword about a group, and this is that, read off the battlefield at the moment a counter
/// would happen.
/// <para>
/// A ban rather than a continuous effect for the reason <see cref="StaticBans"/> exists: a spell
/// on the stack is not a permanent, CR 613's layers are about permanents' characteristics, and
/// running the whole layer computation over the stack to set one flag would be a large change to
/// answer a question that is only ever asked at one moment.
/// </para>
/// </remarks>
public sealed record CounterBan
{
    public required string Id { get; init; }

    /// <summary>A filter the spell has to answer — "creature", "instant|sorcery".</summary>
    /// <remarks>Null for "spells can't be countered", which asks nothing of the spell.</remarks>
    public string? SpellFilter { get; init; }

    /// <summary>Whose spells it covers, or null for anybody's.</summary>
    public PlayerScope? Controller { get; init; }
}

/// <summary>
/// A ban on casting or activating anything with the name this permanent chose (CR 201.4).
/// </summary>
/// <remarks>
/// The parameter is not printed on the card at all: Meddling Mage says "spells with the chosen
/// name", and which name that is was decided by a player as the permanent entered
/// (CR 614.12a). So the ban carries no name and the name is read off the <em>host object</em>
/// at the moment the question is asked - which is also what makes it stop when the permanent
/// leaves, and what makes a permanent that entered again choose again.
/// <para>
/// A ban rather than a continuous effect for the reason the rest of <see cref="StaticBans"/> is
/// one: a spell on the stack is not a permanent, and CR 613's layers order objects'
/// characteristics. Nothing here changes a characteristic.
/// </para>
/// </remarks>
public sealed record ChosenNameBan
{
    public required string Id { get; init; }

    /// <summary>
    /// Whose casting it forbids, read around whoever controls the permanent, or null for
    /// everybody's.
    /// </summary>
    /// <remarks>
    /// Nevermore's "spells with the chosen name can't be cast" stops its own controller too and
    /// is the null case; Gideon's Intervention says "your opponents can't cast" and is the
    /// scoped one. Defaulting a missing subject to the controller is the mistake recorded one
    /// file over, so there is no default that means "guess".
    /// </remarks>
    public Abilities.PlayerScope? Who { get; init; }

    /// <summary>Whether mana abilities are exempt (CR 605.1a).</summary>
    /// <remarks>
    /// Pithing Needle prints the exemption and Phyrexian Revoker does not, and the difference is
    /// the whole of what separates the two cards. Read rather than assumed in either direction:
    /// assuming the exemption makes a Revoker that leaves mana rocks alone, and assuming its
    /// absence makes a Needle that turns off a Sol Ring.
    /// </remarks>
    public bool ExceptManaAbilities { get; init; }
}

/// <summary>
/// A number read off the board at the moment a declaration is made (CR 107.3).
/// </summary>
/// <remarks>
/// A declaration is not a resolution, so <see cref="Abilities.Amount"/> cannot serve here: its
/// <see cref="Abilities.Amount.Counter"/> is handed a <see cref="Abilities.ResolutionContext"/>,
/// and CR 508.1 is a turn-based action with no spell, no ability and no stack object to build one
/// from. What is left when that context is taken away is exactly this — the board, whoever is
/// asking, and the permanent doing the asking.
/// <para>
/// Declared here rather than reusing the compiler's own counting delegate so that
/// <c>MtgEngine.Rules.State</c> does not learn about <c>MtgEngine.Rules.Cards</c>: the compiler
/// reads state, and the dependency may not run the other way. The compiler adapts its shared
/// counting vocabulary to this — the <em>same</em> vocabulary, entered from a caller that has no
/// resolution, rather than a second one written out here.
/// </para>
/// </remarks>
public delegate int BoardCount(
    GameState state, Abilities.IAbilitySource abilities, Guid you, ObjectId source);

/// <summary>Which declaration a combat tax is charged for (CR 508.1h, 509.1d).</summary>
public enum TaxedDeclaration
{
    /// <summary>"Creatures can't attack you unless their controller pays {2}."</summary>
    Attack,

    /// <summary>"~ can't block unless you pay {2}."</summary>
    Block,
}

/// <summary>Which creatures a combat tax charges for (CR 508.1c, 509.1b).</summary>
/// <remarks>
/// The subject is an enum rather than a filter because the four printings are four different
/// questions, and only one of them is about the creature's characteristics at all. "Creatures
/// can't attack <em>you</em>" asks who is being attacked; "~ can't attack" asks whether the
/// creature is the permanent printing the line; "Enchanted creature can't attack" asks what the
/// Aura is on. A filter vocabulary can answer none of those, and widening any of them to "every
/// creature" makes a card strictly stronger than the one printed.
/// </remarks>
public enum TaxedCreatures
{
    /// <summary>
    /// Every creature declared as attacking the host's controller — Ghostly Prison.
    /// </summary>
    /// <remarks>
    /// A creature attacking a planeswalker is not attacking its controller (CR 508.1b), so this
    /// arm deliberately does not cover one. The cards that mean both say so in print, and that
    /// is <see cref="DefendingWithPlaneswalkers"/>.
    /// </remarks>
    Defending,

    /// <summary>
    /// "…you or planeswalkers you control" — Baird, Archon of Absolution, Sphere of Safety.
    /// </summary>
    DefendingWithPlaneswalkers,

    /// <summary>
    /// "Creatures can't attack planeswalkers you control unless…" — Onakke Oathkeeper, and only
    /// the planeswalker half.
    /// </summary>
    DefendingPlaneswalkersOnly,

    /// <summary>"~ can't attack unless you pay {2}" — the permanent printing the line.</summary>
    Host,

    /// <summary>"Enchanted creature can't attack unless its controller pays {3}" — Brainwash.</summary>
    Attached,
}

/// <summary>
/// A price a creature's controller pays to declare it as an attacker or a blocker
/// (CR 508.1h, 509.1d).
/// </summary>
/// <remarks>
/// <b>Not a characteristic, and so not a continuous effect.</b> This is the same argument
/// <see cref="FlashPermission"/> makes and it lands in the same place: CR 613.1 orders the
/// effects that change objects' characteristics, and a creature under a Ghostly Prison has
/// exactly the abilities, power and types it had a moment ago. What the permanent changes is a
/// rule of play — what the active player has to do to make a declaration — so it is read off the
/// battlefield at the one moment the question is asked, which is also what makes it stop dead
/// when the permanent leaves (CR 611.2c) with no state to sweep.
/// <para>
/// <b>Nor is it a cost modifier.</b> <see cref="Abilities.CostModifier"/> changes what a spell or
/// an activated ability costs, and both of those are objects that go on the stack and can be
/// responded to. A declaration is a turn-based action with no stack object at all (CR 508.1), so
/// there is nothing there for a modifier to modify; the price is not an increase to something,
/// it is the whole cost.
/// </para>
/// <para>
/// <b>The price is per creature, and that is what the printed count says.</b> Every group
/// printing in the corpus reads "{2} <em>for each creature they control that's attacking you</em>"
/// or "for each of those creatures", which is one price multiplied by the creatures this tax
/// covers in that declaration — so the multiplication is the declaration's own arithmetic rather
/// than a counted quantity anything has to evaluate. A phrase that counts something else is left
/// unread by the compiler rather than approximated by this.
/// </para>
/// <para>
/// It lives in <see cref="StaticBans"/> rather than beside <see cref="FlashPermission"/> because
/// the polarity is a refusal: the printed sentence is "creatures <em>can't</em> attack you", and
/// the payment is the only way out of it. A tax nothing charges is a permanent that forbids
/// nothing, which is exactly what the empty list means everywhere else in that record.
/// </para>
/// </remarks>
public sealed record CombatTax
{
    public required string Id { get; init; }

    /// <summary>What one covered creature costs to declare.</summary>
    /// <remarks>
    /// A parsed cost rather than a number, so a coloured price would be charged as printed. The
    /// compiler refuses a variable, hybrid or Phyrexian symbol: the pool arithmetic cannot offer
    /// the choice a hybrid asks for at a moment with no stack object to hold the question, and a
    /// price silently read as its generic half is a Norn's Annex that anybody can walk past.
    /// </remarks>
    public required Mana.ManaCostSpec Price { get; init; }

    /// <summary>
    /// A board count that multiplies <see cref="Price"/>, or null for a price printed outright.
    /// </summary>
    /// <remarks>
    /// Sphere of Safety's "{X} for each of those creatures, where X is the number of enchantments
    /// you control" and Phyrexian Marauder's "{1} for each +1/+1 counter on it" are the same
    /// shape: a unit price times something on the board. The count is taken when the declaration
    /// is made and never again — CR 508.1h locks the total in, and an enchantment that leaves
    /// afterwards does not refund anybody.
    /// <para>
    /// The unit is <see cref="Price"/>'s generic part, so a counted tax is generic mana by
    /// construction; the compiler refuses a coloured unit rather than multiplying a pip, which is
    /// not a thing CR 107.3 says how to do and no card prints.
    /// </para>
    /// </remarks>
    public BoardCount? Count { get; init; }

    /// <summary>Whether it is charged at the declaration of attackers or of blockers.</summary>
    public required TaxedDeclaration Declaration { get; init; }

    /// <summary>Which creatures in that declaration it charges for.</summary>
    public required TaxedCreatures Subject { get; init; }
}

/// <summary>
/// What a card's static abilities forbid outright (CR 119.7, 615.12).
/// </summary>
/// <remarks>
/// Held apart from <see cref="ContinuousEffectDefinition"/> because a prohibition changes no
/// characteristic and so has no place in CR 613's layers, and apart from
/// <see cref="ReplacementEffectDefinition"/> because it replaces no event: it is a question the
/// engine asks at one particular moment, and the answer comes from whatever is on the
/// battlefield then. One record rather than two interface members, so a card that prints both
/// bans in two lines — Leyline of Punishment does — is answered by one lookup.
/// </remarks>
public sealed record StaticBans
{
    /// <summary>A card that forbids nothing, which is nearly every card.</summary>
    public static StaticBans None { get; } = new();

    /// <summary>Damage this card makes unpreventable while it is on the battlefield.</summary>
    public ImmutableList<UnpreventableStatic> Unpreventable { get; init; } = [];

    /// <summary>Who this card stops from gaining life while it is on the battlefield.</summary>
    public ImmutableList<PlayerScope> NoLifeGain { get; init; } = [];

    /// <summary>Which spells this card stops being countered while it is on the battlefield.</summary>
    public ImmutableList<CounterBan> NoCounter { get; init; } = [];

    /// <summary>Who this card stops casting a spell with the name it chose (CR 201.4).</summary>
    public ImmutableList<ChosenNameBan> NoCastingNamed { get; init; } = [];

    /// <summary>
    /// Whether this card stops activated abilities of sources with the name it chose
    /// (CR 602.5).
    /// </summary>
    public ImmutableList<ChosenNameBan> NoActivatingNamed { get; init; } = [];

    /// <summary>
    /// What this card charges to declare an attacker or a blocker (CR 508.1h, 509.1d).
    /// </summary>
    public ImmutableList<CombatTax> CombatTaxes { get; init; } = [];

    /// <summary>Whether this card forbids nothing at all, asked before anything is built.</summary>
    public bool IsEmpty =>
        Unpreventable.IsEmpty
        && NoLifeGain.IsEmpty
        && NoCounter.IsEmpty
        && NoCastingNamed.IsEmpty
        && NoActivatingNamed.IsEmpty
        && CombatTaxes.IsEmpty;
}

/// <summary>Whether a prohibition covers a player (CR 119.7).</summary>
public static class Bans
{
    /// <summary>
    /// Every prohibition a permanent on the battlefield is printing right now.
    /// </summary>
    /// <remarks>
    /// CR 613.1f: a permanent that has lost all abilities forbids nothing either, which is the
    /// same exclusion the replacement scan makes and for the same reason. The card asked is the
    /// object's own rather than a computed copy: a prohibition is not a characteristic, so
    /// nothing in CR 613 can move it from one card to another.
    /// <para>
    /// Here rather than on <c>Game</c> because two callers now need it and neither is the other's
    /// business: the life-gain and prevention questions are the engine's, and
    /// <see cref="CannotBeCountered"/> is asked from inside a resolving effect, which holds a
    /// state and an ability source and no game. A second scan written there would be the same
    /// list one repository further out.
    /// </para>
    /// </remarks>
    public static IEnumerable<(GameObject Host, Guid ControllerId, StaticBans Bans)> InPlay(
        GameState state, Abilities.IAbilitySource abilities)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);

        foreach (var id in state.Battlefield)
        {
            if (!state.TryGetObject(id, out var host))
                continue;

            var bans = abilities.BansOf(host.Card);
            if (bans.IsEmpty || !StillSpeaks(state, abilities, host))
                continue;

            yield return (host, Characteristics.ControllerOf(state, abilities, host), bans);
        }
    }

    /// <summary>
    /// Whether this permanent's static abilities still say anything (CR 613.1f).
    /// </summary>
    /// <remarks>
    /// One method rather than a condition written twice, because the two callers must agree:
    /// <see cref="InPlay"/> asks it of what a permanent forbids and
    /// <see cref="CastPermissions"/> asks it of what a permanent permits, and a permanent that
    /// has lost all its abilities has to fall silent in both directions at once. Written
    /// separately in each place, the day one of them learned about a new way to lose abilities
    /// would be the day the other stopped agreeing with it.
    /// <para>
    /// Its callers ask it <em>after</em> checking that the permanent has something to say, and
    /// that order is deliberate: this computes a full set of characteristics, and the scans it
    /// serves run over every permanent on the board at moments the engine passes through
    /// constantly.
    /// </para>
    /// </remarks>
    internal static bool StillSpeaks(
        GameState state, Abilities.IAbilitySource abilities, GameObject host) =>
        !Characteristics.Of(state, abilities, host).HasLostAllAbilities;

    /// <summary>Whether this spell on the stack can't be countered (CR 701.6a).</summary>
    /// <remarks>
    /// Both spellings in one place: the keyword a spell says about itself, and the group ban a
    /// permanent says about somebody's spells. The two counter effects each asked the keyword
    /// alone, in a line of their own — which is how "creature spells you control can't be
    /// countered" could be printed on eleven cards and enforced on none of them.
    /// <para>
    /// The keyword is asked of the printed card rather than of computed characteristics, exactly
    /// as it was: CR 613's layers describe permanents, and this matters while the thing is still
    /// a spell on the stack.
    /// </para>
    /// </remarks>
    public static bool CannotBeCountered(
        GameState state, Abilities.IAbilitySource abilities, GameObject spell)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);
        ArgumentNullException.ThrowIfNull(spell);

        if (abilities.GrantedKeywords(spell.Card)
            .HasFlag(Domain.Enums.KeywordAbility.CantBeCountered))
        {
            return true;
        }

        foreach (var ban in InPlay(state, abilities))
        {
            foreach (var said in ban.Bans.NoCounter)
            {
                if (said.SpellFilter is { } filter
                    && !SearchFilters.Matches(filter, spell.Card))
                {
                    continue;
                }

                // CR 613.1b: "you control" is read around whoever controls the permanent now,
                // which is why the scope is resolved here and not when the card was compiled.
                if (said.Controller is not { } scope
                    || PlayerScopes.Around(scope, state, ban.ControllerId)
                        .Contains(spell.ControllerId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The permanent forbidding this player from casting this card by name, or null (CR 601.3).
    /// </summary>
    /// <remarks>
    /// The name is taken from the host object and the card being cast is compared to it, so a
    /// host that has not been asked yet - <see cref="GameObject.ChosenName"/> null - forbids
    /// nothing. That arm is the one this whole family turns on: read the other way round, a
    /// Meddling Mage entering would stop every spell in the game.
    /// <para>
    /// The comparison is against the printed name and never against a filter id (CR 201.2). A
    /// name is capitalised by definition, and handing one to <c>SearchFilters</c> would ask
    /// whether the card has a <em>subtype</em> spelled that way - which is how a card once
    /// acquired a ban on a creature type called Green.
    /// </para>
    /// </remarks>
    public static GameObject? CastingForbidden(
        GameState state,
        Abilities.IAbilitySource abilities,
        Domain.Models.CardDefinition casting,
        Guid playerId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);
        ArgumentNullException.ThrowIfNull(casting);

        foreach (var ban in InPlay(state, abilities))
        {
            foreach (var said in ban.Bans.NoCastingNamed)
            {
                if (!NamesTheSame(ban.Host.ChosenName, casting.Name))
                    continue;

                // CR 613.1b: "your opponents" is read around whoever controls the permanent
                // now, which is why the scope is resolved here and not when the card compiled.
                if (said.Who is not { } scope
                    || PlayerScopes.Around(scope, state, ban.ControllerId).Contains(playerId))
                {
                    return ban.Host;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The permanent forbidding this source's activated ability by name, or null (CR 602.5).
    /// </summary>
    /// <remarks>
    /// "Sources with the chosen name" is any source in any zone, so the ability's source is
    /// asked by name rather than by where it is - which is what makes a Pithing Needle naming a
    /// card in a graveyard do what it says.
    /// <para>
    /// The <em>computed</em> name, unlike the ban itself: a permanent that is a copy of Sol
    /// Ring is named Sol Ring (CR 707.2), and a Needle that named Sol Ring has to stop it.
    /// Reading <c>obj.Card.Name</c> here is the mistake this engine has recorded nine times
    /// over - a printed field where a characteristic was meant.
    /// </para>
    /// </remarks>
    public static GameObject? ActivatingForbidden(
        GameState state,
        Abilities.IAbilitySource abilities,
        GameObject source,
        bool isManaAbility)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);
        ArgumentNullException.ThrowIfNull(source);

        var activating = Characteristics.CardOf(state, abilities, source);

        foreach (var ban in InPlay(state, abilities))
        {
            foreach (var said in ban.Bans.NoActivatingNamed)
            {
                // CR 605.1a: the exemption the Needle prints and the Revoker does not.
                if (said.ExceptManaAbilities && isManaAbility)
                    continue;

                if (NamesTheSame(ban.Host.ChosenName, activating.Name))
                    return ban.Host;
            }
        }

        return null;
    }

    /// <summary>Whether the name a permanent chose is this card's name (CR 201.2).</summary>
    /// <remarks>
    /// One place, because everything that asks it must agree about the null - and about the
    /// fact that null is "nothing was named" rather than "everything matches". Three callers
    /// now: the two scans above and the cost modifiers, which tax by the same name.
    /// </remarks>
    public static bool NameMatches(GameObject host, string cardName)
    {
        ArgumentNullException.ThrowIfNull(host);

        return NamesTheSame(host.ChosenName, cardName);
    }

    private static bool NamesTheSame(string? chosen, string cardName) =>
        chosen is { Length: > 0 }
        && string.Equals(chosen, cardName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a life-gain ban stops this player gaining life.</summary>
    public static bool Covers(LifeGainBan ban, GameState state, Guid playerId)
    {
        ArgumentNullException.ThrowIfNull(ban);
        ArgumentNullException.ThrowIfNull(state);

        return ban.Player == playerId
            || (ban.Players is { } scope
                && PlayerScopes.Around(scope, state, ban.ControllerId).Contains(playerId));
    }
}

/// <summary>
/// Spells a permanent lets somebody cast any time they could cast an instant (CR 702.8b).
/// </summary>
/// <remarks>
/// **A timing permission is not a characteristic, so it is not a continuous effect in CR 613's
/// sense.** "You may cast creature spells as though they had flash" changes nothing about the
/// cards it names — a creature card in hand under a Vedalken Orrery has no more abilities than
/// it had a moment ago, and if it is countered, exiled or discarded it takes nothing with it.
/// What the permanent changes is a *rule of play*, and CR 613.1 orders only the effects that
/// modify objects' characteristics. Compiled as a layer-6 keyword grant it would have been a
/// lie that happened to work: the flash keyword would show up wherever a card in hand is asked
/// what it is, and a card that gained flash would keep it on the stack and on the battlefield,
/// where the printed permission says nothing at all.
/// <para>
/// So it is read the way <see cref="CounterBan"/> is read — off the battlefield, at the one
/// moment the question is asked, from whatever is there right then. That is also what makes it
/// stop dead when the permanent leaves (CR 611.2c), with no state anywhere to sweep, and it is
/// the same treatment <see cref="GameObject.MayCastFree"/> gives the other half of the
/// offer-a-cast family: a permission is a fact about the moment, not an act performed inside a
/// resolution.
/// </para>
/// <para>
/// It is a list of its own rather than a member of <see cref="StaticBans"/> because the
/// polarity is the whole difference. Everything in that record refuses something, and a
/// permission filed among refusals is one wrong <c>IsEmpty</c> away from a permanent that
/// forbids what it was printed to allow.
/// </para>
/// </remarks>
public sealed record FlashPermission
{
    public required string Id { get; init; }

    /// <summary>
    /// A filter the spell has to answer — "creature", "Spirit", "artifact|enchantment".
    /// </summary>
    /// <remarks>
    /// Null only for the unfiltered printing, "You may cast spells as though they had flash",
    /// which is Leyline of Anticipation and asks nothing of the spell. A qualifier the filter
    /// vocabulary cannot name leaves the whole line unread rather than widening to null — a
    /// permission that covers more spells than the card names is strictly better than the
    /// printed card, and this one would let a player cast sorceries on an opponent's turn.
    /// </remarks>
    public string? SpellFilter { get; init; }

    /// <summary>Whose casting it permits, or null for anybody's — "Any player may cast".</summary>
    /// <remarks>
    /// There is no default that means "guess". Quick Sliver and Vernal Equinox hand the window
    /// to the whole table and every other printing hands it to one seat, so a missing subject
    /// is a line this reader does not understand rather than a scope it picks.
    /// </remarks>
    public PlayerScope? Caster { get; init; }
}

/// <summary>Whether something on the battlefield permits a cast (CR 601.3, 702.8b).</summary>
/// <remarks>
/// The mirror image of <see cref="Bans"/> and kept beside it deliberately: both answer a
/// question about one moment by walking the battlefield, and both have to agree about which
/// permanents are still speaking (CR 613.1f). That agreement is <see cref="Bans.StillSpeaks"/>,
/// which is one method rather than two copies of a condition.
/// </remarks>
public static class CastPermissions
{
    /// <summary>
    /// Whether anything on the battlefield lets this player cast this card as though it had
    /// flash (CR 702.8b).
    /// </summary>
    /// <remarks>
    /// The card is asked by its printed characteristics, exactly as <see cref="Bans"/> asks a
    /// spell it might protect: the object is in a hand, and CR 613's layers describe permanents.
    /// <para>
    /// **It grants timing and nothing else.** The zone the card may be cast from, its cost, and
    /// whether it may be cast at all are all decided elsewhere in <c>Game.CastSpell</c> and none
    /// of them consults this. A permission that answered any of those questions would be a free
    /// cast wearing a timing permission's clothes.
    /// </para>
    /// </remarks>
    public static bool MayCastAsThoughItHadFlash(
        GameState state,
        Abilities.IAbilitySource abilities,
        Guid casterId,
        Domain.Models.CardDefinition casting)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);
        ArgumentNullException.ThrowIfNull(casting);

        foreach (var id in state.Battlefield)
        {
            if (!state.TryGetObject(id, out var host))
                continue;

            var granted = abilities.FlashPermissionsOf(host.Card);
            if (granted.Count == 0 || !Bans.StillSpeaks(state, abilities, host))
                continue;

            foreach (var said in granted)
            {
                if (said.SpellFilter is { } filter && !SearchFilters.Matches(filter, casting))
                    continue;

                // CR 613.1b: "you" is whoever controls the permanent now, not whoever cast it,
                // which is why the scope is resolved here and not when the card was compiled -
                // a stolen Vedalken Orrery hands its window to the thief.
                if (said.Caster is not { } scope
                    || PlayerScopes.Around(
                            scope, state, Characteristics.ControllerOf(state, abilities, host))
                        .Contains(casterId))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>
/// What a declaration of attackers or blockers costs (CR 508.1h, 509.1d).
/// </summary>
/// <remarks>
/// Kept beside <see cref="Bans"/> and <see cref="CastPermissions"/> because it is the third
/// question of the same shape: walk the battlefield at one moment, ask what is standing there,
/// and answer from that. It shares their agreement about which permanents are still speaking
/// (CR 613.1f) through <see cref="Bans.InPlay"/>, so a permanent that has lost all its abilities
/// stops taxing on exactly the turn it stops forbidding.
/// <para>
/// <b>One total for the whole declaration, not one per creature.</b> CR 508.1h works out a total
/// cost and locks it in, and CR 508.1j forbids a partial payment — so a declaration into two
/// Propagandas with three attackers is a single {12} that is paid or refused whole. Summing here
/// rather than charging creature by creature is what makes the refusal leave nothing spent.
/// </para>
/// </remarks>
public static class CombatTaxes
{
    /// <summary>The total price of declaring these attackers (CR 508.1h).</summary>
    public static Mana.ManaCostSpec ToAttack(
        GameState state,
        Abilities.IAbilitySource abilities,
        IReadOnlyDictionary<ObjectId, AttackTarget> attackers)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);
        ArgumentNullException.ThrowIfNull(attackers);

        var total = Mana.ManaCostSpec.Free;

        foreach (var (host, controllerId, bans) in Bans.InPlay(state, abilities))
        {
            foreach (var tax in bans.CombatTaxes)
            {
                if (tax.Declaration != TaxedDeclaration.Attack)
                    continue;

                foreach (var (attackerId, target) in attackers)
                {
                    if (Charges(state, abilities, tax, host, controllerId, attackerId, target))
                        total = total.Plus(PriceOf(state, abilities, tax, host, controllerId));
                }
            }
        }

        return total;
    }

    /// <summary>The total price of declaring these blockers (CR 509.1d).</summary>
    /// <remarks>
    /// The blockers are handed over flat rather than as the attacker-to-blockers map the
    /// declaration is made with, because nothing this asks is about what is being blocked: every
    /// printing the compiler reads taxes the blocking creature itself. The one printing that asks
    /// about the attacker — Hipparion's "can't block creatures with power 3 or greater" — is left
    /// unread for exactly that reason rather than charged as though it had said nothing.
    /// </remarks>
    public static Mana.ManaCostSpec ToBlock(
        GameState state,
        Abilities.IAbilitySource abilities,
        IEnumerable<ObjectId> blockers)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(abilities);
        ArgumentNullException.ThrowIfNull(blockers);

        var declared = blockers.ToList();
        var total = Mana.ManaCostSpec.Free;

        foreach (var (host, controllerId, bans) in Bans.InPlay(state, abilities))
        {
            foreach (var tax in bans.CombatTaxes)
            {
                if (tax.Declaration != TaxedDeclaration.Block)
                    continue;

                foreach (var blockerId in declared)
                {
                    if (Charges(state, abilities, tax, host, controllerId, blockerId, target: null))
                        total = total.Plus(PriceOf(state, abilities, tax, host, controllerId));
                }
            }
        }

        return total;
    }

    /// <summary>What one covered creature costs right now (CR 107.3, 508.1h).</summary>
    /// <remarks>
    /// A count that comes to nought makes the tax free, and that is the printed card rather than
    /// a failure: a Sphere of Safety with no enchantments beside it really does let everything
    /// through. The direction that would be wrong — a phrase the compiler could not read quietly
    /// coming back as nought — cannot happen here, because such a line never becomes a tax at all.
    /// </remarks>
    private static Mana.ManaCostSpec PriceOf(
        GameState state,
        Abilities.IAbilitySource abilities,
        CombatTax tax,
        GameObject host,
        Guid hostControllerId) =>
        tax.Count is not { } count
            ? tax.Price
            : Mana.ManaCostSpec.Free.PlusGeneric(
                Math.Max(0, tax.Price.GenericPart * count(state, abilities, hostControllerId, host.Id)));

    /// <summary>Whether this tax is charged for this creature in this declaration.</summary>
    /// <remarks>
    /// The defending arms read the host's controller <em>now</em> rather than the one it entered
    /// under (CR 613.1b): a stolen Ghostly Prison protects the thief, and reading
    /// <c>host.ControllerId</c> here would have it go on guarding the player who lost it.
    /// </remarks>
    private static bool Charges(
        GameState state,
        Abilities.IAbilitySource abilities,
        CombatTax tax,
        GameObject host,
        Guid hostControllerId,
        ObjectId creatureId,
        AttackTarget? target) =>
        tax.Subject switch
        {
            TaxedCreatures.Host => creatureId == host.Id,
            TaxedCreatures.Attached => host.Permanent?.AttachedTo == creatureId,
            TaxedCreatures.Defending =>
                target is { IsPlaneswalker: false } player
                && player.DefendingPlayer == hostControllerId,
            TaxedCreatures.DefendingWithPlaneswalkers =>
                target is { } either
                && either.DefendingPlayer == hostControllerId
                && (!either.IsPlaneswalker
                    || IsPlaneswalkerControlledBy(state, abilities, either, hostControllerId)),
            TaxedCreatures.DefendingPlaneswalkersOnly =>
                target is { IsPlaneswalker: true } walker
                && walker.DefendingPlayer == hostControllerId
                && IsPlaneswalkerControlledBy(state, abilities, walker, hostControllerId),
            _ => false,
        };

    /// <summary>
    /// Whether the permanent being attacked is a planeswalker that player controls (CR 508.1b).
    /// </summary>
    /// <remarks>
    /// Asked rather than assumed from the target slot, because that slot also holds battles — and
    /// a battle is attacked through its <em>protector</em>, not its controller (CR 310.9d), so a
    /// tax on "planeswalkers you control" that took the slot at face value would charge for an
    /// attack on a Siege the taxing player does not control and never named.
    /// </remarks>
    private static bool IsPlaneswalkerControlledBy(
        GameState state, Abilities.IAbilitySource abilities, AttackTarget target, Guid playerId) =>
        state.TryGetObject(target.Planeswalker, out var permanent)
        && permanent.Zone == Zone.Battlefield
        && Characteristics.CardOf(state, abilities, permanent).CardTypes
            .HasFlag(Domain.Enums.CardType.Planeswalker)
        && Characteristics.ControllerOf(state, abilities, permanent) == playerId;
}
