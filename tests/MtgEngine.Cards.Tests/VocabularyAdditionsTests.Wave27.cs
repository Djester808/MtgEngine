namespace MtgEngine.Cards.Tests;

/// <summary>
/// Wave 27 (seam-requests/V16.md): ten gaps the corpus could not say. A keyword a filter forbids
/// (CR 702.1), the greatest damage dealt to any one opponent this turn (CR 102.2), the creatures
/// that died this turn (CR 700.4), the damage a running prevention prevented (CR 615), an attack tax
/// paid once per attacker (CR 508.1h), a spree mode whose cost is really paid (CR 702.172), the
/// preparation layout and its inset frame (CR 722.1, 722.2a), a copy token that needs no name
/// (CR 707.2), the largest group of permanents sharing a name (CR 201.2) and the rider that watches
/// a permanent being tapped for mana (CR 605.1a). Each round-trips, lints where its halves can
/// contradict, and leaves the IR version where it is.
/// </summary>
public sealed partial class VocabularyAdditionsTests
{
    private static CardDefinition Wave27DefinitionWith(params Ability[] abilities) => new(
        OracleId: "00000000-0000-0000-0000-000000000f27",
        Name: "Wave Twenty Seven Addition",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Wave Twenty Seven Addition",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Enchantment),
                ManaCost: "{2}{W}",
                Abilities: abilities,
                OracleText: "Wave twenty seven text."),
        ]);

    private static CardDefinition Wave27RoundTrip(CardDefinition definition) =>
        CardFileSerializer.ReadDefinition(CardFileSerializer.WriteDefinition(definition));

    private static CardFile Wave27File(CardDefinition definition) => new(
        OracleId: definition.OracleId,
        Name: definition.Name,
        OracleHash: OracleHashing.Of(["Wave twenty seven text."]),
        IrVersion: IrVersion.Current,
        Definition: definition,
        Status: ImplementationStatus.Translated,
        Source: new TemplateProvenance("test"));

    private static IReadOnlyList<LintFinding> Wave27Errors(params Ability[] abilities) =>
        Linter.Check(Wave27File(Wave27DefinitionWith(abilities))).Findings.Where(f => f.Severity == LintSeverity.Error).ToList();

    private static IReadOnlyList<LintFinding> Wave27ErrorsFor(CardDefinition definition) =>
        Linter.Check(Wave27File(definition)).Findings.Where(f => f.Severity == LintSeverity.Error).ToList();

    [Fact]
    public void A_filter_says_which_keyword_its_objects_do_not_have()
    {
        // CR 702.1 with 702.9: "creatures without flying".
        var ability = new SpellAbility(new DealDamage(
            new Fixed(2),
            To: new Target(new ObjectFilter(Types: CardType.Creature, NotHas: KeywordKind.Flying))));
        var back = Wave27RoundTrip(Wave27DefinitionWith(ability));

        var target = Assert.IsType<Target>(Assert.IsType<DealDamage>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do).To);
        Assert.Equal(KeywordKind.Flying, target.Filter.NotHas);
        Assert.Null(target.Filter.Has);
        Assert.Empty(Wave27Errors(ability));

        // Demanded and forbidden at once admits nothing.
        var both = new SpellAbility(new DealDamage(
            new Fixed(2),
            To: new Target(new ObjectFilter(Has: KeywordKind.Flying, NotHas: KeywordKind.Flying))));
        Assert.Contains(Wave27Errors(both), f => f.Code == "L045");
    }

    [Fact]
    public void The_greatest_damage_dealt_to_one_opponent_this_turn_is_an_amount()
    {
        // CR 102.2: "an opponent was dealt 7 or more damage this turn" is the max over the
        // opponents, not a total and not DamageDealtThisTurn aimed at AnOpponent (L041).
        var ability = new SpellAbility(new Draw(new GreatestDamageDealtThisTurn(PlayerFilter.Opponents)));
        var back = Wave27RoundTrip(Wave27DefinitionWith(ability));

        var amount = Assert.IsType<GreatestDamageDealtThisTurn>(Assert.IsType<Draw>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do).N);
        Assert.True(amount.To.Opponent);
        Assert.Empty(Wave27Errors(ability));

        // The old spelling has no number to read.
        Assert.Contains(Wave27Errors(new SpellAbility(new Draw(new DamageDealtThisTurn(To: new AnOpponent())))), f => f.Code == "L041");

        // Nobody is both you and your opponent.
        var nobody = new SpellAbility(new Draw(new GreatestDamageDealtThisTurn(new PlayerFilter(Opponent: true, IsYou: true))));
        Assert.Contains(Wave27Errors(nobody), f => f.Code == "L045");
    }

    [Fact]
    public void The_creatures_that_died_this_turn_are_an_amount()
    {
        // CR 700.4: died is the move onto a graveyard from the battlefield.
        var ability = new SpellAbility(new Draw(new CreaturesDiedThisTurn(new ObjectFilter(ControlledBy: new You()))));
        var back = Wave27RoundTrip(Wave27DefinitionWith(ability));

        var amount = Assert.IsType<CreaturesDiedThisTurn>(Assert.IsType<Draw>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do).N);
        Assert.IsType<You>(amount.Matching!.ControlledBy);
        Assert.Empty(Wave27Errors(ability));

        // A zone asks where the dead creature is now, which is not what the count reads.
        var zoned = new SpellAbility(new Draw(new CreaturesDiedThisTurn(new ObjectFilter(In: Zone.Graveyard))));
        Assert.Contains(Wave27Errors(zoned), f => f.Code == "L045");
    }

    [Fact]
    public void A_replacement_reads_the_damage_it_prevented()
    {
        // CR 615: "prevent the next 3 damage … for each 1 damage prevented this way, you gain 1 life".
        var ability = new ReplacementAbility(
            new WouldBeDealtDamage(To: ObjectFilter.Creature),
            new Sequence(
            [
                new Apply(new PreventDamage(new Fixed(3)), new Self(), new UntilEndOfTurn()),
                new GainLife(new PreventedDamage()),
            ]));
        var back = Wave27RoundTrip(Wave27DefinitionWith(ability));

        var steps = Assert.IsType<Sequence>(Assert.IsType<ReplacementAbility>(back.Faces[0].Abilities![0]).With).Steps;
        Assert.IsType<PreventedDamage>(Assert.IsType<GainLife>(steps[1]).N);
        Assert.Empty(Wave27Errors(ability));

        // With no prevention in the ability there is no "this way" to read.
        var noPrevention = new SpellAbility(new GainLife(new PreventedDamage()));
        Assert.Contains(Wave27Errors(noPrevention), f => f.Code == "L045");
    }

    [Fact]
    public void An_attack_tax_can_be_paid_once_for_every_attacker()
    {
        // CR 508.1h: a Phyrexian symbol per attacker cannot be written as a scaled generic.
        var ability = new StaticAbility(new CantAttack(
            Player: new You(),
            UnlessPays: new PayManaCost(new Fixed(0), "{W/P}"),
            PerAttackingCreature: true));
        var back = Wave27RoundTrip(Wave27DefinitionWith(ability));

        var tax = Assert.IsType<CantAttack>(Assert.IsType<StaticAbility>(back.Faces[0].Abilities![0]).Effect);
        Assert.True(tax.PerAttackingCreature);
        Assert.Equal("{W/P}", Assert.IsType<PayManaCost>(tax.UnlessPays).Colored);
        Assert.Empty(Wave27Errors(ability));

        // A repeat with nothing to repeat says nothing.
        var noPrice = new StaticAbility(new CantAttack(Player: new You(), PerAttackingCreature: true));
        Assert.Contains(Wave27Errors(noPrice), f => f.Code == "L045");
    }

    [Fact]
    public void A_spree_mode_pays_its_cost_for_real()
    {
        // CR 702.172: the mode's cost is an additional cost of the spell, not a price spent against
        // a budget.
        var ability = new SpellAbility(new ChooseMode(
            [
                new Mode("Destroy target artifact.", new Destroy(new Target(new ObjectFilter(Types: CardType.Artifact))), PaidCost: new PayManaCost(new Fixed(1))),
                new Mode("Draw a card.", new Draw(new Fixed(1)), PaidCost: new PayManaCost(new Fixed(2))),
            ],
            Min: 1,
            Max: 2));
        var back = Wave27RoundTrip(Wave27DefinitionWith(ability));

        var modes = Assert.IsType<ChooseMode>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do).Modes;
        Assert.Equal(1, Assert.IsType<Fixed>(Assert.IsType<PayManaCost>(modes[0].PaidCost).Generic).N);
        Assert.Null(modes[0].Cost);
        Assert.Empty(Wave27Errors(ability));
    }

    [Fact]
    public void A_spree_mode_never_carries_a_budget_cost()
    {
        // Both prices on one mode says it is paid and not paid (CR 702.172 against CR 700.2).
        var pricedTwice = new SpellAbility(new ChooseMode(
            [new Mode("Draw a card.", new Draw(new Fixed(1)), Cost: new PayManaCost(new Fixed(1)), PaidCost: new PayManaCost(new Fixed(1)))],
            Budget: new Fixed(5)));
        Assert.Contains(Wave27Errors(pricedTwice), f => f.Code == "L046");

        // A spree list has no budget to spend: its modes are counted and paid for.
        var budgeted = new SpellAbility(new ChooseMode(
            [new Mode("Draw a card.", new Draw(new Fixed(1)), PaidCost: new PayManaCost(new Fixed(1)))],
            Budget: new Fixed(5)));
        Assert.Contains(Wave27Errors(budgeted), f => f.Code == "L046");
    }

    [Fact]
    public void A_preparation_card_carries_its_prepare_spell()
    {
        // CR 722.1 with 722.2a: the layout is the two-part frame and the role is the inset half.
        var definition = new CardDefinition(
            OracleId: "00000000-0000-0000-0000-000000000f27",
            Name: "Wave Twenty Seven Addition",
            Layout: Layout.Prepare,
            Faces:
            [
                new Face(
                    Name: "Wave Twenty Seven Addition",
                    Role: FaceRole.Main,
                    Types: new TypeLine(CardType.Creature),
                    ManaCost: "{1}{W}",
                    OracleText: "Wave twenty seven text."),
                new Face(
                    Name: "Wave Twenty Seven Prepare",
                    Role: FaceRole.PrepareSpell,
                    Types: new TypeLine(CardType.Instant),
                    ManaCost: "{W}",
                    OracleText: "Target creature gains flying until end of turn."),
            ]);
        var back = Wave27RoundTrip(definition);

        Assert.Equal(Layout.Prepare, back.Layout);
        Assert.Equal(FaceRole.PrepareSpell, back.Faces[1].Role);
        Assert.DoesNotContain(Wave27ErrorsFor(definition), f => f.Code == "L046");

        // A preparation layout with no inset frame is half a card.
        var noInset = definition with { Faces = [definition.Faces[0]] };
        Assert.Contains(Wave27ErrorsFor(noInset), f => f.Code == "L046");

        // And an inset frame on any other layout is a face no frame prints.
        var wrongLayout = definition with { Layout = Layout.Normal };
        Assert.Contains(Wave27ErrorsFor(wrongLayout), f => f.Code == "L046");
    }

    [Fact]
    public void A_copy_token_needs_no_name()
    {
        // CR 707.2: a copy acquires the copiable values of what it copies, name included, so there
        // is no name for the spec to carry and L010 must not ask for one.
        var copy = new SpellAbility(new CreateToken(
            new TokenSpec(Name: "", Colors: Color.Colorless, Types: CardType.Creature),
            CopyOf: new Target(ObjectFilter.Creature)));
        Assert.DoesNotContain(Wave27Errors(copy), f => f.Code == "L010");

        // Every other token is named by the effect that creates it.
        var plain = new SpellAbility(new CreateToken(new TokenSpec(Name: "", Colors: Color.White, Types: CardType.Creature)));
        Assert.Contains(Wave27Errors(plain), f => f.Code == "L010");
    }

    [Fact]
    public void A_group_of_permanents_sharing_a_name_is_counted()
    {
        // CR 201.2 with 201.2a: "three or more lands with the same name" is the largest group that
        // shares one, and a plain count is satisfied by three different names.
        var ability = new SpellAbility(new Draw(new GreatestSameNameCount(
            new ObjectFilter(Types: CardType.Land, ControlledBy: new You()))));
        var back = Wave27RoundTrip(Wave27DefinitionWith(ability));

        var amount = Assert.IsType<GreatestSameNameCount>(Assert.IsType<Draw>(Assert.IsType<SpellAbility>(back.Faces[0].Abilities![0]).Do).N);
        Assert.Equal(CardType.Land, amount.Filter.Types);
        Assert.Empty(Wave27Errors(ability));

        // A filter that names the name has already said which one; that is a CountOf.
        var named = new SpellAbility(new Draw(new GreatestSameNameCount(new ObjectFilter(Named: "Forest"))));
        Assert.Contains(Wave27Errors(named), f => f.Code == "L045");
    }

    [Fact]
    public void A_mana_replacement_can_watch_a_permanent_tapped_for_mana()
    {
        // CR 605.1a: "if you tap a permanent for mana, add one additional mana of any type it
        // produced" — only the mana a mana ability added as its tapping cost was paid.
        var ability = new ReplacementAbility(
            new WouldAddMana(new You(), TappedFor: true),
            new AddMana(new ManaProduction(Fixed: "{G}")));
        var back = Wave27RoundTrip(Wave27DefinitionWith(ability));

        var replaced = Assert.IsType<WouldAddMana>(Assert.IsType<ReplacementAbility>(back.Faces[0].Abilities![0]).Instead);
        Assert.True(replaced.TappedFor);
        Assert.Empty(Wave27Errors(ability));

        // Only a permanent can be tapped for mana.
        var spellSource = new ReplacementAbility(
            new WouldAddMana(new You(), FromSource: new ObjectFilter(Spell: true), TappedFor: true),
            new AddMana(new ManaProduction(Fixed: "{G}")));
        Assert.Contains(Wave27Errors(spellSource), f => f.Code == "L045");
    }
}
