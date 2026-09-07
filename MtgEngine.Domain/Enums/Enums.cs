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

public enum ManaColor
{
    Colorless = 0,
    White = 1,
    Blue = 2,
    Black = 3,
    Red = 4,
    Green = 5,
}
