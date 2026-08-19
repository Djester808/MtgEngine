using System.ComponentModel.DataAnnotations;

namespace MtgEngine.Api.Dtos;

// ---- Commander suggestion -------------------------------------------------

/// <summary>What the player wants out of a deck, before they have picked a commander.</summary>
public sealed record CommanderSuggestionRequest
{
    /// <summary>
    /// Free text: the deck they want, in their own words.
    /// </summary>
    /// <remarks>
    /// Attacker-controlled and injected into a prompt, so it is length-capped here and
    /// fenced at the call site rather than concatenated into the instructions. It is
    /// never used as a cache key — see the user-input safety rules in CLAUDE.md.
    /// </remarks>
    [StringLength(600)]
    public string? Brief { get; init; }

    /// <summary>WUBRG letters the deck should stay inside, or empty for no constraint.</summary>
    public string[] Colors { get; init; } = [];

    /// <summary>1–5, matching the build's bracket. Shapes how strong a commander to propose.</summary>
    [Range(1, 5)]
    public int Bracket { get; init; } = 3;

    /// <summary>Only suggest commanders the player already owns a copy of.</summary>
    public bool OwnedOnly { get; init; }

    /// <summary>How many to return. Capped server-side.</summary>
    [Range(1, 12)]
    public int Count { get; init; } = 10;
}

/// <summary>One proposed commander, resolved against the card data and verified eligible.</summary>
public sealed record CommanderSuggestionDto
{
    public string OracleId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? ManaCost { get; init; }
    public string? TypeLine { get; init; }
    public string? OracleText { get; init; }
    public string? ImageUriArtCrop { get; init; }
    public string? ImageUriNormal { get; init; }
    public string[] ColorIdentity { get; init; } = [];

    /// <summary>
    /// Why this commander fits the brief — the concrete mechanism, not an association.
    /// </summary>
    /// <remarks>
    /// The doctrine (§9.11) treats "synergizes with" filler as no reason at all, so the
    /// prompt asks for the rules step that connects the commander to the plan.
    /// </remarks>
    public string Reason { get; init; } = string.Empty;

    /// <summary>The archetype it wants to be built as, e.g. "tokens", "spellslinger".</summary>
    public string Archetype { get; init; } = string.Empty;

    /// <summary>One line on how the 99 should be shaped around it.</summary>
    public string Plan { get; init; } = string.Empty;

    /// <summary>True when the player already owns a copy. Always false when not checked.</summary>
    public bool Owned { get; init; }
}

/// <summary>The suggestions, plus what had to be discarded to produce them.</summary>
public sealed record CommanderSuggestionsDto
{
    public CommanderSuggestionDto[] Commanders { get; init; } = [];

    /// <summary>
    /// Proposals dropped because the name did not resolve to a real card, or the card was
    /// not a legal commander, or it broke the requested colours.
    /// </summary>
    /// <remarks>
    /// Surfaced rather than hidden: a suggestion pass that silently returns two commanders
    /// instead of four looks like a weak model, when the cause is usually grounding doing
    /// its job. See <c>SkippedByReason</c> for the breakdown.
    /// </remarks>
    public int Discarded { get; init; }

    public Dictionary<string, int> SkippedByReason { get; init; } = [];
}

// ---- Build plan (propose, then apply) -------------------------------------

/// <summary>One card the build proposes, already validated against the deck's constraints.</summary>
public sealed record PlannedCardDto
{
    public string OracleId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? ScryfallId { get; init; }
    public string? ManaCost { get; init; }
    public string? TypeLine { get; init; }
    public string? ImageUriArtCrop { get; init; }

    /// <summary>"main" | "side" | "maybe".</summary>
    public string Board { get; init; } = "main";

    public int Quantity { get; init; } = 1;
}

/// <summary>
/// A proposed deck, computed and validated but <b>not written</b>.
/// </summary>
/// <remarks>
/// The build used to insert all 99 cards the moment it returned, which made a bad build
/// something you had to undo by hand, card by card. The plan is the same pipeline stopped
/// one step earlier: every card here has already passed colour identity, legality, bracket
/// and duplicate checks, so applying it is an insert rather than another judgement call.
/// </remarks>
public sealed record AiBuildPlanDto
{
    public string CommanderOracleId { get; init; } = string.Empty;
    public string CommanderName { get; init; } = string.Empty;

    public PlannedCardDto[] Cards { get; init; } = [];

    /// <summary>Main-board slots this build was asked to fill.</summary>
    public int MainTarget { get; init; }

