using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The query seam as a cost sees it: an amount counted off the game (CR 118.8) and a filter
/// matched against a card's printed types (CR 109.1). E0 owns the real one; this is the least a
/// cost in <see cref="CastingBacklogTests"/> asks of it.
/// </summary>
internal sealed class CastingBacklogQuery : IQuery
{
    public string Name => "casting backlog query";

    public int Evaluate(GameState state, Amount amount, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);
        return amount switch
        {
            Fixed number => number.N,
            PowerOf { Of: Self } when context.Source is { } id => state.Objects[id].Face.Power?.Number ?? 0,
            _ => throw new NotSupportedException($"{amount?.GetType().Name} is not evaluated here."),
        };
    }

    public bool Matches(GameState state, ObjectFilter filter, ObjectId candidate, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(context);
        // CR 613.1b: the "you control" half. Without it a filter naming a controller matched
        // everything, and a cost could be paid with an opponent's permanent.
        if (filter.ControlledBy is not null && state.Objects[candidate].InitialController != context.Controller)
            return false;
        var types = state.Objects[candidate].Face.Types.Types;
        return filter.Types is not { } wanted || (types & wanted) == wanted;
    }

    public bool Holds(GameState state, Condition condition, EffectContext context) => throw new NotSupportedException();

    public ImmutableList<ObjectId> Resolve(GameState state, Selector selector, EffectContext context) => throw new NotSupportedException();

    public ImmutableList<PlayerId> Players(GameState state, PlayerRef player, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [context.Controller];
    }
}

/// <summary>
/// Cards as data for the backlog tests, built here rather than read from <c>cards/</c>. The names
/// are deliberately not any printed card's: CARD_ENGINE_PLAN.md § "A card is data or it does not
/// exist" keeps card names out of the engine, and out of the tests that stand in for it.
/// </summary>
internal static class CastingBacklogCards
{
    /// <summary>"As an additional cost to cast this spell, sacrifice a creature", CR 601.2b.</summary>
    public static CardDefinition SacrificeSorcery() => Definition(
        "Backlog Rite",
        new TypeLine(CardType.Sorcery),
        "{2}",
        additional: [new SacrificeCost(new ObjectFilter(Types: CardType.Creature))]);

    /// <summary>Multikicker written as a repeatable additional cost, CR 702.32b.</summary>
    public static CardDefinition MultikickerSorcery(bool repeatable = true) => Definition(
        "Backlog Chant",
        new TypeLine(CardType.Sorcery),
        "{1}",
        abilities: [new KeywordAbility(KeywordKind.Kicker, "{1}", Repeatable: repeatable)]);

    /// <summary>Escape: mana plus the non-mana half its <see cref="KeywordAbility.Costs"/> carries, CR 702.138a.</summary>
    public static CardDefinition EscapeSorcery() => Definition(
        "Backlog Return",
        new TypeLine(CardType.Sorcery),
        "{4}",
        abilities:
        [
            new KeywordAbility(
                KeywordKind.Escape,
                "{1}",
                Costs: [new ExileFromZoneCost(Zone.Graveyard, Count: new Fixed(2))]),
        ]);

    /// <summary>An ability whose generic cost is counted off the game, CR 118.8.</summary>
    public static CardDefinition CountedCostCreature() => Definition(
        "Backlog Warden",
        new TypeLine(CardType.Creature, Subtypes: ["Beast"]),
        "{2}",
        power: 3,
        toughness: 3,
        abilities: [new ActivatedAbility([new PayManaCost(new PowerOf(new Self()))], new Draw(new Fixed(1)))]);

    /// <summary>"Activate only using green mana": the spend restriction of CR 106.6b.</summary>
    public static CardDefinition GreenOnlyEngine() => Definition(
        "Backlog Engine",
        new TypeLine(CardType.Artifact),
        "{2}",
        abilities: [new ActivatedAbility([new ManaCost("{1}")], new Draw(new Fixed(1)), PayOnlyWith: Color.Green)]);

    public static CardDefinition Beast() => Definition("Backlog Beast", new TypeLine(CardType.Creature, Subtypes: ["Beast"]), "{1}{G}", 2, 2);

    private static CardDefinition Definition(
        string name,
        TypeLine types,
        string? cost,
        int? power = null,
        int? toughness = null,
        IReadOnlyList<Ability>? abilities = null,
        IReadOnlyList<Cost>? additional = null) =>
        new(
            name.ToUpperInvariant(),
            name,
            Layout.Normal,
            [
                new Face(
                    name,
                    FaceRole.Main,
                    types,
                    cost,
                    power is null ? null : new PrintedStat(power.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    toughness is null ? null : new PrintedStat(toughness.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    Abilities: abilities,
                    AdditionalCosts: additional),
            ]);
}
