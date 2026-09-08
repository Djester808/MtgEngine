using MtgEngine.Cards;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Views;

/// <summary>
/// What one player is allowed to see. Built by the projector from the state; never the state
/// itself. Libraries, other hands and face-down cards are absent, not flagged. Wave 2 fills
/// this in; the shape here is the seam.
/// </summary>
public sealed record GameView(
    PlayerId Viewer,
    int TurnNumber,
    Step Step,
    PlayerId ActivePlayer,
    PlayerId? PriorityHolder,
    ImmutableList<PlayerView> Players,
    ImmutableList<ObjectView> Battlefield,
    ImmutableList<ObjectView> Stack,
    ImmutableList<ObjectView> Hand,
    ImmutableList<ObjectView> Exile,
    PendingChoice? Pending,
    GameOutcome? Outcome);

/// <summary>
/// What is known about one player, CR 400.2. Every field but the last is state read straight off
/// the seat; <c>ProtectionFromEverything</c> is CR 702.16j computed through the layers, and it is
/// here because it is public and because nothing else in the view explains why damage to that
/// player never lands — a client draws it beside the life total, the way a designation is drawn.
/// The other player limits of CR 613.10 are not here: this is the one a table can see the effect
/// of on every damage event, and a field the view does not need is a field it does not carry.
/// </summary>
[RuleRef("702.16j")]
public sealed record PlayerView(PlayerId Id, string Name, int Life, int HandCount, int LibraryCount, int GraveyardCount, ImmutableDictionary<string, int> Counters, ManaPool Mana, ImmutableList<ObjectView> Graveyard, ImmutableList<ObjectView> Command, bool ProtectionFromEverything = false)
{
    /// <summary>
    /// CR 401.5: while an effect has this player playing with the top card of their library
    /// revealed, that card is revealed — to <em>every</em> player, which is the whole difference
    /// from "may look at the top card of your library", the private half of the same rule. It is
    /// null for every other seat, and a library is otherwise <see cref="LibraryCount"/> and
    /// nothing else, so no viewer learns a card this rule does not show them.
    /// <para>
    /// CR 401.6 rides on the id: the card is shown by the object it is, so a top card that stops
    /// being revealed and is revealed again arrives here under whatever id the zone system gave
    /// the new object, and a client that keyed on the old one sees it replaced rather than
    /// updated. It is a body property rather than a positional one so that no existing
    /// construction of a seat moves for a field most games never fill.
    /// </para>
    /// </summary>
    [RuleRef("401.5")]
    public ObjectView? RevealedTopCard { get; init; }
}

public sealed record ObjectView(
    ObjectId Id,
    string Name,
    string OracleId,
    PlayerId Controller,
    PlayerId Owner,
    bool Tapped,
    bool FaceDown,
    int? Power,
    int? Toughness,
    int? Loyalty,
    int Damage,
    ImmutableDictionary<string, int> Counters,
    ObjectId? AttachedTo,

    /// <summary>
    /// CR 303.4f: an Aura is attached to an object <em>or a player</em>, and the second half is
    /// this. It travels beside <see cref="AttachedTo"/> for every viewer because an attachment is
    /// public information (CR 400.2) — the table can see what the Aura is on, and a client that
    /// read only the object half would draw an "enchant player" Aura floating on nothing.
    /// </summary>
    PlayerId? AttachedToPlayer,

    ImmutableList<string> Keywords,
    ImmutableList<string> Types,
    ImmutableList<string> Subtypes,
    bool Enforced,

    /// <summary>
    /// CR 205.3d: this object is every creature type, which is what changeling grants
    /// (CR 702.72a). The names are not in <see cref="Subtypes"/> — CR 205.3m's list changes with
    /// every set — so a client that asks "is this a Goblin" reads this flag beside the list, as
    /// the engine's own readers do.
    /// </summary>
    bool EveryCreatureType = false,

    /// <summary>
    /// CR 707: this object is a copy. A copy of a spell sits on the stack under the same name and
    /// with the same characteristics as the spell it copies (CR 707.10), so nothing else in the
    /// view tells the two apart, and a client that drew only the name would show one spell twice.
    /// It is public — the stack is a public zone (CR 400.2) — and it is what makes CR 704.5e
    /// legible at the table: this is the object that ceases to exist when it leaves.
    /// </summary>
    bool IsCopy = false);

/// <summary>What a player may do now. The client enables controls from this and reasons about nothing.</summary>
public sealed record LegalActions(
    ImmutableList<ObjectId> Castable,
    ImmutableList<ActivatableAbility> Activatable,
    ImmutableList<ObjectId> PlayableLands,
    ImmutableList<ObjectId> EligibleAttackers,
    ImmutableList<ObjectId> EligibleBlockers,
    bool MayPass,
    bool MustAnswerChoice)
{
    /// <summary>
    /// CR 508.1d: a creature that can't attack unless its controller pays a cost <em>can</em>
    /// attack — the player "is not required to pay that cost" — so the taxed creature is in
    /// <see cref="EligibleAttackers"/> and what it would cost is here beside it. Without this the
    /// only way to learn the price is to make the declaration and have CR 508.1j refuse it, which
    /// is not a choice the player was offered. One entry per eligible attacker that owes a tax;
    /// an attacker that owes nothing has no entry, so an empty list is the ordinary combat.
    /// </summary>
    [RuleRef("508.1d")]
    public ImmutableList<AttackTaxOffer> AttackTaxes { get; init; } = [];
}

public sealed record ActivatableAbility(ObjectId Source, int AbilityIndex, string Text);

/// <summary>
/// What declaring one creature as an attacker would cost, CR 508.1h, and whether that cost can
/// be paid, CR 508.1j.
/// <para>
/// <paramref name="Payable"/> is read against the mana pool as it stands. CR 508.1i gives the
/// active player a window to activate mana abilities after the total is locked in and before it
/// is paid, and this engine does not take that step (recorded in <c>seam-requests/E4i.md</c>), so
/// a false here means "not out of the pool you are holding" and never "this attack is illegal".
/// A tax the engine cannot express as mana at all — CR 508.1h's refusal — is unpayable for a
/// second reason, and the cost text says so rather than the view dropping the attacker.
/// </para>
/// </summary>
[RuleRef("508.1h")]
public sealed record AttackTaxOffer(ObjectId Attacker, string Cost, bool Payable);
