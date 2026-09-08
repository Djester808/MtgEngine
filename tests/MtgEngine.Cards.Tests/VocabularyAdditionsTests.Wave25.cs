namespace MtgEngine.Cards.Tests;

/// <summary>
/// Wave 25 (seam-requests/V14.md): the nine shapes two or three independent translators each hit on
/// a real card of slices 004 and 005. A key on what a destruction actually destroyed (CR 701.8), the
/// life half of a life trigger (CR 603.10), the rulebook's "modified" (CR 700.9), an attack tax
/// (CR 508.1h), a timing permission granted to a player (CR 702.8), the defending player a token
/// enters attacking (CR 508.4), "an opponent" as some opponent rather than every one (CR 102.2), the
/// Heal that Scryfall reads out of regenerate's reminder text (CR 701.19a), and a main phase that
/// names neither of the two (CR 505.1). Each has to round-trip, lint where its halves can
/// contradict, and leave the IR version where it is.
/// </summary>
public sealed partial class VocabularyAdditionsTests
{
    private static CardDefinition Wave25DefinitionWith(params Ability[] abilities) => new(
        OracleId: "00000000-0000-0000-0000-000000000f25",
        Name: "Wave Twenty Five Addition",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Wave Twenty Five Addition",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Enchantment),
                ManaCost: "{2}{W}",
                Abilities: abilities,
                OracleText: "Wave twenty five text."),
        ]);

    private static CardDefinition Wave25RoundTrip(CardDefinition definition) =>
        CardFileSerializer.ReadDefinition(CardFileSerializer.WriteDefinition(definition));

    private static CardFile Wave25File(IReadOnlyList<string>? keywords, params Ability[] abilities) => new(
        OracleId: Wave25DefinitionWith(abilities).OracleId,
        Name: "Wave Twenty Five Addition",
        OracleHash: OracleHashing.Of(["Wave twenty five text."]),
        IrVersion: IrVersion.Current,
        Definition: Wave25DefinitionWith(abilities),
        Status: ImplementationStatus.Translated,
        Source: new TemplateProvenance("test"),
        ScryfallKeywords: keywords);

    private static IReadOnlyList<LintFinding> Wave25Errors(params Ability[] abilities) =>
        Linter.Check(Wave25File(null, abilities)).Findings.Where(f => f.Severity == LintSeverity.Error).ToList();

    [Fact]
    public void A_destruction_remembers_what_it_actually_destroyed()
    {
        // CR 701.8: "destroy all creatures. For each creature destroyed this way, you gain 1 life" —
        // the key holds what died, not what was chosen, so a regenerated creature is not in it.
        var ability = new SpellAbility(new Sequence(
        [
            new Destroy(new Target(ObjectFilter.Creature), RememberAs: "destroyed"),
            new GainLife(new CountOf(new ObjectFilter(Also: new IsRemembered("destroyed")))),
        ]));
        var back = Wave25RoundTrip(Wave25DefinitionWith(ability));

        var steps = Assert.IsType<Sequence>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do).Steps;
        Assert.Equal("destroyed", Assert.IsType<Destroy>(steps[0]).RememberAs);
        Assert.Empty(Wave25Errors(ability));
    }

    [Fact]
    public void A_destruction_key_nothing_reads_is_refused()
    {
        // L003: a key nothing on the face reads back is a dropped sentence, the same as every other
        // RememberAs the vocabulary carries.
        var ability = new SpellAbility(new GainLife(new Stored("nobody-reads-this")));

        Assert.Contains(Wave25Errors(ability), f => f.Code == "L003");
    }

    [Fact]
    public void A_life_trigger_reads_the_life_of_its_own_event()
    {
        // CR 603.10: "whenever you gain life, each opponent loses that much life" and its mirror.
        var gained = new TriggeredAbility(new GainsLife(new You()), new LoseLife(new TriggeringLifeGained(), new EachOpponent()));
        var lost = new TriggeredAbility(new LosesLife(new EachOpponent()), new GainLife(new TriggeringLifeLost()));

        var backGained = Wave25RoundTrip(Wave25DefinitionWith(gained));
        Assert.IsType<TriggeringLifeGained>(Assert.IsType<LoseLife>(Assert.IsType<TriggeredAbility>(backGained.Faces[0].Abilities![0]).Do).N);

        var backLost = Wave25RoundTrip(Wave25DefinitionWith(lost));
        Assert.IsType<TriggeringLifeLost>(Assert.IsType<GainLife>(Assert.IsType<TriggeredAbility>(backLost.Faces[0].Abilities![0]).Do).N);

        Assert.Empty(Wave25Errors(gained));
        Assert.Empty(Wave25Errors(lost));
    }

    [Fact]
    public void A_life_amount_read_from_the_wrong_event_is_refused()
    {
        // L039: the two events are different, and an amount written for the other one reads nothing.
        var crossed = new TriggeredAbility(new GainsLife(new You()), new GainLife(new TriggeringLifeLost()));
        var untriggered = new SpellAbility(new GainLife(new TriggeringLifeGained()));

        Assert.Contains(Wave25Errors(crossed), f => f.Code == "L039");
        Assert.Contains(Wave25Errors(untriggered), f => f.Code == "L039");
    }

    [Fact]
    public void A_delayed_life_trigger_can_read_the_life_it_watches()
    {
        // The delayed trigger is the ability's own event as much as a printed trigger is (L039
        // reads it the way L028 reads a delayed damage trigger).
        var ability = new SpellAbility(new DelayedTrigger(new GainsLife(new You()), new Draw(new TriggeringLifeGained())));

        Assert.Empty(Wave25Errors(ability));
    }

    [Fact]
    public void Modified_is_one_predicate_and_not_a_counter_reading()
    {
        // CR 700.9: a counter of any kind, equipped, or enchanted by an Aura its controller controls.
        var ability = new StaticAbility(
            new ModifyPt(new Fixed(1), new Fixed(1)),
            To: new Target(new ObjectFilter(Types: CardType.Creature, Also: new IsModified())));
        var back = Wave25RoundTrip(Wave25DefinitionWith(ability));

        var filter = Assert.IsType<Target>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).To).Filter;
        Assert.IsType<IsModified>(filter.Also);
        Assert.Empty(Wave25Errors(ability));
    }

    [Fact]
    public void An_attack_tax_is_a_cost_on_the_restriction_and_scales_inside_it()
    {
        // CR 508.1h: "creatures can't attack you unless their controller pays {2} for each creature
        // they control that's attacking you" — the scaling lives in the cost, not in a new field.
        var tax = new CantAttack(new You(), UnlessPays: new PayManaCost(new Times(new AttackersAgainst(new You()), 2)));
        var ability = new StaticAbility(tax);
        var back = Wave25RoundTrip(Wave25DefinitionWith(ability));

        var read = Assert.IsType<CantAttack>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).Effect);
        var cost = Assert.IsType<PayManaCost>(read.UnlessPays);
        Assert.Equal(2, Assert.IsType<Times>(cost.Generic).Factor);
        Assert.Empty(Wave25Errors(ability));
    }

    [Fact]
    public void Two_exceptions_on_one_attack_restriction_are_refused()
    {
        // L040: a card prints the board exception or the tax, never both.
        var ability = new StaticAbility(new CantAttack(new You(), Unless: ObjectFilter.Creature, UnlessPays: new PayManaCost(new Fixed(2))));

        Assert.Contains(Wave25Errors(ability), f => f.Code == "L040");
    }

    [Fact]
    public void A_player_can_be_given_flash_for_a_kind_of_spell()
    {
        // CR 702.8: "you may cast green creature spells as though they had flash" — a timing
        // permission granted to a player, not a keyword granted to a card.
        var ability = new StaticAbility(new MayCastAsThoughFlash(new You(), new ObjectFilter(Types: CardType.Creature, Colors: Color.Green)));
        var back = Wave25RoundTrip(Wave25DefinitionWith(ability));

        var read = Assert.IsType<MayCastAsThoughFlash>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).Effect);
        Assert.IsType<You>(read.Who);
        Assert.Equal(Color.Green, read.What!.Colors);
        Assert.Empty(Wave25Errors(ability));
    }

    [Fact]
    public void Flash_over_a_land_only_filter_is_refused()
    {
        // L042, CR 305.1: a land is played and never cast, so the permission grants nothing.
        var ability = new StaticAbility(new MayCastAsThoughFlash(new You(), new ObjectFilter(Types: CardType.Land)));

        Assert.Contains(Wave25Errors(ability), f => f.Code == "L042");
    }

    [Fact]
    public void A_token_can_enter_attacking_a_named_player()
    {
        // CR 508.4: "create a token attacking that player or a planeswalker they control" — the
        // player carries the whole phrase, and the planeswalker is that player's side of the board.
        var token = new CreateToken(new TokenSpec("Soldier", Color.White, CardType.Creature), Attacking: true, AttackingWhom: new TargetPlayer(PlayerFilter.Opponents, Key: "attacked"));
        var ability = new SpellAbility(token);
        var back = Wave25RoundTrip(Wave25DefinitionWith(ability));

        var read = Assert.IsType<CreateToken>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do);
        Assert.True(read.Attacking);
        Assert.IsType<TargetPlayer>(read.AttackingWhom);
        Assert.Empty(Wave25Errors(ability));
    }

    [Fact]
    public void A_token_that_is_not_attacking_names_no_defending_player()
    {
        // L040: only a token put onto the battlefield attacking chooses one (CR 508.4).
        var ability = new SpellAbility(new CreateToken(new TokenSpec("Soldier", Color.White, CardType.Creature), AttackingWhom: new TargetPlayer(PlayerFilter.Opponents, Key: "attacked")));

        Assert.Contains(Wave25Errors(ability), f => f.Code == "L040");
    }

    [Fact]
    public void An_opponent_is_read_by_a_condition_and_acted_on_by_nothing()
    {
        // CR 102.2: "if an opponent controls three or more creatures" is some opponent, at least
        // one — EachOpponent there reads "every opponent", a different card in a multiplayer game.
        var ability = new TriggeredAbility(
            new AtStep(Step.End, new You()),
            new Draw(new Fixed(1)),
            If: new Controls(new AnOpponent(), ObjectFilter.Creature, new Comparison(ComparisonOp.Ge, new Fixed(3))));
        var back = Wave25RoundTrip(Wave25DefinitionWith(ability));

        var read = Assert.IsType<Controls>(Assert.IsType<TriggeredAbility>(back.Faces[0].Abilities![0]).If);
        Assert.IsType<AnOpponent>(read.Who);
        Assert.Empty(Wave25Errors(ability));

        // L041: nothing can be done to "an opponent"; a card that acts on one names them.
        Assert.Contains(Wave25Errors(new SpellAbility(new Draw(new Fixed(1), new AnOpponent()))), f => f.Code == "L041");
    }

    [Fact]
    public void A_regenerator_is_not_asked_for_the_heal_scryfall_reads_off_its_reminder_text()
    {
        // CR 701.19a: regenerating removes all damage marked on the permanent, so Scryfall lists
        // Heal beside Regenerate. The card prints one keyword action, and L002 must ask for one.
        var regenerates = new SpellAbility(new Regenerate(new Target(ObjectFilter.Creature)));
        Assert.DoesNotContain(
            Linter.Check(Wave25File(["Regenerate", "Heal"], regenerates)).Findings,
            f => f.Code == "L002" && f.Severity == LintSeverity.Error);

        // The check still fires for a card that says Heal and does not regenerate.
        Assert.Contains(
            Linter.Check(Wave25File(["Heal"], new SpellAbility(new Draw(new Fixed(1))))).Findings,
            f => f.Code == "L002" && f.Severity == LintSeverity.Error);
    }

    [Fact]
    public void A_trigger_can_wait_for_the_next_main_phase_without_naming_which()
    {
        // CR 505.1: "at the beginning of your next main phase" names neither main phase, and a
        // delayed trigger made during combat would never see a precombat main again this turn.
        var ability = new SpellAbility(new DelayedTrigger(new AtStep(Step.Main, new You()), new Draw(new Fixed(1))));
        var back = Wave25RoundTrip(Wave25DefinitionWith(ability));

        var delayed = Assert.IsType<DelayedTrigger>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do);
        Assert.Equal(Step.Main, Assert.IsType<AtStep>(delayed.When).Step);
        Assert.Empty(Wave25Errors(ability));
    }

    [Fact]
    public void The_wave_twenty_five_additions_leave_the_ir_version_alone()
    {
        // Every addition is a new record under an existing base, an optional field appended to an
        // existing record, or an enum member appended last: no stored file's shape changed.
        Assert.Equal(1, IrVersion.Current);
    }
}
