using MtgEngine.Cards;
using MtgEngine.Rules.Casting;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Stack;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using SpellCast = MtgEngine.Rules.Events.SpellCast;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The modes of a modal spell or ability are announced with it, CR 700.2 with 601.2b and 602.2b,
/// and a ceiling that depends on the game as the spell is cast (<see cref="ChooseMode.MaxIf"/>)
/// is read at that announcement and never again.
/// </summary>
public sealed partial class CastingTests
{
    [Fact]
    public void With_the_condition_met_both_modes_are_accepted_at_announcement()
    {
        // CR 700.2 with 601.2b: the ceiling is MaxIfMet while MaxIf holds as the spell is cast,
        // read in the caster's context.
        var (table, query, modal) = ModalTable(holds: true);
        var card = table.Add(TestFaces.Instant("{1}", new SpellAbility(ChooseOneOrBoth())), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));

        Assert.Null(modal.Check(table.State, new CastSpell(CastingTable.A, card, Modes: [0, 1])));
        var cast = modal.Cast(table.State, new CastSpell(CastingTable.A, card, Modes: [0, 1]));

        var entry = cast.State.Objects[Assert.IsType<SpellCast>(cast.Events.Last()).Spell].Stack;
        Assert.NotNull(entry);
        Assert.Equal(new[] { 0, 1 }, entry.Modes);
        Assert.Equal("2", entry.Chosen?[ModeAnnouncement.CeilingKey]);
        Assert.Equal(CastingTable.A, query.AskedFor);
    }

    [Fact]
    public void Without_the_condition_two_modes_are_refused_by_700_2_and_one_is_accepted()
    {
        // CR 700.2: the ceiling is Max when MaxIf does not hold.
        var (table, _, modal) = ModalTable(holds: false);
        var card = table.Add(TestFaces.Instant("{1}", new SpellAbility(ChooseOneOrBoth())), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));

        Assert.Equal("700.2", modal.Check(table.State, new CastSpell(CastingTable.A, card, Modes: [0, 1]))?.Rule);
        Assert.Null(modal.Check(table.State, new CastSpell(CastingTable.A, card, Modes: [1])));

        var cast = modal.Cast(table.State, new CastSpell(CastingTable.A, card, Modes: [1]));
        var entry = cast.State.Objects[Assert.IsType<SpellCast>(cast.Events.Last()).Spell].Stack;
        Assert.NotNull(entry);
        Assert.Equal(new[] { 1 }, entry.Modes);
        Assert.Equal("1", entry.Chosen?[ModeAnnouncement.CeilingKey]);
    }

    [Fact]
    public void The_condition_is_read_once_at_announcement_and_never_at_resolution()
    {
        // CR 601.2b: a commander that leaves before the spell resolves changes nothing — the
        // announced modes and the ceiling read then are what the resolution is handed.
        var (table, query, modal) = ModalTable(holds: true);
        var card = table.Add(TestFaces.Instant("{1}", new SpellAbility(ChooseOneOrBoth())), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));
        table.Apply(modal.Cast(table.State, new CastSpell(CastingTable.A, card, Modes: [0, 1])).Events);
        Assert.Equal(1, query.HoldsReads);

        query.ConditionHolds = false;
        var resolved = modal.ResolveTop(table.State);

        Assert.Equal(1, query.HoldsReads);
        var handed = Assert.Single(interpreter.Entries);
        Assert.NotNull(handed);
        Assert.Equal(new[] { 0, 1 }, handed.Modes);
        Assert.Equal("2", handed.Chosen?[ModeAnnouncement.CeilingKey]);
        Assert.True(StackZone.IsEmpty(resolved.State));
    }

    [Fact]
    public void An_activated_ability_announces_its_modes_under_the_same_ceiling()
    {
        // CR 602.2b with 700.2a: the same step of an activation reads the same condition.
        var (table, query, modal) = ModalTable(holds: false);
        var engine = table.Add(
            TestFaces.Artifact("{2}", new ActivatedAbility([new TapSelf()], ChooseOneOrBoth())),
            CastingTable.A,
            CardZone.Battlefield,
            summoningSick: false);

        Assert.Equal("700.2", modal.Check(table.State, new ActivateAbility(CastingTable.A, engine, 0, Modes: [0, 1]))?.Rule);

        query.ConditionHolds = true;
        var activated = modal.Activate(table.State, new ActivateAbility(CastingTable.A, engine, 0, Modes: [0, 1]));

        var top = StackZone.Top(activated.State);
        Assert.NotNull(top);
        var entry = activated.State.Objects[top.Value].Stack;
        Assert.NotNull(entry);
        Assert.Equal(new[] { 0, 1 }, entry.Modes);
        Assert.Equal("2", entry.Chosen?[ModeAnnouncement.CeilingKey]);
    }

    [Fact]
    public void A_modal_spell_announced_without_its_modes_still_records_the_ceiling_for_the_question()
    {
        // CR 601.2b: the condition is read at the announcement whether or not the command named
        // its modes, so the question the resolution asks offers the number allowed then.
        var (table, query, modal) = ModalTable(holds: true);
        var card = table.Add(TestFaces.Instant("{1}", new SpellAbility(ChooseOneOrBoth())), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));

        var cast = modal.Cast(table.State, new CastSpell(CastingTable.A, card));

        var entry = cast.State.Objects[Assert.IsType<SpellCast>(cast.Events.Last()).Spell].Stack;
        Assert.NotNull(entry);
        Assert.Empty(entry.Modes);
        Assert.Equal("2", entry.Chosen?[ModeAnnouncement.CeilingKey]);
        Assert.Equal(1, query.HoldsReads);
    }

    [Fact]
    public void A_fixed_ceiling_holds_the_count_and_the_indices_and_leaves_the_entry_as_it_was()
    {
        // CR 700.2 for the bounds and the indices, CR 700.2d for the same mode twice; a ceiling
        // that is not conditional reads no condition and records nothing on the entry.
        var (table, query, modal) = ModalTable(holds: true);
        var one = table.Add(TestFaces.Instant("{1}", new SpellAbility(TwoModes())), CastingTable.A, CardZone.Hand);
        var two = table.Add(TestFaces.Instant("{1}", new SpellAbility(TwoModes(min: 2, max: 2))), CastingTable.A, CardZone.Hand);
        var twice = table.Add(TestFaces.Instant("{1}", new SpellAbility(TwoModes(max: 2, sameModeTwice: true))), CastingTable.A, CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));

        Assert.Equal("700.2", modal.Check(table.State, new CastSpell(CastingTable.A, one, Modes: [0, 1]))?.Rule);
        Assert.Equal("700.2", modal.Check(table.State, new CastSpell(CastingTable.A, one, Modes: [2]))?.Rule);
        Assert.Equal("700.2", modal.Check(table.State, new CastSpell(CastingTable.A, two, Modes: [0]))?.Rule);
        Assert.Equal("700.2d", modal.Check(table.State, new CastSpell(CastingTable.A, two, Modes: [0, 0]))?.Rule);
        Assert.Null(modal.Check(table.State, new CastSpell(CastingTable.A, twice, Modes: [0, 0])));
        Assert.Equal(0, query.HoldsReads);

        var cast = modal.Cast(table.State, new CastSpell(CastingTable.A, one, Modes: [0]));
        var entry = cast.State.Objects[Assert.IsType<SpellCast>(cast.Events.Last()).Spell].Stack;
        Assert.NotNull(entry);
        Assert.Null(entry.Chosen);
    }

    [Fact]
    public void A_modal_among_a_sequences_steps_is_held_to_its_ceiling()
    {
        // CR 700.2: the bulleted list may be one step of the spell's text.
        var (table, _, modal) = ModalTable(holds: false);
        var stepped = table.Add(
            TestFaces.Instant("{1}", new SpellAbility(new Sequence([new Draw(new Fixed(1)), ChooseOneOrBoth()]))),
            CastingTable.A,
            CardZone.Hand);
        table.AddMana(CastingTable.A, new ManaPool(0, 0, 0, 0, 0, 1));

        Assert.Equal("700.2", modal.Check(table.State, new CastSpell(CastingTable.A, stepped, Modes: [0, 1]))?.Rule);
        Assert.Null(modal.Check(table.State, new CastSpell(CastingTable.A, stepped, Modes: [0])));
    }

    /// <summary>A table whose query answers the modal condition as told and counts how often it was asked.</summary>
    private (CastingTable Table, ModalQuery Query, CastingSystem Casting) ModalTable(bool holds)
    {
        var query = new ModalQuery { ConditionHolds = holds };
        return (new CastingTable(), query, new CastingSystem(new CastingFakeZones(), query, new PrintedCharacteristics(), interpreter));
    }

    /// <summary>
    /// Choose one, or both while the caster controls a commander: CR 700.2 with 601.2b, spelled
    /// as the vocabulary says — Max 1 with MaxIf a Controls condition and MaxIfMet 2.
    /// </summary>
    private static ChooseMode ChooseOneOrBoth() => TwoModes() with
    {
        MaxIf = new Controls(new You(), new ObjectFilter(IsCommander: true), new Comparison(ComparisonOp.Ge, new Fixed(1))),
        MaxIfMet = 2,
    };

    private static ChooseMode TwoModes(int min = 1, int max = 1, bool sameModeTwice = false) => new(
        [new Mode("Draw a card.", new Draw(new Fixed(1))), new Mode("Draw two cards.", new Draw(new Fixed(2)))],
        min,
        max,
        sameModeTwice);

    /// <summary>Answers the modal condition as told and counts each read, so a test can see when the casting system asked.</summary>
    private sealed class ModalQuery : IQuery
    {
        public string Name => "modal query";

        public bool ConditionHolds { get; set; }

        public int HoldsReads { get; private set; }

        public PlayerId? AskedFor { get; private set; }

        public int Evaluate(GameState state, Amount amount, EffectContext context) =>
            amount is Fixed number ? number.N : throw new NotSupportedException();

        public bool Matches(GameState state, ObjectFilter filter, ObjectId candidate, EffectContext context) => throw new NotSupportedException();

        public bool Holds(GameState state, Condition condition, EffectContext context)
        {
            HoldsReads++;
            AskedFor = context.Controller;
            return ConditionHolds;
        }

        public ImmutableList<ObjectId> Resolve(GameState state, Selector selector, EffectContext context) => throw new NotSupportedException();

        public ImmutableList<PlayerId> Players(GameState state, PlayerRef player, EffectContext context) => [context.Controller];
    }
}
