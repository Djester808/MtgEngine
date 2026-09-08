using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The interpreter, CR 608.2: what a resolving spell or ability does to the game. Every test
/// builds its own made-up definition, because a card is data and no card name belongs in here.
/// </summary>
public sealed partial class InterpreterTests
{
    [Fact]
    public void Burn_spell_deals_damage_to_a_creature_and_to_a_player()
    {
        var table = new InterpreterTable();
        var creature = table.Permanent(table.B, toughness: 4);
        var spell = table.OnStack(table.A, new Sequence(
        [
            new DealDamage(new Fixed(3), To: new Target(ObjectFilter.Creature)),
            new DealDamage(new Fixed(2), ToPlayer: new TargetPlayer(PlayerFilter.Any)),
        ]),
            new StackEntry(null, null, [creature], [table.B], [], null, false));

        var events = table.Resolve(spell);

        Assert.Equal(3, table.State.Objects[creature].Damage);
        Assert.Equal(18, table.Life(table.B));
        Assert.Contains(events, e => e is StackObjectResolved);
        // CR 616: every damage event was offered to the replacement layer first.
        Assert.Equal(2, table.Replacements.Proposed.Count);
    }

    [Fact]
    public void Damage_from_a_deathtouch_source_is_marked_as_deathtouch_damage()
    {
        var table = new InterpreterTable();
        var creature = table.Permanent(table.B, toughness: 4);
        var source = table.Permanent(table.A, keywords: [KeywordKind.Deathtouch]);
        var events = table.Run(
            new DealDamage(new Fixed(1), To: new Target(ObjectFilter.Creature)),
            table.Context(source) with { Targets = [creature] });

        // CR 702.2b: damage from a source with deathtouch is deathtouch damage.
        Assert.True(Assert.IsType<DamageDealt>(events.Single()).Deathtouch);
        Assert.True(table.State.Objects[creature].DealtDeathtouchDamage);
    }

    [Fact]
    public void Draw_two_moves_two_cards_from_the_library_to_the_hand()
    {
        var table = new InterpreterTable();
        table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Library);
        table.Card(table.A, Zone.Library);
        var spell = table.OnStack(table.A, new Draw(new Fixed(2)));

        table.Resolve(spell);

