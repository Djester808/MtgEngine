using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Faces;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The interpreter arms that earlier waves left throwing, CR 118.12, 601.2d, 701.14, 701.23,
/// 701.55, 702.26, 707.2 and 710. Every definition here is made up: a card is data, and no card
/// name belongs in a test of the engine either. The arms that still need a seam are asserted to
/// refuse naming their rule, because a table being told is the behaviour, not an accident.
/// </summary>
public sealed class InterpreterBacklogTests
{
    /* ---------------------------------------------------------------------------------------- *
     * CR 707.2 — a copy token comes through the zone system's door.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void A_copy_token_is_created_by_the_zone_system_rather_than_by_the_interpreter()
    {
        var zones = new InterpreterBacklogZones();
        var table = new InterpreterTable(zones: zones);
        var original = table.Permanent(table.A, power: 3, toughness: 3);
        var spell = table.OnStack(
            table.A,
            new CreateToken(Token(), N: new Fixed(1), CopyOf: new Remembered("it")),
            Entry(original));

        table.Resolve(spell);

        // CR 707.2 and 614.1c: the token arrives through the same door as every other object, so
        // the zone system saw the copiable values rather than the interpreter minting an object.
        var values = Assert.Single(zones.CopyTokens);
        Assert.Equal(table.Object(original).Definition, values.Definition);
        Assert.Contains(table.State.Objects.Values, o => o.IsToken);
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 118.12 — a cost paid inside a resolving effect.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void A_cost_that_can_be_paid_inside_a_resolution_is_paid()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, new PayCost(new PayLife(new Fixed(3))));

        table.Resolve(spell);

        // CR 118.12: the cost is paid where it stands, not as part of casting.
        Assert.Equal(17, table.Life(table.A));
    }

    [Fact]
    public void A_may_pay_that_cannot_be_paid_leaves_the_if_you_do_branch_unrun()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        var spell = table.OnStack(table.A, new Sequence(
        [
            new May(new PayCost(new ManaCost("{4}")), StoreChoiceAs: "paid"),
            new If(new ChoiceIs("paid", "yes"), new Draw(new Fixed(1))),
        ]));

        table.Resolve(spell);
        table.Answer("yes");

        // CR 118.12: an empty pool cannot pay {4}, so the cost was not paid and "if you do" is
        // false — the card is still in the library and nothing was drawn.
        Assert.Single(table.In(Zone.Library, table.A));
        Assert.Empty(table.In(Zone.Hand, table.A));
    }

    [Fact]
    public void A_may_pay_that_is_paid_runs_the_if_you_do_branch()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        var spell = table.OnStack(table.A, new Sequence(
        [
            new May(new PayCost(new PayLife(new Fixed(2))), StoreChoiceAs: "paid"),
            new If(new ChoiceIs("paid", "yes"), new Draw(new Fixed(1))),
        ]));

        table.Resolve(spell);
        table.Answer("yes");

        Assert.Equal(18, table.Life(table.A));
        Assert.Single(table.In(Zone.Hand, table.A));
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 701.23 — one search over several zones.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void A_search_of_library_and_graveyard_finds_the_card_in_the_graveyard_without_shuffling()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        var buried = table.Card(table.A, Zone.Graveyard);
        var spell = table.OnStack(table.A, Across(shuffleOnlyIfFound: true));

        table.Resolve(spell);
        var events = table.Answer(buried.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // CR 701.23a: the union is one search, so the graveyard card is found; CR 701.23c shuffles
        // only a library that was searched, and the printed "if you do" rider means not even that.
        Assert.Single(table.In(Zone.Hand, table.A));
        Assert.DoesNotContain(events, e => e is ZoneReordered { Shuffle: true });
    }

    [Fact]
    public void A_search_of_library_and_graveyard_shuffles_when_the_card_came_from_the_library()
    {
        var table = new InterpreterTable();
        var top = table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Graveyard);
        var spell = table.OnStack(table.A, Across(shuffleOnlyIfFound: true));

        table.Resolve(spell);
        var events = table.Answer(top.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Contains(events, e => e is ZoneReordered { Shuffle: true });
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 710 — flipping, and CR 702.26 — phasing out.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void A_flip_permanent_turns_over_once_and_never_back()
    {
        var table = new InterpreterTable();
        var permanent = table.Put(table.A, ZoneKey.Shared(Zone.Battlefield), FlipCard());
        var spell = table.OnStack(table.A, new Flip(new Remembered("it")), Entry(permanent));

        var first = table.Resolve(spell);

        // CR 710.2: the alternative face is up now.
        Assert.Equal(1, table.Object(permanent).FaceIndex);
        Assert.Contains(first, e => e is ObjectTurnedOver { Kind: TurnKind.Flip });

        // CR 710.4: flipping is one way, so a second flip turns nothing over and triggers nothing.
        var again = table.Resolve(table.OnStack(table.A, new Flip(new Remembered("it")), Entry(permanent)));
        Assert.DoesNotContain(again, e => e is ObjectTurnedOver);
    }

    [Fact]
    public void Phasing_out_a_permanent_once_does_not_phase_it_out_again()
    {
        var table = new InterpreterTable();
        var permanent = table.Permanent(table.A);
        var first = table.Resolve(table.OnStack(table.A, new PhaseOut(new Remembered("it")), Entry(permanent)));

        Assert.True(table.Object(permanent).PhasedOut);
        Assert.Contains(first, e => e is ObjectChanged { PhasedOut: true });

        var again = table.Resolve(table.OnStack(table.A, new PhaseOut(new Remembered("it")), Entry(permanent)));
        Assert.DoesNotContain(again, e => e is ObjectChanged);
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 601.2d — a division among targets.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void Three_counters_are_distributed_two_and_one_over_two_targets()
    {
        var table = new InterpreterTable();
        var first = table.Permanent(table.A);
        var second = table.Permanent(table.A);
        var spell = table.OnStack(
            table.A,
            new DistributeCounters(new Fixed(3), "+1/+1", new Target(ObjectFilter.Creature), 3),
            new StackEntry(null, null, [first, second], [], [], null, false));

        table.Resolve(spell);
        table.Answer("2,1");

        // CR 601.2d: every named target gets at least one, and the split is the one announced.
        Assert.Equal(2, table.Object(first).Counters["+1/+1"]);
        Assert.Equal(1, table.Object(second).Counters["+1/+1"]);
    }

    [Fact]
    public void A_division_already_recorded_is_used_without_asking_again()
    {
        var table = new InterpreterTable();
        var first = table.Permanent(table.A);
        var second = table.Permanent(table.A);
        var spell = table.OnStack(
            table.A,
            new DistributeCounters(new Fixed(2), "+1/+1", new Target(ObjectFilter.Creature), 2),
            new StackEntry(null, null, [first, second], [], [], null, false));

        table.Resolve(spell);

        // CR 601.2d: two among two has one legal division, so nothing is asked.
        Assert.Null(table.State.Pending);
        Assert.Equal(1, table.Object(first).Counters["+1/+1"]);
        Assert.Equal(1, table.Object(second).Counters["+1/+1"]);
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 401.4, 701.14 and 701.55 — the rest of the backlog.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void Reorder_puts_the_named_cards_on_the_bottom_of_the_library_in_order()
    {
        var table = new InterpreterTable();
        var first = table.Card(table.A, Zone.Library);
        var second = table.Card(table.A, Zone.Library);
        var third = table.Card(table.A, Zone.Library);
        var spell = table.OnStack(
            table.A,
            new Reorder(new Remembered("it"), Placement.Bottom),
            new StackEntry(null, null, [], [], [], null, false)
            {
                Remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem("it", [first]),
            });

        table.Resolve(spell);

        // CR 401.4: nothing else may change a library's order, so the rest keep theirs.
        Assert.Equal(new[] { second, third, first }, table.In(Zone.Library, table.A));
    }

    [Fact]
    public void Two_creatures_that_fight_each_deal_damage_equal_to_their_power_to_the_other()
    {
        var table = new InterpreterTable();
        var mine = table.Permanent(table.A, power: 2, toughness: 4);
        var theirs = table.Permanent(table.B, power: 3, toughness: 5);
        var spell = table.OnStack(
            table.A,
            new Fight(new Remembered("it"), new Target(ObjectFilter.Creature)),
            new StackEntry(null, null, [theirs], [], [], null, false)
            {
                Remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem("it", [mine]),
            });

        table.Resolve(spell);

        // CR 701.14a: each deals damage equal to its power to the other.
        Assert.Equal(2, table.Object(theirs).Damage);
        Assert.Equal(3, table.Object(mine).Damage);
    }

    [Fact]
    public void A_villainous_choice_runs_the_half_the_named_player_chose()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, new VillainousChoice(
            new EachOpponent(),
            new Mode("lose 3 life", new LoseLife(new Fixed(3), new EachOpponent())),
            new Mode("lose 5 life", new LoseLife(new Fixed(5), new EachOpponent()))));

        table.Resolve(spell);

        // CR 701.55a: the opponent, not the controller, chooses.
        Assert.Equal(table.B, table.Pending.Player);
        table.Answer("1");
        Assert.Equal(15, table.Life(table.B));
    }

    /* ---------------------------------------------------------------------------------------- *
     * The arms that still need a seam refuse naming themselves and their rule.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void An_arm_that_needs_a_seam_refuses_naming_its_rule()
    {
        var table = new InterpreterTable();
        var permanent = table.Permanent(table.A);

        // CR 601: casting from inside a resolution needs the casting system, which the composition
        // sets on the interpreter; without it the table is told, rather than the card doing nothing.
        // Prepare (CR 702.180) and ChangeTarget (CR 115.7) were the other two and are real now —
        // see InterpreterDesignationTests.
        var refused = Assert.Throws<RefusedException>(
            () => table.Resolve(table.OnStack(table.A, new MayCast(new Remembered("it")), Entry(permanent))));

        Assert.Equal("601", refused.Rule);
        Assert.Contains("seam-requests/E9e.md", refused.Message, StringComparison.Ordinal);
    }

    /* ---------------------------------------------------------------------------------------- *
     * Fixtures.
     * ---------------------------------------------------------------------------------------- */

