using System.Collections.Immutable;

namespace MtgEngine.Rules.Views;

/// <summary>
/// What one player is allowed to know. The only shape that ever leaves the server.
/// </summary>
/// <remarks>
/// Hidden information is <b>absent</b> here, not flagged. There is no library list with a
/// "visible: false" beside it and no opponent hand with the names blanked, because both of those
/// designs put the secret in the payload and trust every future serializer, log line and debug
/// dump not to spill it. If a field is not on this record, no amount of client tampering
/// reveals it.
/// <para>
/// The engine this replaces sent one <c>GameState</c> to a SignalR group containing both
/// players, which handed each of them the other's hand and both libraries.
/// </para>
/// </remarks>
public sealed record GameView
{
    public required Guid GameId { get; init; }

    /// <summary>The player this view was built for. Every visibility decision was made for them.</summary>
    public required Guid Viewer { get; init; }

    public required int TurnNumber { get; init; }

    public required Guid ActivePlayerId { get; init; }

    /// <summary>
    /// Where in the turn the game is (CR 500.1), as the step's name.
    /// </summary>
    /// <remarks>
    /// A client needs this to know when to offer an attack: declaring attackers is a turn-based
    /// action that happens before anyone has priority (CR 508.1), so "is it my turn and do I
    /// have priority" does not identify the moment.
    /// </remarks>
    public required string CurrentStep { get; init; }

    /// <summary>
    /// The player who may act right now, or null when nobody has priority (CR 117.1).
    /// </summary>
    /// <remarks>
    /// Public, and it has to be: "may I do something, or am I waiting on them" is the question
    /// a player asks between every pair of actions. Absent from the view until now, so the
    /// board offered Pass Priority to whoever was looking at it and the server refused it with
    /// CR 117.1 — a refusal that reads as a broken button rather than as "not your turn to act".
    /// Null during untap (CR 502.4), during cleanup (CR 514.3), and while something resolves
    /// (CR 117.2e).
    /// </remarks>
    public Guid? PriorityPlayerId { get; init; }

    /// <summary>
    /// Attacking creature to what it is attacking — a player, or a planeswalker (CR 508.1b).
    /// </summary>
    public ImmutableDictionary<Guid, AttackTargetView> Attackers { get; init; } =
        ImmutableDictionary<Guid, AttackTargetView>.Empty;

    /// <summary>
    /// Whether attackers have been declared for this combat (CR 508.1).
    /// </summary>
    /// <remarks>
    /// Not derivable from <see cref="Attackers"/>: declaring no attackers is a declaration, and
    /// leaves that dictionary as empty as never having declared at all. A client without this
    /// cannot tell the two apart, and the board that guessed showed its attack button for the
    /// whole step — with no way to pass, because it believed the declaration was still to come.
    /// </remarks>
    public bool AttackersDeclared { get; init; }

    /// <summary>Whether blockers have been declared for this combat (CR 509.1).</summary>
    public bool BlockersDeclared { get; init; }

    /// <summary>Each attacker and the creatures blocking it (CR 509.1g).</summary>
    public ImmutableDictionary<Guid, ImmutableList<Guid>> Blockers { get; init; } =
        ImmutableDictionary<Guid, ImmutableList<Guid>>.Empty;

    /// <summary>In seating order, so the client can lay the table out consistently (CR 103.5).</summary>
    public ImmutableList<PlayerView> Players { get; init; } = [];

    /// <summary>Public zone (CR 400.2).</summary>
    public ImmutableList<ObjectView> Battlefield { get; init; } = [];

    /// <summary>Public zone; index 0 is the top (CR 405.2).</summary>
    public ImmutableList<ObjectView> Stack { get; init; } = [];

    /// <summary>Public zone (CR 400.2).</summary>
    public ImmutableList<ObjectView> Exile { get; init; } = [];

    /// <summary>Public zone (CR 400.2).</summary>
    public ImmutableList<ObjectView> Command { get; init; } = [];

