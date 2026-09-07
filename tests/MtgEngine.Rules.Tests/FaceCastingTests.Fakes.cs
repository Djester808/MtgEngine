using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Multi-faced cards and cost keywords as data, built here rather than read from <c>cards/</c>.
/// Names are deliberately not any card's name; every one of these is a shape from CR 712, 715,
/// 720 and 702, not a printing.
/// </summary>
internal static class FaceCastingFaces
{
    /// <summary>A modal double-faced card, CR 712.11b: either face may be cast from hand.</summary>
    public static CardDefinition Modal(string front = "{1}{G}", string back = "{2}{U}") => new(
        "TEST-MODAL",
        "Test Modal",
        Layout.ModalDfc,
        [
            Creature("Test Modal Front", FaceRole.Main, front),
            Creature("Test Modal Back", FaceRole.Back, back),
        ]);

    /// <summary>A modal double-faced card whose back face is a land, CR 712.12.</summary>
    public static CardDefinition ModalLand(string front = "{1}{G}") => new(
        "TEST-MODAL-LAND",
        "Test Modal Land",
        Layout.ModalDfc,
        [
            Creature("Test Modal Spell", FaceRole.Main, front),
            new Face("Test Modal Shore", FaceRole.Back, new TypeLine(CardType.Land)),
        ]);

    /// <summary>A transforming double-faced card, CR 712.11: only the front face is ever cast.</summary>
    public static CardDefinition Transforming() => new(
        "TEST-TRANSFORM",
        "Test Transform",
        Layout.Transform,
        [
            Creature("Test Transform Front", FaceRole.Main, "{1}{R}"),
            Creature("Test Transform Back", FaceRole.Back, null),
        ]);

    /// <summary>An adventurer card, CR 715: a creature with an Adventure instant half.</summary>
    public static CardDefinition Adventurer(string creature = "{1}{G}", string adventure = "{G}") => new(
        "TEST-ADVENTURE",
        "Test Adventure",
        Layout.Adventure,
        [
            Creature("Test Adventurer", FaceRole.Main, creature),
            new Face("Test Excursion", FaceRole.Adventure, new TypeLine(CardType.Instant), adventure),
        ]);

    /// <summary>A sorcery with flashback, CR 702.34: cast from the graveyard, exiled as it resolves.</summary>
    public static CardDefinition Flashback(string cost = "{2}{R}", string flashback = "{4}{R}") =>
        Single(new Face(
            "Test Recall",
            FaceRole.Main,
            new TypeLine(CardType.Sorcery),
            cost,
            Abilities: [new KeywordAbility(KeywordKind.Flashback, Cost: flashback)]));

    /// <summary>A creature with kicker, CR 702.32: an additional cost chosen as it is cast.</summary>
    public static CardDefinition Kicker(string cost = "{1}{G}", string kicker = "{2}") =>
        Single(Creature("Test Kicked Beast", FaceRole.Main, cost, [new KeywordAbility(KeywordKind.Kicker, Cost: kicker)]));

    /// <summary>An artifact with affinity for artifacts, CR 702.40a.</summary>
    public static CardDefinition Affinity(string cost = "{4}") =>
        Single(new Face(
            "Test Affine Engine",
            FaceRole.Main,
            new TypeLine(CardType.Artifact),
            cost,
            Abilities: [new KeywordAbility(KeywordKind.Affinity, Of: new ObjectFilter(Types: CardType.Artifact))]));

    /// <summary>A plain artifact to count for affinity.</summary>
    public static CardDefinition Trinket() =>
        Single(new Face("Test Trinket", FaceRole.Main, new TypeLine(CardType.Artifact), "{1}"));

    private static CardDefinition Single(Face face) =>
        new(face.Name.ToUpperInvariant(), face.Name, Layout.Normal, [face]);

    private static Face Creature(string name, FaceRole role, string? cost, IReadOnlyList<Ability>? abilities = null) =>
        new(
            name,
            role,
            new TypeLine(CardType.Creature, Subtypes: ["Beast"]),
            cost,
            new PrintedStat("2"),
            new PrintedStat("2"),
            Abilities: abilities);
}

/// <summary>
/// Enough of the query layer for a cost keyword: affinity counts permanents matching a filter,
/// which here is the filter's card types, and nothing else in these tests asks a question.
/// </summary>
internal sealed class FaceCastingQuery : IQuery
{
    public string Name => "face casting query";

    public int Evaluate(GameState state, Amount amount, EffectContext context) =>
        amount is Fixed number ? number.N : throw new NotSupportedException();

    public bool Matches(GameState state, ObjectFilter filter, ObjectId candidate, EffectContext context)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var obj = state.Objects[candidate];
        return filter.Types is not { } types || (obj.Face.Types.Types & types) == types;
    }

    public bool Holds(GameState state, Condition condition, EffectContext context) => throw new NotSupportedException();

    public ImmutableList<ObjectId> Resolve(GameState state, Selector selector, EffectContext context) => throw new NotSupportedException();

    public ImmutableList<PlayerId> Players(GameState state, PlayerRef player, EffectContext context) => [context.Controller];
}

/// <summary>
/// The zone system these tests need: a move makes a new object (CR 400.7) and a shuffle is one
/// <see cref="ZoneReordered"/> carrying the library it reordered, so CR 720.3d is observable.
/// </summary>
internal sealed class FaceCastingZones : IZoneSystem
{
    public string Name => "face casting zones";

    public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where = Placement.Default, bool tapped = false, PlayerId? controller = null)
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
                Damage = 0,
                Stack = null,
            },
            -1,
            Departed: obj);
        return new Outcome(moved.Apply(state), [moved]);
    }

    public Outcome Shuffle(GameState state, PlayerId player)
    {
        var library = ZoneKey.Of(CardZone.Library, player);
        var reordered = new ZoneReordered(library, state.In(library), Shuffle: true);
        return new Outcome(reordered.Apply(state), [reordered]);
    }

    public Outcome Create(GameSetup setup, IDefinitionStore store) => throw new NotSupportedException();

    public Outcome Draw(GameState state, PlayerId player, int count) => throw new NotSupportedException();

    public Outcome CreateToken(GameState state, TokenSpec token, PlayerId controller, bool tapped, bool attacking) => throw new NotSupportedException();
}