    private static Search Across(bool shuffleOnlyIfFound) => new(
        ObjectFilter.Creature,
        Zone.Hand,
        Zones: [Zone.Library, Zone.Graveyard],
        ShuffleOnlyIfFound: shuffleOnlyIfFound);

    private static TokenSpec Token() => new("engine test token", Color.Colorless, CardType.Creature);

    /// <summary>A stack entry that remembers one object under "it", the way a trigger's would.</summary>
    private static StackEntry Entry(ObjectId what) => new StackEntry(null, null, [], [], [], null, false)
    {
        Remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem("it", [what]),
    };

    /// <summary>A made-up flip card, CR 710.1: a front face and the alternative one it flips to.</summary>
    private static CardDefinition FlipCard() => new(
        "00000000-0000-0000-0000-0000000000f1",
        "engine test flip object",
        Layout.Flip,
        [
            new Face("engine test flip object", FaceRole.Main, new TypeLine(CardType.Creature)),
            new Face("engine test flipped object", FaceRole.Flipped, new TypeLine(CardType.Creature)),
        ]);
}

/// <summary>
/// A zone system that does everything the real one does and writes down the copiable values it was
/// handed, CR 707.2. It is how this suite proves the interpreter asked the zone system for a copy
/// token rather than building an object of its own.
/// </summary>
internal sealed class InterpreterBacklogZones : IZoneSystem
{
    private readonly Zones.ZoneSystem _real = new();

    public string Name => "backlog zones";

    public List<CopiableValues> CopyTokens { get; } = [];

    public Outcome Create(GameSetup setup, IDefinitionStore store) => _real.Create(setup, store);

    public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where = Placement.Default, bool tapped = false, PlayerId? controller = null) =>
        _real.Move(state, what, to, where, tapped, controller);

    public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where, bool tapped, PlayerId? controller, int? faceIndex) =>
        _real.Move(state, what, to, where, tapped, controller, faceIndex);

    public Outcome Draw(GameState state, PlayerId player, int count, IEnumerable<string>? usedReplacements = null) => _real.Draw(state, player, count, usedReplacements);

    public Outcome Shuffle(GameState state, PlayerId player, IEnumerable<string>? usedReplacements = null) => _real.Shuffle(state, player, usedReplacements);

    public Outcome CreateToken(GameState state, TokenSpec token, PlayerId controller, bool tapped, bool attacking) =>
        _real.CreateToken(state, token, controller, tapped, attacking);

    public Outcome CreateToken(GameState state, CopiableValues values, PlayerId controller, bool tapped, bool attacking)
    {
        CopyTokens.Add(values);
        return _real.CreateToken(state, values, controller, tapped, attacking);
    }
}