        Assert.Equal(2, table.In(Zone.Hand, table.A).Count);
        Assert.Single(table.In(Zone.Library, table.A));
    }

    [Fact]
    public void Pump_creates_a_continuous_effect_whose_amounts_are_already_worked_out()
    {
        var table = new InterpreterTable();
        var creature = table.Permanent(table.A);
        // "Target creature gets +X/+2 until end of turn" with X paid as 2: CR 611.2b locks the
        // value in when the effect is created, so the stored effect must carry no live amount.
        var spell = table.OnStack(
            table.A,
            new Apply(new ModifyPt(new X(), new Fixed(2)), new Target(ObjectFilter.Creature), new UntilEndOfTurn()),
            new StackEntry(null, null, [creature], [], [], 2, false));

        table.Resolve(spell);

        var active = Assert.Single(table.State.ActiveEffects);
        var pt = Assert.IsType<ModifyPt>(active.Effect);
        Assert.Equal(new Fixed(2), pt.Power);
        Assert.Equal(new Fixed(2), pt.Toughness);
        Assert.Equal([creature], active.AppliesTo);
        Assert.IsType<UntilEndOfTurn>(active.Until);
    }

    [Fact]
    public void Modal_spell_runs_only_the_mode_that_was_chosen()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(
            table.A,
            new ChooseMode(
            [
                new Mode("gain 5 life", new GainLife(new Fixed(5))),
                new Mode("lose 3 life", new LoseLife(new Fixed(3))),
            ]),
            new StackEntry(null, null, [], [], [1], null, false));

        table.Resolve(spell);

        Assert.Equal(17, table.Life(table.A));
    }

    [Fact]
    public void A_may_stops_the_resolution_and_asks()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, new May(new GainLife(new Fixed(4))));

        var events = table.Resolve(spell);

        var asked = Assert.IsType<ChoiceRequested>(Assert.Single(events));
        Assert.Equal("may", asked.Choice.Kind);
        Assert.Equal(table.A, asked.Choice.Player);
        Assert.Equal(20, table.Life(table.A));
        // The spell has not resolved: nothing may move it off the stack yet, CR 608.2.
        Assert.DoesNotContain(events, e => e is StackObjectResolved);
        Assert.NotNull(table.State.Pending);
    }

    [Fact]
    public void An_answered_may_runs_the_effect()
    {
        var table = new InterpreterTable();
        var context = table.Context() with
        {
            Chosen = ImmutableDictionary<string, string>.Empty.SetItem("gain", "yes"),
        };

        table.Run(new May(new GainLife(new Fixed(4)), StoreChoiceAs: "gain"), context);

        Assert.Equal(24, table.Life(table.A));
    }

    [Fact]
    public void An_illegal_target_is_skipped_and_the_legal_one_is_not()
    {
        var table = new InterpreterTable();
        var creature = table.Permanent(table.B, toughness: 4);
        var artifact = table.Permanent(table.B, CardType.Artifact, power: null, toughness: null);
        var spell = table.OnStack(
            table.A,
            new DealDamage(new Fixed(2), To: new Target(ObjectFilter.Creature)),
            new StackEntry(null, null, [artifact, creature], [], [], null, false));

        table.Resolve(spell);

        // CR 608.2b: the artifact is no longer a legal target and is simply skipped.
        Assert.Equal(0, table.State.Objects[artifact].Damage);
        Assert.Equal(2, table.State.Objects[creature].Damage);
    }

    [Fact]
    public void A_spell_whose_every_target_is_illegal_does_nothing()
    {
        var table = new InterpreterTable();
        var artifact = table.Permanent(table.B, CardType.Artifact, power: null, toughness: null);
        var spell = table.OnStack(
            table.A,
            new DealDamage(new Fixed(2), To: new Target(ObjectFilter.Creature)),
            new StackEntry(null, null, [artifact], [], [], null, false));

        var events = table.Resolve(spell);

        // CR 608.2b: the spell does not resolve, so nothing it would have done happens.
        Assert.Equal(0, table.State.Objects[artifact].Damage);
        Assert.Empty(table.Replacements.Proposed);
        Assert.IsType<StackObjectResolved>(Assert.Single(events));
    }

    [Fact]
    public void A_remembered_target_is_still_there_for_a_later_step()
    {
        var table = new InterpreterTable();
        var creature = table.Permanent(table.B);
        var spell = table.OnStack(
            table.A,
            new Sequence(
            [
                new Remember("it", new Target(ObjectFilter.Creature)),
                new PutCounters(new Remembered("it"), "+1/+1", new Fixed(2)),
                new Cards.Move(new Remembered("it"), Zone.Hand),
            ]),
            new StackEntry(null, null, [creature], [], [], null, false));

        table.Resolve(spell);

        Assert.Single(table.In(Zone.Hand, table.B));
        Assert.False(table.State.Objects.ContainsKey(creature));
    }

    [Fact]
    public void For_each_binds_the_object_it_is_running_over()
    {
        var table = new InterpreterTable();
        table.Permanent(table.A);
        table.Permanent(table.A);
        var context = table.Context();

        table.Run(new ForEach(new Each(ObjectFilter.Creature), new PutCounters(new Triggering(), "+1/+1", new Fixed(1))), context);

        Assert.All(table.State.Objects.Values, o => Assert.Equal(1, o.Counters.GetValueOrDefault("+1/+1")));
    }

    [Fact]
    public void Destroy_leaves_an_indestructible_permanent_alone()
    {
        var table = new InterpreterTable();
        var plain = table.Permanent(table.A);
        var tough = table.Permanent(table.A, keywords: [KeywordKind.Indestructible]);

        var events = table.Run(new Destroy(new Each(ObjectFilter.Creature)), table.Context());

        // CR 701.8b: an indestructible permanent is not destroyed.
        Assert.True(table.State.Objects.ContainsKey(tough));
        Assert.False(table.State.Objects.ContainsKey(plain));
        Assert.Equal("destroyed", Assert.IsType<ObjectLeftBy>(events[0]).Verb);
        Assert.Single(table.In(Zone.Graveyard, table.A));
    }

    [Fact]
    [RuleRef("608.2")]
    public void An_effect_with_no_arm_names_the_record_it_could_not_run()
    {
        // CR 608.2: the dispatch fallback refuses by name. It threw a NotImplementedException until
        // wave 22 — a crash the caller cannot tell from a bug in the engine.
        var table = new InterpreterTable();

        var missing = Assert.Throws<RefusedException>(
            () => table.Run(new Investigate(), table.Context()));

        Assert.Contains(nameof(Investigate), missing.Reason, StringComparison.Ordinal);
        Assert.Equal("608.2", missing.Rule);
    }

    /* ---------------------------------------------------------------------------------------- *
     * CR 601 — "you may cast that card": the free lasting permission, and the costs it still pays.
     * ---------------------------------------------------------------------------------------- */

    [Fact]
    public void A_lasting_permission_to_cast_for_free_carries_that_on_the_effect()
    {
        var table = new InterpreterTable();
        table.Subject.Casting = new InterpreterTestsCasting();
        var card = table.Card(table.A, Zone.Exile);

        table.Resolve(table.OnStack(
            table.A,
            new MayCast(new Remembered("it"), WithoutPayingManaCost: true, Until: new UntilEndOfTurn()),
            Remembering(card)));

        // CR 118.9: "you may cast it this turn without paying its mana cost" is one permission with
        // two halves — where it may be cast from and what it costs — and the effect says both, so
        // the player takes it later without the permission having forgotten the free half.
        var active = Assert.Single(table.State.ActiveEffects);
        var permission = Assert.IsType<MayPlayFromZone>(active.Effect);
        Assert.True(permission.WithoutPayingManaCost);
        Assert.Equal(Zone.Exile, permission.From);
        Assert.Equal([card], active.AppliesTo);
    }

    [Fact]
    public void A_may_cast_whose_additional_cost_needs_no_object_is_cast()
    {
        var table = new InterpreterTable();
        var casting = new InterpreterTestsCasting();
        table.Subject.Casting = casting;
        var card = table.Put(table.A, ZoneKey.Of(Zone.Graveyard, table.A), WithCost(new PayLife(new Fixed(2))));

        table.Resolve(table.OnStack(table.A, new MayCast(new Remembered("it")), Remembering(card)));
        table.Answer("yes");

        // CR 601.2b: an additional cost is paid whether or not the mana cost is, and whether this
        // command can pay it is the casting system's word. A cost that names no object is announced
        // and cast; the printed face is not read here to refuse it in advance.
        Assert.Equal(card, Assert.Single(casting.Instructed).Card);
    }

    [Fact]
    public void A_sacrifice_cost_is_asked_for_and_the_answer_pays_it()
    {
        var table = new InterpreterTable();
        var casting = new InterpreterTestsCasting();
        table.Subject.Casting = casting;
        var mine = table.Permanent(table.A);
        var other = table.Permanent(table.A);
        table.Permanent(table.B);
        casting.Choices.Add(new CostChoice("sacrifice a creature", [mine, other], 1));
        var card = table.Put(
            table.A,
            ZoneKey.Of(Zone.Graveyard, table.A),
            WithCost(new SacrificeCost(ObjectFilter.Creature)));

        table.Resolve(table.OnStack(table.A, new MayCast(new Remembered("it")), Remembering(card)));
        table.Answer("yes");

        // CR 601.2c: the objects an additional cost consumes are chosen as the spell is announced,
        // so the yes is followed by a second question — of exactly what the casting system said the
        // command still has to name, because it is the only thing that knows what a cost consumes.
        Assert.Equal("cast-cost", table.Pending.Kind);
        Assert.Equal([mine, other], table.Pending.Options.Select(o => o.Object));
        Assert.Equal(1, table.Pending.Min);

        table.Answer(mine.Value.ToString(CultureInfo.InvariantCulture));

        var command = Assert.Single(casting.Instructed);
        Assert.Equal([mine], command.CostObjects);
    }

    [Fact]
    public void The_cast_cost_question_is_answered_by_its_resumption_alone()
    {
        var table = new InterpreterTable();
        var casting = new InterpreterTestsCasting();
        table.Subject.Casting = casting;
        var mine = table.Permanent(table.A);
        casting.Choices.Add(new CostChoice("sacrifice a creature", [mine], 1));
        var card = table.Put(
            table.A,
            ZoneKey.Of(Zone.Graveyard, table.A),
            WithCost(new SacrificeCost(ObjectFilter.Creature)));
        var spell = table.OnStack(table.A, new MayCast(new Remembered("it")), Remembering(card));
        table.Resolve(spell);
        table.Answer("yes");
        var pending = table.Pending;

        // CR 101.4: there is no captured continuation. The question carries the path back into the
        // effect tree, so the answer alone puts the walk where it stopped — a game rebuilt from its
        // log resumes the same way.
        var resumed = table.Subject.Resume(
            table.State,
            new Resumption(pending.Kind, pending.Player, [new ChoiceOption(mine.Value.ToString(CultureInfo.InvariantCulture), "one", mine)], pending.Context));

        Assert.Equal([mine], Assert.Single(casting.Instructed).CostObjects);
        Assert.Contains(resumed.Events, e => e is StackObjectResolved);
    }

    [Fact]
    public void A_refusal_the_casting_system_gives_is_relayed_with_its_own_rule()
    {
        var table = new InterpreterTable();
        table.Subject.Casting = new InterpreterTestsCasting
        {
            Refusal = new Refusal("There is nothing to sacrifice.", "601.2h"),
        };
        var card = table.Put(
            table.A,
            ZoneKey.Of(Zone.Graveyard, table.A),
            WithCost(new SacrificeCost(ObjectFilter.Creature)));

        table.Resolve(table.OnStack(table.A, new MayCast(new Remembered("it")), Remembering(card)));
        var refused = Assert.Throws<RefusedException>(() => table.Answer("yes"));

        // The casting system decides whether a spell can be announced, and a refusal that is not
        // "nobody chose the objects" (CR 601.2c) is relayed as it stands rather than turned into a
        // question nobody can answer.
        Assert.Equal("601.2h", refused.Rule);
        Assert.Equal("There is nothing to sacrifice.", refused.Message);
    }

    /// <summary>A sorcery whose printed face carries one additional cost, CR 601.2b.</summary>
    private static CardDefinition WithCost(Cost cost) => new(
        "00000000-0000-0000-0000-00000000e9a0",
        "engine test object",
        Layout.Normal,
        [new Face("engine test object", FaceRole.Main, new TypeLine(CardType.Sorcery), AdditionalCosts: [cost])]);

    /// <summary>A stack entry that remembers the card a resolution named, the way a trigger's would.</summary>
    private static StackEntry Remembering(ObjectId what) =>
        new(null, null, [], [], [], null, false)
        {
            Remembered = ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty.SetItem("it", [what]),
        };
}