    /// <summary>
    /// The decision the game is waiting on, or null.
    /// </summary>
    /// <remarks>
    /// Every player is told the game is waiting and on whom — a board that simply stops with no
    /// explanation is the worst thing a client can show. Only the player being asked is sent the
    /// options, because those can be hidden information: the list of cards to put on the bottom
    /// after a mulligan is that player's hand.
    /// </remarks>
    public ChoiceView? Choice { get; init; }
}

/// <summary>A decision the game is waiting on, as one player may see it.</summary>
public sealed record ChoiceView
{
    public required string Id { get; init; }

    /// <summary>Who has to answer. Everyone is told this much.</summary>
    public required Guid PlayerId { get; init; }

    public required string Kind { get; init; }

    public required string Prompt { get; init; }

    public int MinPicks { get; init; }

    public int MaxPicks { get; init; }

    /// <summary>True when the order of the picks is the answer (CR 603.3b, 616.1).</summary>
    public bool IsOrdering { get; init; }

    /// <summary>
    /// True when the answer is an amount per option rather than a selection (CR 510.1c).
    /// </summary>
    /// <remarks>
    /// A division is answered by picking an option once per point, so the same option repeats —
    /// which is a mistake everywhere else and the whole mechanism here. A client that did not
    /// know the difference would stop the player after one pick.
    /// </remarks>
    public bool IsDivision { get; init; }

    /// <summary>How much is being divided, for a division.</summary>
    public int TotalToDivide { get; init; }

    /// <summary>Populated only for the player being asked; null for everyone else.</summary>
    public ImmutableList<ChoiceOptionView>? Options { get; init; }
}

public sealed record ChoiceOptionView(string Id, string Label);

/// <summary>One player as the viewer sees them.</summary>
public sealed record PlayerView
{
    public required Guid PlayerId { get; init; }

    public required string Name { get; init; }

    public required int Life { get; init; }

    public int PoisonCounters { get; init; }

    /// <summary>
    /// A count and never a list. Any player may count any library at any time (CR 401.3) and no
    /// player may look at one (CR 401.2) — including their own.
    /// </summary>
    public required int LibraryCount { get; init; }

    /// <summary>
    /// The top card of this player's library, when something lets them see it (CR 401.2).
    /// </summary>
    /// <remarks>
    /// Null for everyone else, always. "You may look at the top card of your library any time" is
    /// a permission given to one player, so it is answered here rather than by sending the card
    /// and trusting the client to hide it - which is the cheating vector this projection exists
    /// to close.
    /// </remarks>
    public ObjectView? TopOfLibrary { get; init; }

    /// <summary>Any player may count any hand (CR 402.3).</summary>
    public required int HandCount { get; init; }

    /// <summary>
    /// Populated only when this player is the viewer; null for everyone else (CR 402.3).
    /// </summary>
    public ImmutableList<ObjectView>? Hand { get; init; }

    /// <summary>
    /// The cards in this hand that everybody has been shown (CR 701.16a).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Hand"/>, which is null for anyone but its owner. A reveal makes
    /// particular cards public without making the hand public, and the two are different facts.
    /// </remarks>
    public ImmutableList<ObjectView> RevealedHand { get; init; } = [];

    /// <summary>Public zone, examinable by anyone at any time (CR 404.2).</summary>
    public ImmutableList<ObjectView> Graveyard { get; init; } = [];

    public bool HasLost { get; init; }

    /// <summary>
    /// Combat damage this player has taken from each commander, by the commander's name
    /// (CR 903.10a).
    /// </summary>
    /// <remarks>
    /// Public: everyone at the table needs to know how close a commander is to twenty-one,
    /// because it changes every block. Keyed by name rather than oracle id so a client can show
    /// it without another lookup.
    /// </remarks>
    public ImmutableDictionary<string, int> CommanderDamage { get; init; } =
        ImmutableDictionary<string, int>.Empty;

