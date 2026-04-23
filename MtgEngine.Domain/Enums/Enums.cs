namespace MtgEngine.Domain.Enums;

[Flags]
public enum CardType
{
    None        = 0,
    Creature    = 1 << 0,
    Instant     = 1 << 1,
    Sorcery     = 1 << 2,
    Enchantment = 1 << 3,
    Artifact    = 1 << 4,
    Land        = 1 << 5,
    Planeswalker= 1 << 6,
    Tribal      = 1 << 7,
}

[Flags]
public enum KeywordAbility
{
    None          = 0,
    Flying        = 1 << 0,
    Reach         = 1 << 1,
    FirstStrike   = 1 << 2,
    DoubleStrike  = 1 << 3,
    Trample       = 1 << 4,
    Deathtouch    = 1 << 5,
    Lifelink      = 1 << 6,
    Vigilance     = 1 << 7,
    Haste         = 1 << 8,
    Hexproof      = 1 << 9,
    Indestructible= 1 << 10,
    Menace        = 1 << 11,
    Flash         = 1 << 12,
    Shroud        = 1 << 13,
    Protection    = 1 << 14,
    Ward          = 1 << 15,
}

public enum ManaColor
{
    Colorless = 0,
    White     = 1,
    Blue      = 2,
    Black     = 3,
    Red       = 4,
    Green     = 5,
}

public enum Phase
{
    Beginning,
    PreCombatMain,
    Combat,
    PostCombatMain,
    Ending,
}

public enum Step
{
    // Beginning
    Untap,
    Upkeep,
    Draw,
    // Main (no steps)
    Main,
    // Combat
    BeginningOfCombat,
    DeclareAttackers,
    DeclareBlockers,
    FirstStrikeDamage,
    CombatDamage,
    EndOfCombat,
    // Ending
    End,
    Cleanup,
}

public enum Zone
{
    Library,
    Hand,
    Battlefield,
    Graveyard,
    Exile,
    Stack,
    Command,
}

public enum CounterType
{
    PlusOnePlusOne,
    MinusOneMinusOne,
    Loyalty,
    Poison,
    Charge,
    Fade,
    Time,
    Age,
    Feather,
    Lore,
    Verse,
}

public enum GameResult
{
    InProgress,
    Player1Wins,
    Player2Wins,
    Draw,
}

public enum SpeedRestriction
{
    Sorcery,   // Main phase, empty stack, active player only
    Instant,   // Any time player has priority
}
