namespace MtgEngine.Domain.Enums;

[Flags]
public enum CardType
{
    None = 0,
    Creature = 1 << 0,
    Instant = 1 << 1,
    Sorcery = 1 << 2,
    Enchantment = 1 << 3,
    Artifact = 1 << 4,
    Land = 1 << 5,
    Planeswalker = 1 << 6,
    Tribal = 1 << 7,
    Token = 1 << 8,
    Battle = 1 << 9,
    Other = 1 << 10,
}

[Flags]
public enum KeywordAbility : long
{
    None = 0,
    Flying = 1 << 0,
    Reach = 1 << 1,
    FirstStrike = 1 << 2,
    DoubleStrike = 1 << 3,
    Trample = 1 << 4,
    Deathtouch = 1 << 5,
    Lifelink = 1 << 6,
    Vigilance = 1 << 7,
    Haste = 1 << 8,
    Hexproof = 1 << 9,
    Indestructible = 1 << 10,
    Menace = 1 << 11,
    Flash = 1 << 12,
    Shroud = 1 << 13,
    Protection = 1 << 14,
    Ward = 1 << 15,

    // CR 702.3. Added for the rules engine, which has to ask whether a creature may attack.
    Defender = 1 << 16,

    // ---- Evasion and blocking restrictions (CR 509.1b) ----
    //
    // Each of these is a rule about who may block whom, and each is one clause in
    // CombatRules.CannotBlock. They are flags rather than parsed text because that is where the
    // combat code asks the question, and because a computed characteristic can grant them.

    /// <summary>CR 702.14: unblockable unless the defender controls a Swamp.</summary>
    Swampwalk = 1 << 17,

    /// <summary>CR 702.14: unblockable unless the defender controls a Forest.</summary>
    Forestwalk = 1 << 18,

    /// <summary>CR 702.14: unblockable unless the defender controls an Island.</summary>
    Islandwalk = 1 << 19,

    /// <summary>CR 702.14: unblockable unless the defender controls a Mountain.</summary>
    Mountainwalk = 1 << 20,

    /// <summary>CR 702.14: unblockable unless the defender controls a Plains.</summary>
    Plainswalk = 1 << 21,

    /// <summary>This creature can't block at all.</summary>
    CantBlock = 1 << 22,

    /// <summary>This creature can't be blocked at all.</summary>
    CantBeBlocked = 1 << 23,

    /// <summary>CR 702.9b applies to flying; this is its reach-less counterpart for fliers only.</summary>
    BlocksOnlyFlying = 1 << 24,

    /// <summary>CR 702.5b: blockable only by creatures with horsemanship.</summary>
    Horsemanship = 1 << 25,

    /// <summary>
    /// CR 702.90b: damage to players is poison counters, damage to creatures is -1/-1 counters.
    /// </summary>
    Infect = 1 << 26,

    /// <summary>CR 702.73a: this card is every creature type.</summary>
    Changeling = 1 << 27,

    /// <summary>CR 508.1d: this creature attacks each combat if it is able to.</summary>
    MustAttack = 1 << 28,

    /// <summary>
    /// CR 702.80a: damage to creatures is dealt as -1/-1 counters. Infect without the poison.
    /// </summary>
    Wither = 1L << 29,

    // ---- Protection, one flag per colour (CR 702.16) ----
    //
    // Protection is always "protection from" something, and the something is a parameter a
    // flags enum cannot carry. The five colours are the overwhelming majority of printed
    // protection, so they get a flag each; protection from a type or a name still does not
    // compile, and says so rather than pretending.

    /// <summary>CR 702.16: protection from white.</summary>
    ProtectionFromWhite = 1L << 30,

    /// <summary>CR 702.16: protection from blue.</summary>
    ProtectionFromBlue = 1L << 31,

    /// <summary>CR 702.16: protection from black.</summary>
    ProtectionFromBlack = 1L << 32,

    /// <summary>CR 702.16: protection from red.</summary>
    ProtectionFromRed = 1L << 33,

    /// <summary>CR 702.16: protection from green.</summary>
    ProtectionFromGreen = 1L << 34,

    /// <summary>
    /// CR 702.36b: can't be blocked except by artifact creatures and/or black creatures.
    /// </summary>
    Fear = 1L << 35,

