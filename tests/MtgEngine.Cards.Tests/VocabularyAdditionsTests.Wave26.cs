namespace MtgEngine.Cards.Tests;

/// <summary>
/// Wave 26 (seam-requests/V15.md): the vocabulary slice 006 could not say. A key produced by a cost
/// on the face rather than by an ability (CR 118.8 with L003), a trigger on damage a prevention
/// prevented and the amount it prevented (CR 615.13), damage that can't be prevented at all
/// (CR 615.12), a top card revealed to everybody (CR 401.5), the draw step's own draw spared by a
/// replacement (CR 121.1), the triggering spell's payment (CR 400.7d), the difference between
/// distinct targets and different mana values (CR 601.2c), the bottom of a library in a random order
/// (CR 401.4), "any number" of counters moved (CR 122.5), a countered spell put on top of a library
/// (CR 701.6a) and a player's second spell each turn (CR 603.2). Each round-trips, lints where its
/// halves can contradict, and leaves the IR version where it is.
/// </summary>
public sealed partial class VocabularyAdditionsTests
{
    private static CardDefinition Wave26DefinitionWith(params Ability[] abilities) => new(
        OracleId: "00000000-0000-0000-0000-000000000f26",
        Name: "Wave Twenty Six Addition",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Wave Twenty Six Addition",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Enchantment),
                ManaCost: "{2}{B}",
                Abilities: abilities,
                OracleText: "Wave twenty six text."),
        ]);

    private static CardDefinition Wave26DefinitionWithCosts(IReadOnlyList<Cost> costs, params Ability[] abilities) => new(
        OracleId: "00000000-0000-0000-0000-000000000f26",
        Name: "Wave Twenty Six Addition",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Wave Twenty Six Addition",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Sorcery),
                ManaCost: "{1}{B}",
                Abilities: abilities,
                AdditionalCosts: costs,
                OracleText: "Wave twenty six text."),
        ]);

    private static CardDefinition Wave26RoundTrip(CardDefinition definition) =>
        CardFileSerializer.ReadDefinition(CardFileSerializer.WriteDefinition(definition));

    private static CardFile Wave26File(CardDefinition definition) => new(
        OracleId: definition.OracleId,
        Name: definition.Name,
        OracleHash: OracleHashing.Of(["Wave twenty six text."]),
        IrVersion: IrVersion.Current,
        Definition: definition,
        Status: ImplementationStatus.Translated,
        Source: new TemplateProvenance("test"));

    private static IReadOnlyList<LintFinding> Wave26Errors(params Ability[] abilities) =>
        Linter.Check(Wave26File(Wave26DefinitionWith(abilities))).Findings.Where(f => f.Severity == LintSeverity.Error).ToList();

    private static IReadOnlyList<LintFinding> Wave26ErrorsWithCosts(IReadOnlyList<Cost> costs, params Ability[] abilities) =>
        Linter.Check(Wave26File(Wave26DefinitionWithCosts(costs, abilities))).Findings.Where(f => f.Severity == LintSeverity.Error).ToList();

    [Fact]
    public void A_key_a_face_cost_produces_is_read_by_an_ability()
    {
        // CR 118.8: "as an additional cost to cast this spell, sacrifice a creature. [This] deals
        // damage equal to the sacrificed creature's power." The producer is on the face, not on any
        // ability, so a walk over the abilities alone calls this correct card L003.
        var ability = new SpellAbility(new DealDamage(
            new PowerOf(new Remembered("sacrificed")),
            To: new AnyTarget()));
        var costs = new Cost[] { new SacrificeCost(ObjectFilter.Creature, RememberAs: "sacrificed") };

        Assert.Empty(Wave26ErrorsWithCosts(costs, ability));

        // With nothing producing it, it is still L003.
        Assert.Contains(Wave26Errors(ability), f => f.Code == "L003");
    }

    [Fact]
    public void A_prevention_trigger_reads_the_damage_it_prevented()
    {
        // CR 615.13 with 603.10: "when damage is prevented this way, this creature deals that much
        // damage to any other target".
        var ability = new TriggeredAbility(
            new DamagePrevented(To: ObjectFilter.Creature, ThisWay: true),
            new DealDamage(new TriggeringPreventedDamage(), To: new AnyTarget(Other: true), Source: new Self()));
        var back = Wave26RoundTrip(Wave26DefinitionWith(ability));

        var triggered = Assert.IsType<TriggeredAbility>(back.Faces[0].Abilities![0]);
        Assert.True(Assert.IsType<DamagePrevented>(triggered.When).ThisWay);
        var damage = Assert.IsType<DealDamage>(triggered.Do);
        Assert.IsType<TriggeringPreventedDamage>(damage.N);
        Assert.True(Assert.IsType<AnyTarget>(damage.To).Other);
        Assert.Empty(Wave26Errors(ability));
    }

    [Fact]
    public void The_prevented_amount_needs_a_prevention_to_read()
    {
        // The damage that was dealt and the damage that was prevented are two numbers on one event.
        var wrongEvent = new TriggeredAbility(
            new IsDealtDamage(ObjectFilter.Creature),
            new DealDamage(new TriggeringPreventedDamage(), To: new AnyTarget(), Source: new Self()));

        Assert.Contains(Wave26Errors(wrongEvent), f => f.Code == "L043");
    }

    [Fact]
    public void Combat_damage_that_cant_be_prevented_is_its_own_continuous_effect()
    {
        // CR 615.12.
        var ability = new StaticAbility(new DamageCantBePrevented(Combat: true));
        var back = Wave26RoundTrip(Wave26DefinitionWith(ability));

        var effect = Assert.IsType<DamageCantBePrevented>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).Effect);
        Assert.True(effect.Combat);
        Assert.Null(effect.From);
        Assert.Empty(Wave26Errors(ability));

        // The board-wide effect and DealDamage's per-instruction rider are two different lines.
        var contradiction = new StaticAbility(
            new DamageCantBePrevented(Combat: true),
            While: new Compare(new Fixed(1), ComparisonOp.Eq, new Fixed(1)));
        Assert.Empty(Wave26Errors(contradiction));
    }

    [Fact]
    public void The_top_card_revealed_is_not_the_top_card_looked_at()
    {
        // CR 401.5: revealed is public; MayLookAtTopCard is one player's private information.
        var ability = new StaticAbility(new PlayWithTopCardRevealed(new You()));
        var back = Wave26RoundTrip(Wave26DefinitionWith(ability));

        var effect = Assert.IsType<PlayWithTopCardRevealed>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).Effect);
        Assert.IsType<You>(effect.Who);
        Assert.Empty(Wave26Errors(ability));
    }

    [Fact]
    public void A_draw_replacement_can_spare_the_draw_steps_own_draw()
    {
        // CR 121.1: the field mirrors the Draws trigger's, and the two draw clauses contradict.
        var ability = new ReplacementAbility(
            new WouldDraw(new You(), ExceptFirstInDrawStep: true),
            new Sequence([new Draw(new Fixed(2))]));
        var back = Wave26RoundTrip(Wave26DefinitionWith(ability));

        var replacement = Assert.IsType<ReplacementAbility>(back.Faces[0].Abilities![0]);
        Assert.True(Assert.IsType<WouldDraw>(replacement.Instead).ExceptFirstInDrawStep);
        Assert.Empty(Wave26Errors(ability));

        var both = new ReplacementAbility(
            new WouldDraw(new You(), FirstEachTurn: true, ExceptFirstInDrawStep: true),
            new Sequence([new Draw(new Fixed(2))]));
        Assert.Contains(Wave26Errors(both), f => f.Code == "L043");
    }

    [Fact]
    public void The_triggering_spells_payment_is_read_off_the_cast()
    {
        // CR 400.7d: "whenever a player casts a spell, if no mana was spent to cast it, …"
        var ability = new TriggeredAbility(
            new SpellCast(new ObjectFilter(Spell: true), new EachPlayer()),
            new Draw(new Fixed(1)),
            If: new Compare(new ManaSpentOnTriggering(), ComparisonOp.Eq, new Fixed(0)));
        var back = Wave26RoundTrip(Wave26DefinitionWith(ability));

        var triggered = Assert.IsType<TriggeredAbility>(back.Faces[0].Abilities![0]);
        Assert.IsType<ManaSpentOnTriggering>(Assert.IsType<Compare>(triggered.If).Left);
        Assert.Empty(Wave26Errors(ability));

        // Nothing here triggers on a spell being cast, so there is no payment to read.
        var noCast = new TriggeredAbility(
            new EntersBattlefield(ObjectFilter.Creature),
            new Draw(new Fixed(1)),
            If: new Compare(new ManaSpentOnTriggering(), ComparisonOp.Eq, new Fixed(0)));
        Assert.Contains(Wave26Errors(noCast), f => f.Code == "L043");
    }

    [Fact]
    public void Distinct_targets_are_not_different_mana_values()
    {
        // CR 601.2c: "distinct" is the default and says only that two targets are two objects.
        Assert.True(new Target(ObjectFilter.Creature).Distinct);

        var ability = new SpellAbility(new Exile(new Target(
            new ObjectFilter(Types: CardType.Creature, In: Zone.Graveyard),
            Count: new Fixed(3))));
        Assert.Empty(Wave26Errors(ability));
    }

    [Fact]
    public void The_bottom_of_a_library_in_a_random_order_is_one_placement()
    {
        // CR 401.4: one value, because a placement is one value.
        var ability = new SpellAbility(new Reorder(new Remembered("rest"), Placement.BottomRandomOrder));
        var costs = new Cost[] { new ExileFromZoneCost(Zone.Library, Count: new Fixed(3), RememberAs: "rest") };

        Assert.Empty(Wave26ErrorsWithCosts(costs, ability));
        var back = Wave26RoundTrip(Wave26DefinitionWith(ability));
        Assert.Equal(Placement.BottomRandomOrder, Assert.IsType<Reorder>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do).Where);
    }

    [Fact]
    public void Any_number_of_counters_moved_is_not_all_of_them()
    {
        // CR 122.5: a printed count, "all", and a free choice are three different lines.
        var ability = new SpellAbility(new MoveCounters(
            new Target(ObjectFilter.Creature, Key: "from"),
            new Target(ObjectFilter.Creature, Key: "to"),
            "+1/+1",
            AnyNumber: true));
        var back = Wave26RoundTrip(Wave26DefinitionWith(ability));

        var move = Assert.IsType<MoveCounters>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do);
        Assert.True(move.AnyNumber);
        Assert.False(move.All);
        Assert.Null(move.N);
        Assert.Empty(Wave26Errors(ability));

        var two = new SpellAbility(new MoveCounters(
            new Target(ObjectFilter.Creature, Key: "from"),
            new Target(ObjectFilter.Creature, Key: "to"),
            "+1/+1",
            All: true,
            AnyNumber: true));
        Assert.Contains(Wave26Errors(two), f => f.Code == "L044");
    }

    [Fact]
    public void A_countered_spell_can_be_put_on_top_of_a_library()
    {
        // CR 701.6a: one Counter, with the zone and where in it.
        var ability = new SpellAbility(new Counter(
            new Target(new ObjectFilter(Spell: true)),
            PutInto: Zone.Library,
            Where: Placement.Top));
        var back = Wave26RoundTrip(Wave26DefinitionWith(ability));

        var counter = Assert.IsType<Counter>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do);
        Assert.Equal(Zone.Library, counter.PutInto);
        Assert.Equal(Placement.Top, counter.Where);
        Assert.Empty(Wave26Errors(ability));

        var ordered = new SpellAbility(new Counter(new Target(new ObjectFilter(Spell: true)), Where: Placement.Top));
        Assert.Contains(Wave26Errors(ordered), f => f.Code == "L043");
    }

    [Fact]
    public void A_players_second_spell_each_turn_is_a_count_on_the_trigger()
    {
        // CR 603.2: that cast alone, not every cast after it.
        var ability = new TriggeredAbility(
            new SpellCast(new ObjectFilter(Spell: true), new EachPlayer(), Nth: 2),
            new Draw(new Fixed(1)));
        var back = Wave26RoundTrip(Wave26DefinitionWith(ability));

        Assert.Equal(2, Assert.IsType<SpellCast>(Assert.IsType<TriggeredAbility>(back.Faces[0].Abilities![0]).When).Nth);
        Assert.Empty(Wave26Errors(ability));

        var zeroth = new TriggeredAbility(
            new SpellCast(new ObjectFilter(Spell: true), new EachPlayer(), Nth: 0),
            new Draw(new Fixed(1)));
        Assert.Contains(Wave26Errors(zeroth), f => f.Code == "L044");
    }

    [Fact]
    public void The_wave_twenty_six_additions_do_not_move_the_ir_version()
    {
        // Every one is a new kind under an existing base, an optional field appended to an existing
        // record, or an enum member appended last: no stored file's shape changed.
        Assert.Equal(1, IrVersion.Current);
    }
}