    /// <summary>The name of this player's commander, if they have one (CR 903.3).</summary>
    public string? CommanderName { get; init; }

    /// <summary>Against the one-per-turn allowance (CR 305.2), so the client can grey the drop.</summary>
    public int LandsPlayedThisTurn { get; init; }

    /// <summary>
    /// The mana this player has available, by symbol — "W", "U", "B", "R", "G", "C" (CR 106.4).
    /// </summary>
    /// <remarks>
    /// Public information: a mana pool is not hidden from anyone, and it has to be, because
    /// whether an opponent can pay for a response is a decision every player at the table makes
    /// out loud. Absent from the view until now, which left a player tapping lands with no way
    /// to see what they had floating — and it empties as each step ends (CR 500.5), so guessing
    /// from what you tapped is not reliable either.
    /// </remarks>
    public ImmutableDictionary<string, int> ManaPool { get; init; } =
        ImmutableDictionary<string, int>.Empty;

    /// <summary>
    /// Mana that may only be spent on some things, one entry per mana (CR 106.6).
    /// </summary>
    /// <remarks>
    /// A field of its own rather than more entries in <see cref="ManaPool"/>, because the two
    /// answer different questions and a client that added them together would tell a player they
    /// could pay for something they cannot. Public for the same reason the rest of the pool is.
    /// <para>
    /// Each entry is the symbol and what the mana may be spent on, in the card's own words, so a
    /// board can show the restriction without having to know the vocabulary behind it.
    /// </para>
    /// </remarks>
    public ImmutableList<RestrictedManaView> RestrictedMana { get; init; } =
        ImmutableList<RestrictedManaView>.Empty;
}

/// <summary>One mana that may only be spent on some things (CR 106.6).</summary>
public sealed record RestrictedManaView
{
    /// <summary>The mana symbol: "W", "U", "B", "R", "G" or "C".</summary>
    public required string Symbol { get; init; }

    /// <summary>What it may be spent on, e.g. "creature spells".</summary>
    public required string SpendableOn { get; init; }
}

/// <summary>
/// One object, as much of it as the viewer may see.
/// </summary>
/// <remarks>
/// The characteristics here are the <em>printed</em> ones, and they are named that way on
/// purpose. Current power, toughness, types and abilities are the printed values with every
/// applicable continuous effect layered over them (CR 613), which is slice 4; until that exists
/// there is nothing to report and a field called <c>Power</c> would be a lie the moment the
/// first lord is implemented.
/// </remarks>
/// <summary>
/// One activated ability a client can offer on a permanent (CR 602.1).
/// </summary>
/// <remarks>
/// The view has to carry these because the client cannot work them out. Everything a board knew
/// about activating was the word "mana" hardcoded against lands, which made a Sol Ring
/// untappable and a Prodigal Pyromancer unplayable — the abilities were implemented and simply
/// could not be reached.
/// </remarks>
public sealed record AbilityView
{
    /// <summary>The id to send back when activating it.</summary>
    public required string Id { get; init; }

    /// <summary>What it says, for the button.</summary>
    public required string Text { get; init; }

    /// <summary>Whether {T} is part of the cost, so a tapped permanent cannot use it.</summary>
    public bool RequiresTap { get; init; }

    /// <summary>How many targets it needs, so the client knows to ask before sending.</summary>
    public int TargetCount { get; init; }

    /// <summary>Whether it produces mana, which a client may want to present differently.</summary>
    public bool IsManaAbility { get; init; }

    /// <summary>
    /// A printed timing restriction, as the enum name (CR 602.5d). Absent means any time.
    /// </summary>
    /// <remarks>
    /// The board needs it for the same reason it needs <see cref="TargetCount"/>: an action
    /// offered and then refused is worse than one never offered. The engine still enforces it —
    /// this is the courtesy copy, and where the two disagree the board is wrong.
    /// </remarks>
    public string? Timing { get; init; }