/// <summary>
/// A casting system that announces nothing and writes down what it was asked to cast, CR 601.2.
/// <see cref="Choices"/> is the half the interpreter's cast-cost question exists for: the real
/// casting system refuses naming CR 601.2c when a cost that consumes objects was announced without
/// them, says through <see cref="CostChoices"/> what those objects could be, and accepts once the
/// command names them. One object per choice, in the order the costs are written.
/// </summary>
internal sealed class InterpreterTestsCasting : ICastingSystem
{
    public string Name => "fake casting";

    /// <summary>What <see cref="Check"/> answers regardless: null means the command is legal.</summary>
    public Refusal? Refusal { get; set; }

    /// <summary>What a cost paid for a rule answers (CR 118.6): null pays it, at one life, as the mark a test reads.</summary>
    public Refusal? PaymentRefusal { get; set; } = new("the fake pays nothing", "118.6");

    public Refusal? CanPayForRule(GameState state, PlayerId player, Cost cost, ObjectId? source = null, int? x = null) => PaymentRefusal;

    public Outcome PayForRule(GameState state, PlayerId player, Cost cost, ObjectId? source = null, int? x = null)
    {
        var paid = new LifeChanged(player, -1, "paid for a rule");
        return new Outcome(paid.Apply(state), [paid]);
    }

