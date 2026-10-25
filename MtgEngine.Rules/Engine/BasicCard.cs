using MtgEngine.Cards;

namespace MtgEngine.Rules.Engine;

public enum BasicBehavior { Land, Creature, GainLife, ReturnCreature }

/// <summary>Trusted, explicitly authored support declaration; never supplied by a player.</summary>
public sealed record BasicCard(CardDefinition Definition, BasicBehavior Behavior, int Amount = 0)
{
    /// <summary>Explicit executable declaration, separate from printed card data. Creature-only.</summary>
    public ImmutableHashSet<BasicKeyword> Keywords { get; init; } = [];
}

public enum BasicKeyword
{
    Flying, Reach, Vigilance, Haste, Defender, Menace, Trample, Deathtouch, Lifelink,
    FirstStrike, DoubleStrike, Indestructible,
}

/// <summary>One complete casting choice. Payment specifies exact mana spent, including generic allocation.</summary>
public sealed record CastChoice(State.ObjectId Card, State.ManaPool Payment,
    State.ObjectId? Target = null, State.PlayerId? TargetPlayer = null,
    ImmutableList<State.ObjectId>? ManaSources = null);
