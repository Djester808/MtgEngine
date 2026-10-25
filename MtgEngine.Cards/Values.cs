namespace MtgEngine.Cards;

/// <summary>Marks a record, enum member or property with the Comprehensive Rules entry that defines it.</summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class RuleRefAttribute(string rule) : Attribute
{
    public string Rule { get; } = rule;
}

/// <summary>The zones of CR 400.1, plus "outside the game" for wishes and companions.</summary>
[RuleRef("400.1")]
public enum Zone
{
    Library,
    Hand,
    Battlefield,
    Graveyard,
    Stack,
    Exile,
    Command,
    OutsideTheGame,
}

/// <summary>Card types, CR 205.2a. Flags, because an object can have several.</summary>
[Flags]
[RuleRef("205.2a")]
public enum CardType
{
    None = 0,
    Artifact = 1 << 0,
    Battle = 1 << 1,
    Conspiracy = 1 << 2,
    Creature = 1 << 3,
    Dungeon = 1 << 4,
    Enchantment = 1 << 5,
    Instant = 1 << 6,
    Kindred = 1 << 7,
    Land = 1 << 8,
    Phenomenon = 1 << 9,
    Plane = 1 << 10,
    Planeswalker = 1 << 11,
    Scheme = 1 << 12,
    Sorcery = 1 << 13,
    Vanguard = 1 << 14,
}

/// <summary>Supertypes, CR 205.4a.</summary>
[Flags]
[RuleRef("205.4a")]
public enum Supertype
{
    None = 0,
    Basic = 1 << 0,
    Legendary = 1 << 1,
    Ongoing = 1 << 2,
    Snow = 1 << 3,
    World = 1 << 4,
}

/// <summary>The five colors, CR 105.1. Colorless is the empty set.</summary>
[Flags]
[RuleRef("105.1")]
public enum Color
{
    Colorless = 0,
    White = 1 << 0,
    Blue = 1 << 1,
    Black = 1 << 2,
    Red = 1 << 3,
    Green = 1 << 4,
}

/// <summary>Steps of a turn, CR 500.1 and 505–514. Main phases have no steps, so they appear here as their own entries.</summary>
[RuleRef("500.1")]
public enum Step
{
    Untap,
    Upkeep,
    Draw,
    PrecombatMain,
    BeginningOfCombat,
    DeclareAttackers,
    DeclareBlockers,
    FirstStrikeCombatDamage,
    CombatDamage,
    EndOfCombat,
    PostcombatMain,
    End,
    Cleanup,

    /// <summary>
    /// "At the beginning of your next main phase", CR 505.1: either main phase, whichever begins
    /// next — the printed line names neither the precombat nor the postcombat one, and a reading
    /// that picks one of them fires at the wrong time half of the games (a delayed trigger made
    /// during combat would never see a PrecombatMain again this turn). Appended last so no stored
    /// file's step shifts. Use PrecombatMain or PostcombatMain when the card names one.
    /// </summary>
    Main,
}

/// <summary>Phases, for effects that refer to a whole phase ("additional combat phase", "your main phase").</summary>
[RuleRef("500.1")]
public enum Phase
{
    Beginning,
    PrecombatMain,
    Combat,
    PostcombatMain,
    Ending,
}

/// <summary>Scryfall layouts represented by the printed-card model; Other preserves unknown layouts.</summary>
public enum Layout
{
    Normal,
    Split,
    Flip,
    Transform,
    ModalDfc,
    Meld,
    Leveler,
    Class,
    Saga,
    Adventure,
    Mutate,
    Prototype,
    Battle,
    Case,
    Omen,
    Other,

    /// <summary>
    /// A preparation card, CR 722.1: a two-part frame with a smaller frame inset within the text
    /// box. The inset frame is a "prepare spell" (CR 722.2a) — alternative characteristics the card
    /// may have while it is a spell — and it is one card, not two (CR 722.2c). It is not
    /// <see cref="Adventure"/> or <see cref="Omen"/>: those halves are cast from the hand, and a
    /// preparation card's inset frame can never be cast (CR 722.3), only copied into exile when the
    /// permanent becomes prepared and cast from there. Appended last, so no stored file's layout
    /// shifts.
    /// </summary>
    Prepare,
}

/// <summary>What a face is to its card.</summary>
public enum FaceRole
{
    Main,
    Back,
    Left,
    Right,
    Adventure,
    Omen,
    Flipped,
    MeldResult,

    /// <summary>
    /// The inset frame of a preparation card, CR 722.2a — the "prepare spell" half of a
    /// <see cref="Layout.Prepare"/> card. It holds alternative characteristics the object may have
    /// while it is a spell, and their existence is part of the card's copiable values (CR 722.2b),
    /// so the half is a face of this card and not a card of its own. It is never cast from the card
    /// itself (CR 722.3): the copy the controller may cast is made in exile as the permanent gains
    /// the prepared designation (CR 722.3c). Appended last, so no stored file's role shifts.
    /// </summary>
    PrepareSpell,
}

/// <summary>Printed power or toughness: a number, or a characteristic-defining marker such as "*".</summary>
public sealed record PrintedStat(string Text)
{
    public int? Number => int.TryParse(Text, out var n) ? n : null;
}