    /// <summary>CR 601.2c: what this system says a command still has to name, in cost order.</summary>
    public List<CostChoice> Choices { get; } = [];

    /// <summary>Commands announced through the ordinary door, CR 601 — a player's own cast.</summary>
    public List<CastSpell> Cast { get; } = [];

    /// <summary>Commands announced through CR 608.2g's instructed cast, which is the one the interpreter uses.</summary>
    public List<CastSpell> Instructed { get; } = [];

    Outcome ICastingSystem.Cast(GameState state, CastSpell command)
    {
        Cast.Add(command);
        return Outcome.Unchanged(state);
    }

    Outcome ICastingSystem.CastInstructed(GameState state, CastSpell command)
    {
        Instructed.Add(command);
        return Outcome.Unchanged(state);
    }

    public Outcome Activate(GameState state, ActivateAbility command) => Outcome.Unchanged(state);

    public Outcome PlayLand(GameState state, PlayLand command) => Outcome.Unchanged(state);

    public Outcome ResolveTop(GameState state) => Outcome.Unchanged(state);

    public Refusal? Check(GameState state, Command command) =>
        Refusal ?? (Named(command) < Choices.Count
            ? new Refusal("Which of the permanents pay that spell's costs was not chosen.", "601.2c")
            : null);

    /// <summary>CR 601.2c: the asks this command has not answered yet, in the order the costs are written.</summary>
    public ImmutableList<CostChoice> CostChoices(GameState state, Command command) => [.. Choices.Skip(Named(command))];

    private static int Named(Command command) =>
        command is CastSpell cast ? cast.CostObjects?.Count ?? 0 : 0;
}
