using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The zone system <see cref="CastingChoiceTests"/> needs: a move makes a new object (CR 400.7),
/// and the face-index form of the move (CR 712.12) gives that object the face it was played as,
/// which is the only way a permanent can enter showing the back of its card.
/// </summary>
internal sealed class CastingChoiceZones : IZoneSystem
{
    public string Name => "casting choice zones";

    public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where = Placement.Default, bool tapped = false, PlayerId? controller = null) =>
        Move(state, what, to, where, tapped, controller, null);

    public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where, bool tapped, PlayerId? controller, int? faceIndex)
    {
        var obj = state.Objects[what];
        var moved = new ObjectMoved(
            what,
            obj with
            {
                Id = new ObjectId(state.NextObjectId),
                Zone = to,
                Timestamp = state.NextTimestamp,
                Tapped = tapped,
                InitialController = controller ?? obj.InitialController,
                FaceIndex = faceIndex ?? obj.FaceIndex,
                Damage = 0,
                Stack = null,
            },
            -1,
            Departed: obj);
        return new Outcome(moved.Apply(state), [moved]);
    }

    public Outcome Create(GameSetup setup, IDefinitionStore store) => throw new NotSupportedException();

    public Outcome Draw(GameState state, PlayerId player, int count) => throw new NotSupportedException();

    public Outcome Shuffle(GameState state, PlayerId player) => throw new NotSupportedException();

    public Outcome CreateToken(GameState state, TokenSpec token, PlayerId controller, bool tapped, bool attacking) => throw new NotSupportedException();
}

/// <summary>
/// A pool that holds the restricted mana of CR 106.6a, standing in for the field
/// <c>State/PlayerState.cs</c> does not carry yet (seam-requests/E3d.md). It is how the casting
/// side is tested before the seam lands, without a static anywhere.
/// </summary>
internal sealed class CastingChoiceRestricted(ImmutableList<RestrictedBlock> blocks) : IRestrictedMana
{
    public ImmutableList<RestrictedBlock> In(ManaPool pool) => blocks;
}

/// <summary>
/// Cards as data for the choice tests, built here rather than read from <c>cards/</c>. The names
/// are deliberately not any printed card's, as CARD_ENGINE_PLAN.md § "A card is data or it does
/// not exist" requires of the engine and of the tests that stand in for it.
/// </summary>
internal static class CastingChoiceCards
{
    /// <summary>"As an additional cost to cast this spell, discard a card", CR 601.2b and 701.9a.</summary>
    public static CardDefinition DiscardSorcery() => Definition(
        "Choice Rite",
        new TypeLine(CardType.Sorcery),
        "{1}",
        additional: [new DiscardCost()]);

    /// <summary>A spell that offers a gift, CR 702.174b: the promise is made as it is cast.</summary>
    public static CardDefinition GiftSorcery() => Definition(
        "Choice Offering",
        new TypeLine(CardType.Sorcery),
        "{1}",
        abilities: [new KeywordAbility(KeywordKind.Gift)]);

    /// <summary>"{T}: Add {G}. Spend this mana only on creature spells", CR 106.6a.</summary>
    public static CardDefinition RestrictedProducer() => Definition(
        "Choice Engine",
        new TypeLine(CardType.Artifact),
        "{2}",
        abilities:
        [
            new ActivatedAbility(
                [new TapSelf()],
                new Cards.AddMana(new ManaProduction(Fixed: "{G}"), SpendOnlyOn: new ObjectFilter(Types: CardType.Creature))),
        ]);

    /// <summary>A plain card, so a hand has something to discard that is not the spell.</summary>
    public static CardDefinition Spare() => Definition("Choice Spare", new TypeLine(CardType.Instant), "{1}");

    private static CardDefinition Definition(
        string name,
        TypeLine types,
        string? cost,
        IReadOnlyList<Ability>? abilities = null,
        IReadOnlyList<Cost>? additional = null) =>
        new(
            name.ToUpperInvariant(),
            name,
            Layout.Normal,
            [new Face(name, FaceRole.Main, types, cost, Abilities: abilities, AdditionalCosts: additional)]);
}