    /// <summary>
    /// Costs the player has to pick cards for before activating (CR 601.2f).
    /// </summary>
    /// <remarks>
    /// The board has to know, because the payment travels <em>with</em> the activation: an
    /// ability whose cost is "Sacrifice a creature" cannot be activated by clicking it, and a
    /// board that offered it as a plain button would send an activation the engine refuses. The
    /// description reads naturally in a prompt — "target creature you control" — because that is
    /// what it is going to be shown as.
    /// </remarks>
    public ImmutableList<CostChoiceView> CostChoices { get; init; } = [];
}

/// <summary>One cost the player picks cards for, as the board needs to ask it.</summary>
/// <param name="Kind">Sacrifice, discard, or discard at random.</param>
/// <param name="Count">How many cards to pick.</param>
/// <param name="Description">What qualifies, for the prompt. Null means any card in hand.</param>
public sealed record CostChoiceView(string Kind, int Count, string? Description);

public sealed record ObjectView
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Lets the client fetch art and printings from the card database it already has.</summary>
    public required string OracleId { get; init; }

    public required Guid ControllerId { get; init; }

    public string? ManaCost { get; init; }

    public string? TypeLine { get; init; }

    /// <summary>The card's oracle text, so a player can read what it does.</summary>
    /// <remarks>
    /// A board that names cards and never says what they do is a board you cannot play from
    /// unless you already know every card on it. The engine never reads this — it is here for
    /// the person holding the phone.
    /// </remarks>
    public string? OracleText { get; init; }

    /// <summary>Cropped art, for the card as it sits on the battlefield.</summary>
    /// <remarks>
    /// Presentation belongs in the view and nowhere else. It is deliberately absent from the
    /// event log, which records a game and not what a printing looked like — see
    /// <c>EventLogSerializer</c> — but a client rendering a board needs it, and making it look
    /// the card up by oracle id would be a request per permanent for something the server has
    /// in hand.
    /// </remarks>
    public string? ArtUri { get; init; }

    /// <summary>The whole card, for reading it.</summary>
    public string? ImageUri { get; init; }

    /// <summary>
    /// Whether this is a planeswalker, so a client can offer it as something to attack
    /// (CR 508.1b).
    /// </summary>
    /// <remarks>
    /// Sent as its own flag rather than left for the client to find in
    /// <see cref="TypeLine"/>. That string is assembled for a person to read, and a client
    /// searching it for the word would be making a rules judgement out of display text — which
    /// breaks the first time the line is localised or reworded.
    /// </remarks>
    public bool IsPlaneswalker { get; init; }

    /// <summary>
    /// Whether this is a creature, and so something that could be declared as an attacker or
    /// blocker (CR 508.1a).
    /// </summary>
    /// <remarks>
    /// Not a claim that it <em>can</em> attack — that is a question about tapped status,
    /// summoning sickness and any restriction on the card (CR 508.1a), which the engine answers
    /// when the declaration arrives. This is only enough for a client to stop offering a land.
    /// </remarks>
    public bool IsCreature { get; init; }

    /// <summary>Whether it is a land, which is what a card face is coloured by first.</summary>
    public bool IsLand { get; init; }

    /// <summary>What this permanent can be asked to do (CR 602.1).</summary>
    public System.Collections.Immutable.ImmutableList<AbilityView> Abilities { get; init; } = [];

    /// <summary>The card's colours, for the frame it is drawn in.</summary>
    public IReadOnlyList<string> Colors { get; init; } = [];

    public int? PrintedPower { get; init; }

    public int? PrintedToughness { get; init; }

    // ---- Battlefield status; null off the battlefield (CR 403.3) ------------------------

    public bool? IsTapped { get; init; }

    public bool? HasSummoningSickness { get; init; }

    public int? DamageMarked { get; init; }

    public IReadOnlyDictionary<string, int>? Counters { get; init; }
}

/// <summary>What a creature is attacking, as a client sees it (CR 508.1b).</summary>
public sealed record AttackTargetView(Guid DefendingPlayer, Guid? Planeswalker);
