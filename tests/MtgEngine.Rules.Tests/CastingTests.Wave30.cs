using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Interpreter;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Wave 30: the two halves of a cast that the resolving spell's own shape decides — a face-down
/// spell entering the battlefield face down (CR 702.37c) and the range of targets a spell may be
/// announced with (CR 601.2c). Every fixture here is invented in this file; no card file is read
/// and no card name appears.
/// </summary>
public sealed class CastingWave30Tests
{
    private readonly CastingSystem casting;
    private readonly Wave30RecordingZones zones = new();

    public CastingWave30Tests()
    {
        var chars = new PrintedCharacteristics();
        var query = new Query.Query(chars);
        casting = new CastingSystem(zones, query, chars, new EffectInterpreter(query, zones, new CastingPassThroughReplacements(), chars));
    }

    // --- CR 702.37c: a face-down spell resolves into a face-down permanent --------------------

    [Fact]
    [RuleRef("702.37c")]
    public void A_face_down_spell_enters_the_battlefield_face_down()
    {
        // CR 702.37c: "when the spell resolves, it enters the battlefield with the same
        // characteristics the spell had". CR 400.7 gives the new object no memory of the old one,
        // so the flag has to ride on the move; before this it was turned face up as it resolved.
        var table = new CastingTable();
        var spell = Wave30SpellOnStack(table, faceDown: true);

        var resolved = casting.ResolveTop(table.State);

        Assert.True(zones.FaceDown);
        Assert.True(resolved.State.Objects.Values.Single(o => o.Zone.Zone == CardZone.Battlefield).FaceDown);
    }

    [Fact]
    [RuleRef("608.3")]
    public void An_ordinary_permanent_spell_still_enters_face_up()
    {
        // The flag is read off the resolving object, so a spell that was never face down passes
        // false and nothing about CR 608.3's ordinary resolution changed.
        var table = new CastingTable();
        Wave30SpellOnStack(table, faceDown: false);

        var resolved = casting.ResolveTop(table.State);

        Assert.False(zones.FaceDown);
        Assert.False(resolved.State.Objects.Values.Single(o => o.Zone.Zone == CardZone.Battlefield).FaceDown);
    }

    // --- CR 601.2c: "one or two target creatures" --------------------------------------------

    [Fact]
    [RuleRef("601.2c")]
    public void A_cast_naming_fewer_targets_than_the_low_end_is_refused()
    {
        // CR 601.2c: the number of targets is announced with the cast, and a range's low end is
        // part of what the spell requires. Naming none is not "up to two" — that is a different
        // record, and the vocabulary refuses the two together.
        var table = Wave30RangeTable(out var spell, out _, out _);

        var refusal = casting.Check(table.State, new CastSpell(CastingTable.A, spell));

        Assert.Equal("601.2c", refusal?.Rule);
    }

