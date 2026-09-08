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

/// <summary>Scryfall's layouts that reach the engine. Anything else is not a playable card.</summary>
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
}

/// <summary>Where a card goes inside a zone that is ordered.</summary>
public enum Placement
{
    Default,
    Top,
    Bottom,
    AnyOrder,
    RandomOrder,
}

/// <summary>Comparison operators for amounts.</summary>
public enum ComparisonOp
{
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
}

/// <summary>A comparison of some quantity against an amount: "power 4 or greater" is Ge 4.</summary>
public sealed record Comparison(ComparisonOp Op, Amount Value);

/// <summary>When an activated ability may be activated, CR 602.5 and the common printed restrictions.</summary>
public enum Timing
{
    Any,
    SorcerySpeed,
    DuringYourTurn,
    DuringCombat,
    DuringOpponentsTurn,
    OnlyDuringDeclareBlockers,
}

/// <summary>Which object a trigger-relative selector means when a trigger involves two objects.</summary>
public enum TriggerRole
{
    /// <summary>The object the trigger is about: the creature that died, the spell that was cast.</summary>
    Subject,
    /// <summary>The other object, when there is one: the creature it blocked, the source of the damage.</summary>
    Other,
}

/// <summary>What a "double" or "triple" instruction doubles, CR 701.10–11.</summary>
public enum DoubleWhat
{
    Power,
    Toughness,
    PowerAndToughness,
    Counters,
    Life,
    Mana,
    Damage,
}

/// <summary>What an "exchange" instruction exchanges, CR 701.12.</summary>
public enum ExchangeWhat
{
    Control,
    LifeTotals,
    PowerAndToughness,
    Zones,
}

/// <summary>Things a player can be asked to choose that are not objects.</summary>
public enum ChooseWhat
{
    Color,
    CreatureType,
    CardType,
    BasicLandType,
    CardName,
    Number,
    Player,
    OddOrEven,
    Keyword,
    Word,
}

/// <summary>Who picks the card when a player discards.</summary>
public enum DiscardChooser
{
    Owner,
    Random,
    Controller,
}

/// <summary>Which kind of ability an activation trigger watches for.</summary>
public enum AbilityKind
{
    Any,
    Mana,
    Loyalty,
    NonMana,
}

/// <summary>Counter kinds the linter knows. Extend here, never with an ad-hoc string.</summary>
[RuleRef("122")]
public static class Counters
{
    public const string PlusOnePlusOne = "+1/+1";
    public const string MinusOneMinusOne = "-1/-1";
    public const string Loyalty = "loyalty";
    public const string Lore = "lore";
    public const string Charge = "charge";
    public const string Poison = "poison";
    public const string Energy = "energy";
    public const string Experience = "experience";
    public const string Rad = "rad";
    public const string Ticket = "ticket";
    public const string Time = "time";
    public const string Age = "age";
    public const string Fade = "fade";
    public const string Level = "level";
    public const string Shield = "shield";
    public const string Stun = "stun";
    public const string Oil = "oil";
    public const string Verse = "verse";
    public const string Quest = "quest";
    public const string Ki = "ki";
    public const string Spore = "spore";
    public const string Storage = "storage";
    public const string Finality = "finality";
    public const string Defense = "defense";
    public const string Incubator = "incubator";
    public const string Page = "page";
    public const string Study = "study";
    public const string Doom = "doom";
    public const string Ice = "ice";
    public const string Gold = "gold";
    public const string Coin = "coin";
    public const string Foreshadow = "foreshadow";

    /// <summary>Keyword counters, CR 122.1a: a permanent with one has the keyword.</summary>
    public static readonly IReadOnlySet<string> KeywordCounters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "flying", "first strike", "double strike", "deathtouch", "decayed", "exalted", "flash", "haste",
        "hexproof", "indestructible", "lifelink", "menace", "reach", "shadow", "trample", "vigilance", "ward",
    };

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        PlusOnePlusOne, MinusOneMinusOne, Loyalty, Lore, Charge, Poison, Energy, Experience, Rad, Ticket, Time,
        Age, Fade, Level, Shield, Stun, Oil, Verse, Quest, Ki, Spore, Storage, Finality, Defense, Incubator, Page,
        Study, Doom, Ice, Gold, Coin, Foreshadow,
        "+1/+0", "+0/+1", "-1/-0", "-0/-1", "+2/+2", "-2/-2", "+1/+2", "+0/+2", "-0/-2", "+2/+0", "-2/-0",
        "arrow", "arrowhead", "aim", "awakening", "blaze", "blood", "bloodline", "book", "bounty", "bribery",
        "brick", "burden", "cage", "carrion", "collection", "component", "contested", "corpse", "corruption",
        "credit", "croak", "crystal", "cube", "currency", "death", "delay", "depletion", "descent", "despair",
        "devotion", "divinity", "dream", "echo", "egg", "elixir", "ember", "enlightened", "eon", "eruption",
        "everything", "eyeball", "eyestalk", "feather", "fellowship", "fetch", "filibuster", "flame", "flood",
        "fungus", "fury", "fuse", "gem", "ghostform", "glyph", "growth", "harmony", "hatching", "hatchling",
        "healing", "hit", "hone", "hoofprint", "hour", "hourglass", "hunger", "impostor", "incarnation",
        "infection", "influence", "ingenuity", "intel", "intervention", "invitation", "isolation", "javelin",
        "journey", "judgment", "knickknack", "knowledge", "landmark", "loot", "luck", "magnet", "manabond",
        "manifestation", "mannequin", "mask", "matrix", "memory", "midway", "mine", "mining", "mire", "music",
        "muster", "necrodermis", "nest", "net", "night", "omen", "ore", "pain", "palliation", "paralyzation",
        "pause", "petal", "petrification", "phyresis", "phylactery", "pin", "plague", "plot", "point",
        "polyp", "possession", "pressure", "prey", "pupa", "rejection", "reprieve", "rev", "ribbon",
        "ritual", "rope", "rust", "scream", "scroll", "shell", "shred", "silver", "sleep", "sleight", "slime",
        "slumber", "soot", "soul", "spark", "spite", "stash", "strife", "supply", "suspect", "task", "theft",
        "tide", "tower", "training", "trap", "treasure", "unity", "unlock", "valor", "velocity", "vitality",
        "void", "vortex", "vow", "voyage", "wage", "winch", "wind", "wish", "wreck",
    };
}

/// <summary>Printed power or toughness: a number, or a characteristic-defining marker such as "*".</summary>
public sealed record PrintedStat(string Text)
{
    public int? Number => int.TryParse(Text, out var n) ? n : null;
}
