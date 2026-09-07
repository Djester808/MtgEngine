using MtgEngine.Cards;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The interpreter, CR 608.2: what a resolving spell or ability does to the game. Every test
/// builds its own made-up definition, because a card is data and no card name belongs in here.
/// </summary>
public sealed class InterpreterTests
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
    public void An_unsupported_effect_refuses_to_resolve()
    {
        var table = new InterpreterTable();
        var spell = table.OnStack(table.A, new Unsupported("no primitive for this sentence"));

        var refused = Assert.Throws<NotSupportedException>(() => table.Resolve(spell));

        Assert.Contains(nameof(Unsupported), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_effect_with_no_arm_names_the_record_it_could_not_run()
    {
        var table = new InterpreterTable();

        var missing = Assert.Throws<NotImplementedException>(
            () => table.Run(new Investigate(), table.Context()));

        Assert.Contains(nameof(Investigate), missing.Message, StringComparison.Ordinal);
    }
}
