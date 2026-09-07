using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The designations the rules put on an object (CR 702.180, 701.15, 701.37, 701.54, 701.60,
/// 701.64), changing a spell's targets (CR 115.7 and 114.6), "any target" (CR 115.4) and casting
/// a card from inside a resolution (CR 601). Every definition here is made up: a card is data, and
/// no card name belongs in a test of the engine either. Each designation is asserted through the
/// query layer as well as through state, because the point of a designation is that a later
/// instruction can ask about it.
/// </summary>
public sealed class InterpreterDesignationTests
{
    /* ---------------------------------------------------------------------------------------- *
     * CR 702.180, 701.60, 701.64 — designations that are one flag.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void Prepare_folds_the_designation_and_the_query_answers_it()
    {
        var table = new InterpreterTable();
        var creature = table.Permanent(table.A);

        table.Resolve(table.OnStack(table.A, new Prepare(new Remembered("it")), Remembering(creature)));

        // CR 702.180: prepared is a designation, not a counter and not an ability.
        Assert.Contains("prepared", table.Object(creature).Designations);
        Assert.True(Asks(table, creature, new IsPrepared()));
    }

    [Fact]
    public void Unprepare_takes_the_designation_off_and_does_nothing_to_a_creature_that_had_none()
    {
        var table = new InterpreterTable();
        var creature = table.Permanent(table.A);
        table.Resolve(table.OnStack(table.A, new Prepare(new Remembered("it")), Remembering(creature)));

        var events = table.Resolve(table.OnStack(table.A, new Unprepare(new Remembered("it")), Remembering(creature)));

        Assert.False(Asks(table, creature, new IsPrepared()));
        Assert.Single(events.OfType<ObjectDesignated>());

        // CR 702.180: taking away one that was never there changes nothing, so nothing is emitted
        // and nothing downstream can trigger on it.
        var again = table.Resolve(table.OnStack(table.A, new Unprepare(new Remembered("it")), Remembering(creature)));
        Assert.Empty(again.OfType<ObjectDesignated>());
    }

    [Fact]
    public void Suspect_folds_the_suspected_designation_once()
    {
        var table = new InterpreterTable();
        var creature = table.Permanent(table.A);

        table.Resolve(table.OnStack(table.A, new Suspect(new Remembered("it")), Remembering(creature)));
        var again = table.Resolve(table.OnStack(table.A, new Suspect(new Remembered("it")), Remembering(creature)));

        // CR 701.60a: a suspected creature has menace and can't block; suspecting it twice is
        // still one designation, so nothing is emitted the second time.
        Assert.True(Asks(table, creature, new IsSuspected()));
        Assert.Empty(again.OfType<ObjectDesignated>());
    }

    [Fact]
    public void Harness_folds_the_harnessed_designation()
    {
        var table = new InterpreterTable();
        var creature = table.Permanent(table.A);

        table.Resolve(table.OnStack(table.A, new Harness(new Remembered("it")), Remembering(creature)));

        // CR 701.64a.
        Assert.True(Asks(table, creature, new IsHarnessed()));
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 701.15 — goading names the goader.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void Goad_records_the_goaders_seat_so_the_query_can_ask_who()
    {
        var table = new InterpreterTable();
        var creature = table.Permanent(table.B);

        table.Resolve(table.OnStack(table.A, new Goad(new Remembered("it")), Remembering(creature)));

        // CR 701.15a: the creature must attack a player other than the one who goaded it, so the
        // designation carries that seat rather than being a single flag.
        Assert.Contains("goaded:0", table.Object(creature).Designations);
        Assert.True(Asks(table, creature, new IsGoaded()));
        Assert.True(Asks(table, creature, new IsGoaded(new You())));
        Assert.False(Asks(table, creature, new IsGoaded(new EachOpponent())));
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 701.37 — monstrosity is counters and a designation, once.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void Monstrosity_puts_the_counters_on_and_makes_it_monstrous_only_once()
    {
        var table = new InterpreterTable();
        var creature = table.Permanent(table.A);

        table.Resolve(table.OnStack(table.A, new Monstrosity(new Fixed(3)), From(creature)));
        table.Resolve(table.OnStack(table.A, new Monstrosity(new Fixed(3)), From(creature)));

        // CR 701.37a: "if this permanent isn't monstrous" — the second monstrosity does nothing at
        // all, not the counters either.
        Assert.Equal(3, table.Object(creature).Counters["+1/+1"]);
        Assert.Contains("monstrous", table.Object(creature).Designations);
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 701.54 — one Ring-bearer per player.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void A_new_ring_bearer_takes_the_designation_from_the_previous_one()
    {
        var table = new InterpreterTable();
        var first = table.Permanent(table.A);
        var second = table.Permanent(table.A);

        table.Resolve(table.OnStack(table.A, new RingTemptsYou()));
        table.Answer(first.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        table.Resolve(table.OnStack(table.A, new RingTemptsYou()));
        table.Answer(second.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // CR 701.54c: a player has one Ring-bearer, so choosing a new one takes it off the old one.
        Assert.False(Asks(table, first, new IsRingBearer()));
        Assert.True(Asks(table, second, new IsRingBearer()));
    }

    [Fact]
    public void A_player_with_no_creatures_is_not_asked_to_choose_a_ring_bearer()
    {
        var table = new InterpreterTable();

        table.Resolve(table.OnStack(table.A, new RingTemptsYou()));

        // CR 701.54d.
        Assert.Null(table.State.Pending);
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 115.7 — a spell on the stack points somewhere else.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void A_changed_target_is_the_one_the_spell_hits_when_it_resolves()
    {
        var table = new InterpreterTable();
        var was = table.Permanent(table.B, toughness: 5);
        var now = table.Permanent(table.B, toughness: 5);
        var spell = Bolt(table, was);

        table.Resolve(table.OnStack(
            table.A,
            new ChangeTarget(new Remembered("it"), new Remembered("to")),
            Remembering(spell, ("to", now))));

        // CR 115.7: the stack entry now points somewhere else, and CR 608.2b re-checks that target
        // when the spell resolves rather than the one it was cast with.
        Assert.Equal([now], table.Object(spell).Stack!.Targets);
        table.Resolve(spell);
        Assert.Equal(now, Assert.Single(table.Replacements.Proposed.OfType<DamageDealt>()).ToObject);
    }

    [Fact]
    public void A_new_target_that_does_not_fit_the_spells_filter_is_not_written()
    {
        var table = new InterpreterTable();
        var was = table.Permanent(table.B, toughness: 5);
        var land = table.Permanent(table.B, CardType.Land, power: null, toughness: null);
        var spell = Bolt(table, was);

        table.Resolve(table.OnStack(
            table.A,
            new ChangeTarget(new Remembered("it"), new Remembered("to")),
            Remembering(spell, ("to", land))));

        // CR 115.7a: the new target has to be legal for the slot it fills, so a creature spell is
        // not pointed at a land and the entry is left alone.
        Assert.Equal([was], table.Object(spell).Stack!.Targets);
    }

    [Fact]
    public void Choosing_new_targets_asks_the_player_and_writes_what_was_chosen()
    {
        var table = new InterpreterTable();
        var was = table.Permanent(table.B, toughness: 5);
        var now = table.Permanent(table.B, toughness: 5);
        var spell = Bolt(table, was);

        table.Resolve(table.OnStack(
            table.A,
            new ChangeTarget(new Remembered("it"), AllTargets: true),
            Remembering(spell)));

        // CR 114.6: "you may choose new targets for it" is a question, and the answer may name the
        // target it already had.
        Assert.Equal("target", table.Pending.Kind);
        table.Answer(now.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal([now], table.Object(spell).Stack!.Targets);
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 115.4 — "any target" is one choice with two halves.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void Damage_to_any_target_reaches_the_player_the_entry_holds()
    {
        var table = new InterpreterTable();
        var source = table.Permanent(table.A);
        var spell = table.OnStack(
            table.A,
            new DealDamage(new Fixed(3), new AnyTarget()),
            new StackEntry(source, null, [], [table.B], [], null, false));

        table.Resolve(spell);

        // CR 115.4: the player half of the announcement is the target, so that is where the damage
        // goes — and the object half is empty, so nothing else is hurt.
        Assert.Equal(17, table.Life(table.B));
        Assert.Equal(table.B, Assert.Single(table.Replacements.Proposed.OfType<DamageDealt>()).ToPlayer);
    }

    [Fact]
    public void Damage_to_any_target_reaches_the_creature_the_entry_holds()
    {
        var table = new InterpreterTable();
        var source = table.Permanent(table.A);
        var creature = table.Permanent(table.B, toughness: 5);
        var spell = table.OnStack(
            table.A,
            new DealDamage(new Fixed(3), new AnyTarget()),
            new StackEntry(source, null, [creature], [], [], null, false));

        table.Resolve(spell);

        // CR 115.4 and 608.2b: an "any target" counts as a target for the re-check, so the object
        // it named is still the object the damage goes to.
        var dealt = Assert.Single(table.Replacements.Proposed.OfType<DamageDealt>());
        Assert.Equal(creature, dealt.ToObject);
        Assert.Equal(20, table.Life(table.B));
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 601 — casting a card from inside a resolution.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void A_may_cast_asks_and_casts_the_card_when_the_answer_is_yes()
    {
        var table = new InterpreterTable();
        var casting = new InterpreterDesignationCasting();
        table.Subject.Casting = casting;
        var card = table.Card(table.A, Zone.Graveyard);

        table.Resolve(table.OnStack(table.A, new MayCast(new Remembered("it")), Remembering(card)));

        // CR 601: the permission is an offer, so the walk stops and asks; the cast itself is the
        // casting system's announcement, not the interpreter's.
        Assert.Equal("may-cast", table.Pending.Kind);
        table.Answer("yes");
        var command = Assert.Single(casting.Cast);
        Assert.Equal(card, command.Card);
        Assert.Equal(table.A, command.Player);
    }

    [Fact]
    public void A_may_cast_answered_no_casts_nothing()
    {
        var table = new InterpreterTable();
        var casting = new InterpreterDesignationCasting();
        table.Subject.Casting = casting;
        var card = table.Card(table.A, Zone.Graveyard);

        table.Resolve(table.OnStack(table.A, new MayCast(new Remembered("it")), Remembering(card)));
        table.Answer("no");

        // CR 601: nobody has to take a permission.
        Assert.Empty(casting.Cast);
    }

    [Fact]
    public void A_may_cast_the_casting_system_refuses_is_refused_with_that_rule()
    {
        var table = new InterpreterTable();
        table.Subject.Casting = new InterpreterDesignationCasting { Refusal = new Refusal("Not from there.", "601.3") };
        var card = table.Card(table.A, Zone.Graveyard);
        table.Resolve(table.OnStack(table.A, new MayCast(new Remembered("it")), Remembering(card)));

        var refused = Assert.Throws<RefusedException>(() => table.Answer("yes"));

        // The casting system decides whether a spell can be cast, and its refusal is the one the
        // table is told about (CR 601.3).
        Assert.Equal("601.3", refused.Rule);
    }

    [Fact]
    public void Casting_without_paying_the_mana_cost_casts_for_free()
    {
        var table = new InterpreterTable();
        var casting = new InterpreterDesignationCasting();
        table.Subject.Casting = casting;
        var card = table.Card(table.A, Zone.Graveyard);
        table.Resolve(table.OnStack(
            table.A,
            new MayCast(new Remembered("it"), WithoutPayingManaCost: true),
            Remembering(card)));

        table.Answer("yes");

        // CR 601.2h and 118.9: the mana cost is not paid, and the same flag is the permission to
        // cast the card from the graveyard it is in (CR 601.3a). CastPlan.For turns it into the
        // "free" entry on the stack, so "if it was cast that way" can be asked when it resolves.
        var command = Assert.Single(casting.Cast);
        Assert.True(command.WithoutPayingManaCost);
        Assert.Equal(card, command.Card);
    }

    [Fact]
    public void A_may_cast_that_pays_the_printed_cost_says_so()
    {
        var table = new InterpreterTable();
        var casting = new InterpreterDesignationCasting();
        table.Subject.Casting = casting;
        table.Resolve(table.OnStack(
            table.A,
            new MayCast(new Remembered("it")),
            Remembering(table.Card(table.A, Zone.Graveyard))));

        table.Answer("yes");

        // CR 601.2h: the flag is not the default. A card the effect did not make free is announced
        // for its printed mana cost, whatever the zone permission turns out to be.
        Assert.False(Assert.Single(casting.Cast).WithoutPayingManaCost);
    }

    [Fact]
    public void A_may_cast_of_a_card_with_an_additional_cost_refuses_naming_the_rule()
    {
        var table = new InterpreterTable();
        table.Subject.Casting = new InterpreterDesignationCasting();
        var card = table.Put(
            table.A,
            ZoneKey.Of(Zone.Graveyard, table.A),
            new CardDefinition(
                "00000000-0000-0000-0000-0000000009f1",
                "engine test object",
                Layout.Normal,
                [
                    new Face(
                        "engine test object",
                        FaceRole.Main,
                        new TypeLine(CardType.Sorcery),
                        AdditionalCosts: [new SacrificeCost(ObjectFilter.Creature)]),
                ]));
        table.Resolve(table.OnStack(
            table.A,
            new MayCast(new Remembered("it"), WithoutPayingManaCost: true),
            Remembering(card)));

        var refused = Assert.Throws<RefusedException>(() => table.Answer("yes"));

        // CR 601.2b: the additional cost is paid whatever the mana cost does, and CR 601.2c has
        // its objects chosen as the spell is announced — a yes/no question cannot name them.
        Assert.Equal("601.2b", refused.Rule);
        Assert.Contains("seam-requests/E9f.md", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_may_cast_that_lasts_creates_the_permission_instead_of_casting()
    {
        var table = new InterpreterTable();
        var casting = new InterpreterDesignationCasting();
        table.Subject.Casting = casting;
        var card = table.Card(table.A, Zone.Exile);

        table.Resolve(table.OnStack(
            table.A,
            new MayCast(new Remembered("it"), Until: new UntilEndOfTurn()),
            Remembering(card)));

        // CR 601.3a and 611.1: "you may cast it this turn" is a permission that outlives the
        // resolution, so nothing is offered now and nothing is cast now; it is a continuous
        // effect over the card, in the zone it is in.
        Assert.Null(table.State.Pending);
        Assert.Empty(casting.Cast);
        var active = Assert.Single(table.State.ActiveEffects);
        var permission = Assert.IsType<MayPlayFromZone>(active.Effect);
        Assert.Equal(Zone.Exile, permission.From);
        Assert.Equal([card], active.AppliesTo);
        Assert.IsType<UntilEndOfTurn>(active.Until);
    }

    [Fact]
    public void A_lasting_permission_that_changes_the_cost_refuses_naming_the_rule()
    {
        var table = new InterpreterTable();
        table.Subject.Casting = new InterpreterDesignationCasting();
        var card = table.Card(table.A, Zone.Exile);

        // CR 118.9: MayPlayFromZone says where a card may be cast from and nothing about what it
        // costs, so "you may cast it this turn without paying its mana cost" has no spelling.
        var refused = Assert.Throws<RefusedException>(() => table.Resolve(table.OnStack(
            table.A,
            new MayCast(new Remembered("it"), WithoutPayingManaCost: true, Until: new UntilEndOfTurn()),
            Remembering(card))));

        Assert.Equal("118.9", refused.Rule);
        Assert.Contains("seam-requests/E9f.md", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_may_cast_without_a_casting_system_refuses_naming_the_rule()
    {
        var table = new InterpreterTable();
        var card = table.Card(table.A, Zone.Graveyard);

        var refused = Assert.Throws<RefusedException>(
            () => table.Resolve(table.OnStack(table.A, new MayCast(new Remembered("it")), Remembering(card))));

        Assert.Equal("601", refused.Rule);
        Assert.Contains("seam-requests/E9e.md", refused.Message, StringComparison.Ordinal);
    }

    /* ---------------------------------------------------------------------------------------- *
     * Fixtures.
     * ---------------------------------------------------------------------------------------- */

    /// <summary>Whether the query layer answers that predicate about the object, CR 115.</summary>
    private static bool Asks(InterpreterTable table, ObjectId id, Predicate predicate) =>
        new Query.Query(table.Characteristics)
            .Matches(table.State, new ObjectFilter(Also: predicate), id, table.Context());

    /// <summary>A stack entry that remembers objects the way a trigger's would.</summary>
    private static StackEntry Remembering(ObjectId what, params (string Key, ObjectId Id)[] more)
    {
        var remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem("it", [what]);
        foreach (var (key, id) in more)
            remembered = remembered.SetItem(key, [id]);
        return new StackEntry(null, null, [], [], [], null, false) { Remembered = remembered };
    }

    /// <summary>A stack entry whose source is that permanent: an activated ability of it, CR 113.7a.</summary>
    private static StackEntry From(ObjectId source) => new(source, null, [], [], [], null, false);

    /// <summary>A spell on the stack that deals damage to one target creature, CR 115.</summary>
    private static ObjectId Bolt(InterpreterTable table, ObjectId target) => table.OnStack(
        table.A,
        new DealDamage(new Fixed(2), new Target(ObjectFilter.Creature)),
        new StackEntry(null, null, [target], [], [], null, false));
}

/// <summary>
/// A casting system that announces nothing and writes down what it was asked to cast, CR 601.2.
/// The interpreter's job is to ask the question and hand the command over; whether the spell can
/// be paid for is the real casting system's, so this one answers <see cref="Refusal"/> when a test
/// wants that half.
/// </summary>
internal sealed class InterpreterDesignationCasting : ICastingSystem
{
    public string Name => "fake casting";

    /// <summary>What <see cref="Check"/> answers: null means the command is legal, CR 601.2.</summary>
    public Refusal? Refusal { get; set; }

    public List<CastSpell> Cast { get; } = [];

    Outcome ICastingSystem.Cast(GameState state, CastSpell command)
    {
        Cast.Add(command);
        return Outcome.Unchanged(state);
    }

    public Outcome Activate(GameState state, ActivateAbility command) => Outcome.Unchanged(state);

    public Outcome PlayLand(GameState state, PlayLand command) => Outcome.Unchanged(state);

    public Outcome ResolveTop(GameState state) => Outcome.Unchanged(state);

    public Refusal? Check(GameState state, Command command) => Refusal;
}
