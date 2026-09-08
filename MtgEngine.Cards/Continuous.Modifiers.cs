namespace MtgEngine.Cards;

/// <summary>"Whenever you tap a Forest for mana, add an additional {G}."</summary>
public sealed record ManaProducesAdditional(ObjectFilter Sources, string Mana) : ContinuousEffect;

public sealed record TokensEnterWithCounters(PlayerRef Who, string Counter, Amount Count) : ContinuousEffect;

public sealed record CountersDoubled(PlayerRef Who, string? Counter = null) : ContinuousEffect;

public sealed record LifeGainDoubled(PlayerRef Who) : ContinuousEffect;

public sealed record AbilitiesTriggerTwice(PlayerRef Who, ObjectFilter? OfSources = null) : ContinuousEffect;

[RuleRef("701.19")]
public sealed record RegenerationShield : ContinuousEffect;

[RuleRef("122.1a")]
public sealed record TreatAsHavingKeywordCounter(string Counter) : ContinuousEffect;