    /// <summary>Main-board slots the plan could not fill. Non-zero means an incomplete deck.</summary>
    public int MainShortfall { get; init; }

    public int CardsSkipped { get; init; }
    public Dictionary<string, int> SkippedByReason { get; init; } = [];

    /// <summary>Whether this deck makes this commander work, judged against the doctrine.</summary>
    public DeckAssessmentDto Assessment { get; init; } = new();
}

/// <summary>Coloured sources among the deck's lands, per doctrine §3.2.</summary>
public sealed record ColorSourceDto(string Color, int Count);

/// <summary>
/// What the built deck measurably contains. Facts, never a verdict.
/// </summary>
/// <remarks>
/// There are deliberately no target numbers on this record. The doctrine's quotas are
/// baselines that move with the deck — §2.2 lowers the land count for a cheap curve and
/// raises it for an expensive one, §6.4 inverts the value of mass removal with creature
/// density — so a fixed table in code would contradict the standard it claims to enforce
/// and would call a correctly-built deck broken. Judgement belongs to the assessment below,
/// which reads these facts against the doctrine for this specific commander.
/// </remarks>
public sealed record DeckFactsDto
{
    public int Cards { get; init; }
    public int Lands { get; init; }
    public int Ramp { get; init; }
    public int Draw { get; init; }
    public int Interaction { get; init; }

    /// <summary>
    /// How many of <see cref="Interaction"/> are creatures rather than spells.
    /// </summary>
    /// <remarks>
    /// Each card is counted in exactly one role, and the classifier picks it from the rules
    /// text. That reads a creature whose arrival makes every player sacrifice as removal —
    /// which it is, and which in a sacrifice deck is also the plan. Doctrine §2 says the
    /// roles overlap and to "count a card in the role it is actually being played for", and
    /// a single bucket cannot.
    /// <para>
    /// So the overlap is reported instead of hidden. A measured build showed 17 interaction
    /// against a band of 8-12, which read as badly over quota until you saw that most of
    /// them were the sacrifice payoffs the deck is built on. "17, of which 11 are creatures"
    /// says that; "17" does not.
    /// </para>
    /// </remarks>
    public int InteractionOnCreatures { get; init; }
    public int Other { get; init; }
    public int Creatures { get; init; }

    /// <summary>Creature density, the §7 archetype signal that decides §6.4.</summary>
    public int CreaturePercentOfNonland { get; init; }

    /// <summary>Lands + ramp. Doctrine §2.1 treats this as mattering more than land count alone.</summary>
    public int ManaSources { get; init; }

    public double AverageManaValue { get; init; }

    public ColorSourceDto[] ColorSources { get; init; } = [];
}

/// <summary>One thing worth knowing about the built deck.</summary>
public sealed record DeckFindingDto
{
    /// <summary>Plan | Mana | Interaction | Resilience.</summary>
    public string Area { get; init; } = string.Empty;

    /// <summary>critical | improve | note. Critical means the deck cannot execute its plan.</summary>
    public string Severity { get; init; } = "note";

    public string Finding { get; init; } = string.Empty;

    /// <summary>What to change, or empty when nothing needs to.</summary>
    public string Fix { get; init; } = string.Empty;
}

/// <summary>
/// The model's judgement of the built deck, against the doctrine and this commander.
/// </summary>
/// <remarks>
/// Empty when the assessment pass failed. The deck is the expensive half and is perfectly
/// usable without a verdict, so a failure here degrades rather than losing the build.
/// </remarks>
public sealed record DeckAssessmentDto
{
    public string Verdict { get; init; } = string.Empty;
    public DeckFindingDto[] Findings { get; init; } = [];
    public DeckFactsDto Facts { get; init; } = new();
}

/// <summary>Applies a previously returned plan.</summary>
/// <remarks>
/// Carries the card list rather than a server-side plan id, so no build state has to be
/// stored between the two calls. Everything in it is re-validated on the way in — the
/// payload is a request, not a trusted continuation.
/// </remarks>
public sealed record AiApplyPlanRequest
{
    [Required]
    [StringLength(64)]
    public string CommanderOracleId { get; init; } = string.Empty;

    [Range(1, 5)]
    public int Bracket { get; init; } = 3;

    /// <summary>The cards to insert. Capped so one call cannot be used as a bulk-insert hole.</summary>
    [MaxLength(200)]
    public PlannedCardDto[] Cards { get; init; } = [];
}