    /// <summary>
    /// CR 702.28b: shadow blocks and is blocked only by shadow — evasion in both directions.
    /// </summary>
    /// <remarks>
    /// The only evasion keyword that restricts what its bearer may block as well as what may
    /// block it, which is why it needs a check on each side rather than one.
    /// </remarks>
    Shadow = 1L << 36,

    /// <summary>CR 702.13b: can't be blocked except by artifact creatures and/or creatures that share a colour.</summary>
    Intimidate = 1L << 37,

    /// <summary>CR 702.17b: can't be blocked by creatures with power 2 or less.</summary>
    Skulk = 1L << 38,

    /// <summary>
    /// CR 702.25a: whenever this becomes blocked by a creature without flanking, that creature
    /// gets -1/-1 until end of turn.
    /// </summary>
    /// <remarks>
    /// A flag as well as a trigger, because the trigger asks about it: a flanking creature
    /// blocking another flanking creature is spared, so "does it have flanking" has to be a
    /// question the computed characteristics can answer.
    /// </remarks>
    Flanking = 1L << 39,

    /// <summary>
    /// "This spell can't be countered" (CR 701.6a).
    /// </summary>
    /// <remarks>
    /// A static ability of the spell while it is on the stack, not of the permanent it becomes —
    /// which is why it is asked of the card rather than of computed characteristics: the layers
    /// are about permanents, and this matters at a moment when there is no permanent yet.
    /// </remarks>
    CantBeCountered = 1L << 40,

    /// <summary>
    /// "Protection from artifacts" (CR 702.16b).
    /// </summary>
    /// <remarks>
    /// Protection names a quality, and a card type is one as readily as a colour is. It is a flag
    /// of its own rather than a colour because the question it asks is about types, and the two
    /// are answered from different parts of the source's characteristics.
    /// </remarks>
    ProtectionFromArtifacts = 1L << 41,

    /// <summary>
    /// Banding (CR 702.22). Only the blocking half is enforced - see
    /// <c>GAME_ENGINE_FEATURE.md</c>.
    /// </summary>
    Banding = 1L << 42,

    /// <summary>
    /// CR 702.145b: on the front face of a werewolf. Three static abilities in one word - it
    /// enters transformed if it is night, it turns over as it becomes night, and nothing else may
    /// turn it over at all.
    /// </summary>
    Daybound = 1L << 43,

    /// <summary>
    /// CR 702.145e: the back face's half of the same pair. It turns over as it becomes day, and
    /// nothing else may turn it over.
    /// </summary>
    Nightbound = 1L << 44,

    /// <summary>
    /// CR 702.154a: as this attacks, you may tap another creature you control that could have
    /// attacked, and this gets +X/+0 where X is that creature's power.
    /// </summary>
    Enlist = 1L << 45,

    /// <summary>
    /// "This creature can't attack alone." A restriction on the declaration rather than on the
    /// creature: it is legal whenever at least one other creature is attacking (CR 506.3c).
    /// </summary>
    CantAttackAlone = 1L << 46,

    /// <summary>
    /// "This creature can't block alone." Satisfied by any other creature blocking, whatever it
    /// is blocking - the count is of blockers, not of blocks on one attacker (CR 509.1b).
    /// </summary>
    CantBlockAlone = 1L << 47,

    /// <summary>
    /// "Phasing" (CR 702.26a): this permanent phases out and back in during untap steps.
    /// </summary>
    /// <remarks>
    /// A keyword rather than an ability because the rule it modifies is the untap step's, and
    /// because effects grant it - "enchanted permanent has phasing" - which a per-card flag could
    /// not express.
    /// </remarks>
    Phasing = 1L << 48,

    /// <summary>
    /// "Soulbond" (CR 702.95a): may pair with another creature as either enters.
    /// </summary>
    /// <remarks>
    /// The keyword's behaviour is two triggered abilities, compiled from the line rather than
    /// from this bit. The flag exists because a card can ask whether a creature <em>has</em>
    /// soulbond — "unless it's paired with a creature with soulbond" — and that question is
    /// answered off computed characteristics like any other keyword.
    /// </remarks>
    Soulbond = 1L << 49,
}

public enum ManaColor
{
    Colorless = 0,
    White = 1,
    Blue = 2,
    Black = 3,
    Red = 4,
    Green = 5,
}
