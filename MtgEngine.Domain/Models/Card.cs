using MtgEngine.Domain.Enums;
using MtgEngine.Domain.ValueObjects;
using MtgEngine.Domain.Interfaces;

namespace MtgEngine.Domain.Models;

/// <summary>
/// Immutable oracle definition of a card. Shared across all copies.
/// Think of this as the card's "type" -- loaded once from Scryfall.
/// </summary>
public sealed class CardDefinition
{
    public string OracleId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public ManaCost ManaCost { get; init; } = ManaCost.Zero;
    /// <summary>Raw Scryfall mana cost string e.g. "{2}{W}{B}". Used for display only.</summary>
    public string ManaCostRaw { get; init; } = string.Empty;
    public CardType CardTypes { get; init; }
    public IReadOnlyList<string> Subtypes { get; init; } = [];
    public IReadOnlyList<string> Supertypes { get; init; } = [];
    public string OracleText { get; init; } = string.Empty;
    public int? Power { get; init; }
    public int? Toughness { get; init; }
    public int? StartingLoyalty { get; init; }
    public KeywordAbility Keywords { get; init; }
    public SpeedRestriction CastingSpeed { get; init; }

    // Scryfall image URIs and metadata -- populated by ScryfallService
    public string? ImageUriNormal { get; init; }
    public string? ImageUriNormalBack { get; init; }
    public string? ImageUriSmall { get; init; }
    public string? ImageUriArtCrop { get; init; }
    public IReadOnlyList<ManaColor> ColorIdentity { get; init; } = [];
    public string? FlavorText { get; init; }
    public string? Artist { get; init; }
    public string? SetCode { get; init; }
    public IReadOnlyDictionary<string, string> Legalities { get; init; } = new Dictionary<string, string>();

    public bool IsCreature    => CardTypes.HasFlag(CardType.Creature);
    public bool IsInstant     => CardTypes.HasFlag(CardType.Instant);
    public bool IsSorcery     => CardTypes.HasFlag(CardType.Sorcery);
    public bool IsLand        => CardTypes.HasFlag(CardType.Land);
    public bool IsEnchantment => CardTypes.HasFlag(CardType.Enchantment);
    public bool IsArtifact    => CardTypes.HasFlag(CardType.Artifact);
    public bool IsPlaneswalker=> CardTypes.HasFlag(CardType.Planeswalker);
    public bool IsNonland     => !IsLand;
    public bool IsPermanentType => IsCreature || IsEnchantment || IsArtifact || IsLand || IsPlaneswalker;

    public bool HasKeyword(KeywordAbility kw) => Keywords.HasFlag(kw);

    /// <summary>Returns the basic land color this produces, if applicable.</summary>
    public ManaColor? BasicLandColor => Name switch
    {
        "Plains"   => ManaColor.White,
        "Island"   => ManaColor.Blue,
        "Swamp"    => ManaColor.Black,
        "Mountain" => ManaColor.Red,
        "Forest"   => ManaColor.Green,
        _ => null
    };
}

/// <summary>
/// A specific physical card instance in a game. Has a unique CardId.
/// References its oracle definition. Carries zone-independent state (owner).
/// </summary>
public sealed class Card
{
    public Guid CardId { get; init; } = Guid.NewGuid();
    public CardDefinition Definition { get; init; } = null!;
    public Guid OwnerId { get; init; }

    // Convenience pass-throughs
    public string Name => Definition.Name;
    public ManaCost ManaCost => Definition.ManaCost;
    public CardType CardTypes => Definition.CardTypes;
    public KeywordAbility Keywords => Definition.Keywords;
    public bool IsLand => Definition.IsLand;
    public bool IsPermanentType => Definition.IsPermanentType;

    public Card WithOwner(Guid ownerId) => new()
    {
        CardId = this.CardId,
        Definition = this.Definition,
        OwnerId = ownerId,
    };
}

/// <summary>
/// A permanent on the battlefield. Wraps a Card with battlefield-specific state.
/// Immutable -- all mutations return a new Permanent.
/// </summary>
public sealed record Permanent
{
    public Guid PermanentId { get; init; } = Guid.NewGuid();
    public Card SourceCard { get; init; } = null!;
    public Guid ControllerId { get; init; }
    public bool IsTapped { get; init; }
    public bool HasSummoningSickness { get; init; } = true;
    public int DamageMarked { get; init; }
    public bool HasDeathtouchDamage { get; init; }
    public IReadOnlyDictionary<CounterType, int> Counters { get; init; } = new Dictionary<CounterType, int>();
    public IReadOnlyList<Guid> Attachments { get; init; } = [];

    // Definition pass-throughs
    public CardDefinition Definition => SourceCard.Definition;
    public string Name => Definition.Name;
    public CardType CardTypes => Definition.CardTypes;
    public KeywordAbility Keywords => Definition.Keywords;
    public bool IsCreature => Definition.IsCreature;
    public bool IsLand => Definition.IsLand;

    /// <summary>Effective power after +1/+1 and -1/-1 counters.</summary>
    public int? EffectivePower =>
        Definition.Power.HasValue
            ? Definition.Power.Value
              + Counters.GetValueOrDefault(CounterType.PlusOnePlusOne)
              - Counters.GetValueOrDefault(CounterType.MinusOneMinusOne)
            : null;

    /// <summary>Effective toughness after counters.</summary>
    public int? EffectiveToughness =>
        Definition.Toughness.HasValue
            ? Definition.Toughness.Value
              + Counters.GetValueOrDefault(CounterType.PlusOnePlusOne)
              - Counters.GetValueOrDefault(CounterType.MinusOneMinusOne)
            : null;

    public bool HasKeyword(KeywordAbility kw) => Keywords.HasFlag(kw);

    public bool CanAttack =>
        IsCreature &&
        !IsTapped &&
        (!HasSummoningSickness || HasKeyword(KeywordAbility.Haste));

    public bool CanBlock => IsCreature && !IsTapped;

    // Fluent mutators -- each returns a new Permanent
    public Permanent Tap()      => this with { IsTapped = true };
    public Permanent Untap()    => this with { IsTapped = false };

    public Permanent ClearSummoningSickness() => this with { HasSummoningSickness = false };

    public Permanent AddDamage(int amount, bool fromDeathtouch = false) =>
        this with { DamageMarked = DamageMarked + amount, HasDeathtouchDamage = HasDeathtouchDamage || fromDeathtouch };
    public Permanent ClearDamage() => this with { DamageMarked = 0, HasDeathtouchDamage = false };

    public Permanent AddCounter(CounterType type, int count = 1)
    {
        var next = new Dictionary<CounterType, int>(Counters);
        next[type] = next.GetValueOrDefault(type) + count;
        return this with { Counters = next };
    }

    public Permanent RemoveCounter(CounterType type, int count = 1)
    {
        var next = new Dictionary<CounterType, int>(Counters);
        int current = next.GetValueOrDefault(type);
        int after = current - count;
        if (after <= 0) next.Remove(type);
        else next[type] = after;
        return this with { Counters = next };
    }

    public Permanent WithController(Guid controllerId) => this with { ControllerId = controllerId };
}