    [Fact]
    [RuleRef("601.2c")]
    public void A_cast_naming_the_low_end_is_allowed()
    {
        var table = Wave30RangeTable(out var spell, out var one, out _);

        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, spell, Targets: [one])));
    }

    [Fact]
    [RuleRef("601.2c")]
    public void A_cast_naming_the_high_end_is_allowed()
    {
        var table = Wave30RangeTable(out var spell, out var one, out var two);

        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, spell, Targets: [one, two])));
    }

    [Fact]
    [RuleRef("601.2c")]
    public void A_cast_naming_more_than_the_high_end_is_refused()
    {
        // The other end of the same announcement: a spell that offers at most two targets was
        // never offering a third, so the announcement is refused rather than silently trimmed.
        var table = Wave30RangeTable(out var spell, out var one, out var two);
        var three = table.Add(TestFaces.Creature(), CastingTable.B, CardZone.Battlefield);

        var refusal = casting.Check(table.State, new CastSpell(CastingTable.A, spell, Targets: [one, two, three]));

        Assert.Equal("601.2c", refusal?.Rule);
    }

    [Fact]
    [RuleRef("601.2c")]
    public void A_target_without_a_range_is_not_counted()
    {
        // A bare Target is every stored card's shape and nothing counted it before this file, so
        // a spell that prints no range casts with no targets exactly as it always did.
        var table = new CastingTable();
        var spell = table.Add(Wave30Ranged(min: null, most: null), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 4));

        Assert.Null(casting.Check(table.State, new CastSpell(CastingTable.A, spell)));
    }

    // --- fixtures ----------------------------------------------------------------------------

    /// <summary>
    /// A permanent spell cast through the front door and then, for the face-down case, turned face
    /// down on the stack the way CR 702.37c's morph ability put it there. The casting system has no
    /// morph road of its own, so the flag is the only thing this fixture invents.
    /// </summary>
    private ObjectId Wave30SpellOnStack(CastingTable table, bool faceDown)
    {
        var card = table.Add(TestFaces.Creature("{1}"), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));
        table.Apply(casting.Cast(table.State, new CastSpell(CastingTable.A, card)).Events);
        var top = StackZone.Top(table.State) ?? throw new InvalidOperationException("the spell is not on the stack");
        if (faceDown)
            table.Apply([new ObjectChanged(top, FaceDown: true)]);
        return top;
    }

    /// <summary>An instant that destroys "one or two target creatures", with two on the battlefield.</summary>
    private static CastingTable Wave30RangeTable(out ObjectId spell, out ObjectId one, out ObjectId two)
    {
        var table = new CastingTable();
        spell = table.Add(Wave30Ranged(min: 1, most: 2), CastingTable.A, CardZone.Hand);
        one = table.Add(TestFaces.Creature(), CastingTable.B, CardZone.Battlefield);
        two = table.Add(TestFaces.Creature(), CastingTable.B, CardZone.Battlefield);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 4));
        return table;
    }

    /// <summary>
    /// "Destroy one or two target creatures", CR 601.2c, built here rather than read from a card
    /// file: a range is <see cref="Target.Min"/> beside <see cref="Target.Count"/> on one selector.
    /// </summary>
    private static CardDefinition Wave30Ranged(int? min, int? most) =>
        new(
            "TEST RANGED RITE",
            "Test Ranged Rite",
            Layout.Normal,
            [
                new Face(
                    "Test Ranged Rite",
                    FaceRole.Main,
                    new TypeLine(CardType.Instant),
                    "{1}",
                    Abilities:
                    [
                        new SpellAbility(new Destroy(new Target(
                            new ObjectFilter(Types: CardType.Creature),
                            Count: most is null ? null : new Fixed(most.Value),
                            Min: min is null ? null : new Fixed(min.Value)))),
                    ]),
            ]);
}

/// <summary>
/// The zone seam of <see cref="CastingFakeZones"/> with the face-down overload built, so a test can
/// see the flag CR 702.37c makes the move carry rather than only the object it produced.
/// </summary>
internal sealed class Wave30RecordingZones : IZoneSystem
{
    public string Name => "wave 30 recording zones";

    /// <summary>What the last move said about face-down status, CR 701.40a.</summary>
    public bool FaceDown { get; private set; }

    public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where = Placement.Default, bool tapped = false, PlayerId? controller = null) =>
        Move(state, what, to, where, tapped, controller, null, faceDown: false);

    public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where, bool tapped, PlayerId? controller, int? faceIndex) =>
        Move(state, what, to, where, tapped, controller, faceIndex, faceDown: false);

    public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where, bool tapped, PlayerId? controller, int? faceIndex, bool faceDown, IEnumerable<string>? usedReplacements = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        FaceDown = faceDown;
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
                FaceDown = faceDown,
                Damage = 0,
                Stack = null,
            },
            -1);
        return new Outcome(moved.Apply(state), [moved]);
    }

    public Outcome Create(GameSetup setup, IDefinitionStore store) => throw new NotSupportedException();

    public Outcome Draw(GameState state, PlayerId player, int count, IEnumerable<string>? usedReplacements = null) => throw new NotSupportedException();

    public Outcome Shuffle(GameState state, PlayerId player, IEnumerable<string>? usedReplacements = null) => throw new NotSupportedException();

    public Outcome CreateToken(GameState state, TokenSpec token, PlayerId controller, bool tapped, bool attacking) => throw new NotSupportedException();
}
