using System.Text.Json;

namespace MtgEngine.Cards.Tests;

/// <summary>
/// The sentences the 200-face pilot's two translators could not express, one test per addition.
/// Each addition has to be in its name table, survive a round trip with its own kind, be described
/// in the schema the translator is constrained to, be reachable by <see cref="Linter.Walk"/>, and
/// lint clean — a record that misses any of those is a card lost silently at translation time.
/// The IR version is deliberately not bumped: every addition has a default, so a file written
/// before it existed still reads (asserted at the bottom).
/// </summary>
public sealed class VocabularyBacklogTests
{
    private static CardDefinition BacklogDefinitionWith(params Ability[] abilities) => new(
        OracleId: "00000000-0000-0000-0000-0000000000b3",
        Name: "Backlog Subject",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Backlog Subject",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Creature, Subtypes: ["Human"]),
                ManaCost: "{2}{G}",
                Abilities: abilities,
                OracleText: "Test text.")
        ]);

    private static CardFile BacklogFileWith(params Ability[] abilities)
    {
        var definition = BacklogDefinitionWith(abilities);
        return new CardFile(
            OracleId: definition.OracleId,
            Name: definition.Name,
            OracleHash: OracleHashing.Of(definition.Faces.Select(f => f.OracleText)),
            IrVersion: IrVersion.Current,
            Definition: definition,
            Status: ImplementationStatus.Translated,
            Source: new TemplateProvenance("test"));
    }

    private static string BacklogJson(params Ability[] abilities) =>
        CardFileSerializer.WriteDefinition(BacklogDefinitionWith(abilities));

    private static CardDefinition BacklogRoundTrip(params Ability[] abilities) =>
        CardFileSerializer.ReadDefinition(BacklogJson(abilities));

    private static T BacklogFirst<T>(CardDefinition definition)
        where T : Ability => Assert.IsType<T>(definition.Faces[0].AbilityList[0]);

    // 1. "Unless that player pays {X}, where X is this creature's power" — CR 118.8, 601.2h.
    private static SpellAbility BacklogUnlessPaysPower() =>
        new(new Unless(
            new Destroy(new Target(ObjectFilter.Creature)),
            new PayManaCost(new PowerOf(new Self())),
            new EachOpponent()));

    // 1. "You may pay {4}. If you do, draw a card." — CR 118.12, composed with May and If.
    private static SpellAbility BacklogMayPayThenDraw() =>
        new(new Sequence([
            new May(new PayCost(new ManaCost("{4}")), StoreChoiceAs: "paid"),
            new If(new ChoiceIs("paid", "yes"), new Draw(new Fixed(1)))
        ]));

    // 2. Multikicker: a repeatable keyword and the count of its payments — CR 702.32b.
    private static KeywordAbility BacklogMultikicker() => new(KeywordKind.Kicker, "{1}{G}", Repeatable: true);

    // 3. A search over more than one zone — CR 701.23.
    private static SpellAbility BacklogSearchTwoZones() =>
        new(new Search(
            new ObjectFilter(Types: CardType.Creature),
            Zone.Hand,
            Zones: [Zone.Graveyard, Zone.Library],
            ShuffleOnlyIfFound: true));

    // 11. Escape's cost is not a mana string — CR 702.138a.
    private static KeywordAbility BacklogEscape() =>
        new(KeywordKind.Escape, Costs: [new ManaCost("{1}{B}{B}"), new ExileFromZoneCost(Zone.Graveyard, Count: new Fixed(4))]);

    [Fact]
    public void Every_backlog_record_is_registered_under_its_base()
    {
        var expected = new (Type Base, Type Record)[]
        {
            (typeof(Cost), typeof(PayManaCost)),
            (typeof(Effect), typeof(PayCost)),
            (typeof(Amount), typeof(TimesKicked)),
            (typeof(Condition), typeof(AttackedThisTurn)),
            (typeof(Condition), typeof(GiftPromised)),
            (typeof(Predicate), typeof(IsPrepared)),
            (typeof(ContinuousEffect), typeof(PlayerCant)),
            (typeof(ContinuousEffect), typeof(PlayerProtectionFromEverything)),
            (typeof(Effect), typeof(DistributeCounters)),
            (typeof(Effect), typeof(ChangeTarget)),
            (typeof(Effect), typeof(Flip)),
            (typeof(Effect), typeof(Prepare)),
            (typeof(Effect), typeof(Unprepare)),
        };

        foreach (var (polymorphicBase, record) in expected)
        {
            var registered = CardFileSerializer.RegisteredNames(polymorphicBase);
            Assert.True(registered.ContainsKey(record), $"{record.Name} has no [JsonDerivedType] on {polymorphicBase.Name}.");
            Assert.Equal(record.Name, registered[record]);
        }
    }

    [Fact]
    public void A_dynamic_mana_cost_round_trips_as_a_cost_and_not_as_a_mana_string()
    {
        var json = BacklogJson(BacklogUnlessPaysPower());
        Assert.Contains("\"PayManaCost\"", json, StringComparison.Ordinal);

        var unless = Assert.IsType<Unless>(BacklogFirst<SpellAbility>(BacklogRoundTrip(BacklogUnlessPaysPower())).Do);
        var cost = Assert.IsType<PayManaCost>(unless.Paid);
        Assert.IsType<PowerOf>(cost.Generic);
        Assert.Null(cost.Colored);
        Assert.Equal(json, CardFileSerializer.WriteDefinition(BacklogRoundTrip(BacklogUnlessPaysPower())));
    }

    [Fact]
    public void Pay_a_cost_inside_a_resolving_effect_composes_with_May_and_If()
    {
        var back = BacklogRoundTrip(BacklogMayPayThenDraw());

        var steps = Assert.IsType<Sequence>(BacklogFirst<SpellAbility>(back).Do).Steps;
        var may = Assert.IsType<May>(steps[0]);
        Assert.Equal("paid", may.StoreChoiceAs);
        Assert.IsType<ManaCost>(Assert.IsType<PayCost>(may.Do).Cost);
        Assert.IsType<ChoiceIs>(Assert.IsType<If>(steps[1]).Test);
    }

    [Fact]
    public void Multikicker_is_castable_repeatedly_and_countable()
    {
        var json = BacklogJson(BacklogMultikicker(), new SpellAbility(new Draw(new TimesKicked())));
        Assert.Contains("\"repeatable\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"TimesKicked\"", json, StringComparison.Ordinal);

        var back = CardFileSerializer.ReadDefinition(json);
        Assert.True(BacklogFirst<KeywordAbility>(back).Repeatable);
        var draw = Assert.IsType<Draw>(Assert.IsType<SpellAbility>(back.Faces[0].AbilityList[1]).Do);
        Assert.IsType<TimesKicked>(draw.N);

        // Plain kicker keeps the meaning it had; the flag is additive.
        Assert.False(new KeywordAbility(KeywordKind.Kicker, "{2}").Repeatable);
    }

    [Fact]
    public void A_search_can_name_several_zones_and_shuffle_only_when_it_found_something()
    {
        var search = Assert.IsType<Search>(BacklogFirst<SpellAbility>(BacklogRoundTrip(BacklogSearchTwoZones())).Do);

        Assert.Equal([Zone.Graveyard, Zone.Library], search.Zones);
        Assert.True(search.ShuffleOnlyIfFound);
        // The single-zone spelling is untouched: From still defaults to the library.
        Assert.Equal(Zone.Library, search.From);
        Assert.Null(new Search(ObjectFilter.Any, Zone.Hand).Zones);
    }

    [Fact]
    public void A_filter_can_say_attacking_or_blocking_without_matching_nothing()
    {
        // Attacking: true, Blocking: true would be a conjunction and match no creature at all.
        var filter = new ObjectFilter(Types: CardType.Creature, InCombat: true);

        var json = JsonSerializer.Serialize(filter, CardFileSerializer.Options);
        Assert.Contains("\"inCombat\": true", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize<ObjectFilter>(json, CardFileSerializer.Options)!;
        Assert.True(back.InCombat);
        Assert.Null(back.Attacking);
        Assert.Null(back.Blocking);
        Assert.Null(ObjectFilter.Creature.InCombat);
    }

    [Fact]
    public void Attacking_with_two_or_more_creatures_is_a_condition_with_a_number()
    {
        var ability = new TriggeredAbility(
            new Attacks(ObjectFilter.Creature),
            new Draw(new Fixed(1)),
            If: new AttackedThisTurn(new You(), new Fixed(2)));

        var back = BacklogFirst<TriggeredAbility>(BacklogRoundTrip(ability));
        var condition = Assert.IsType<AttackedThisTurn>(back.If);
        Assert.Equal(new Fixed(2), condition.Min);
        Assert.IsType<You>(condition.Who);
    }

    [Fact]
    public void Devotion_can_take_a_chosen_color_instead_of_a_printed_one()
    {
        var chosen = new Devotion(new You(), ChosenColorKey: "color");
        var printed = new Devotion(new You(), Color.Black);

        var ability = new SpellAbility(new Sequence([
            new ChooseValue(ChooseWhat.Color, "color"),
            new GainLife(chosen)
        ]));

        var back = BacklogFirst<SpellAbility>(BacklogRoundTrip(ability));
        var gain = Assert.IsType<GainLife>(Assert.IsType<Sequence>(back.Do).Steps[1]);
        var amount = Assert.IsType<Devotion>(gain.N);
        Assert.Equal("color", amount.ChosenColorKey);
        Assert.Equal(Color.Colorless, amount.Colors);

        // The printed spelling that was already in card files is unchanged.
        Assert.Equal(Color.Black, printed.Colors);
        Assert.Null(printed.ChosenColorKey);
    }

    [Fact]
    public void Mana_can_carry_a_spend_restriction_and_an_ability_a_payment_restriction()
    {
        var ability = new ActivatedAbility(
            [new TapSelf()],
            new AddMana(new ManaProduction("{C}"), SpendOnlyOn: new ObjectFilter(Spell: true, Types: CardType.Artifact)),
            PayOnlyWith: Color.Green);

        var back = BacklogFirst<ActivatedAbility>(BacklogRoundTrip(ability));
        var add = Assert.IsType<AddMana>(back.Do);
        Assert.Equal(CardType.Artifact, add.SpendOnlyOn!.Types);
        Assert.True(add.SpendOnlyOn.Spell);
        Assert.Equal(Color.Green, back.PayOnlyWith);

        // Unrestricted mana keeps its old shape.
        Assert.Null(new AddMana(new ManaProduction("{G}")).SpendOnlyOn);
        Assert.Null(new ActivatedAbility([new TapSelf()], new Draw(new Fixed(1))).PayOnlyWith);
    }

    [Fact]
    public void A_player_can_be_stopped_from_losing_and_from_being_targeted_in_one_effect()
    {
        var ability = new StaticAbility(
            new PlayerCant(PlayerRestriction.Lose | PlayerRestriction.LifeTotalChanges),
            Players: new You());

        var json = BacklogJson(ability);
        // The flag set writes as an array of names, like every other [Flags] enum in the vocabulary.
        Assert.Contains("\"LifeTotalChanges\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Lose\"", json, StringComparison.Ordinal);

        var back = BacklogFirst<StaticAbility>(CardFileSerializer.ReadDefinition(json));
        var cant = Assert.IsType<PlayerCant>(back.Effect);
        Assert.Equal(PlayerRestriction.Lose | PlayerRestriction.LifeTotalChanges, cant.What);
        Assert.Equal(json, CardFileSerializer.WriteDefinition(CardFileSerializer.ReadDefinition(json)));
    }

    [Fact]
    public void Protection_from_everything_is_a_player_scoped_continuous_effect()
    {
        var ability = new SpellAbility(new ApplyToPlayer(
            new PlayerProtectionFromEverything(),
            new You(),
            new UntilEndOfTurn()));

        var apply = Assert.IsType<ApplyToPlayer>(BacklogFirst<SpellAbility>(BacklogRoundTrip(ability)).Do);
        Assert.IsType<PlayerProtectionFromEverything>(apply.What);
        Assert.IsType<UntilEndOfTurn>(apply.Until);
    }

    [Fact]
    public void The_gift_and_the_prepared_designation_round_trip()
    {
        var ability = new SpellAbility(new Sequence([
            new If(new GiftPromised(), new Draw(new Fixed(1))),
            new Prepare(new Target(ObjectFilter.Creature)),
            new Unprepare(new Each(new ObjectFilter(Types: CardType.Creature, Also: new IsPrepared())))
        ]));

        var steps = Assert.IsType<Sequence>(BacklogFirst<SpellAbility>(BacklogRoundTrip(ability)).Do).Steps;
        Assert.IsType<GiftPromised>(Assert.IsType<If>(steps[0]).Test);
        Assert.IsType<Prepare>(steps[1]);
        var each = Assert.IsType<Each>(Assert.IsType<Unprepare>(steps[2]).What);
        Assert.IsType<IsPrepared>(each.Filter.Also);
    }

    [Fact]
    public void Counters_can_be_distributed_and_a_target_can_be_changed()
    {
        var ability = new SpellAbility(new Sequence([
            new DistributeCounters(new Fixed(3), "+1/+1", new Target(ObjectFilter.Creature), MaxTargets: 3),
            new ChangeTarget(new Target(new ObjectFilter(Spell: true)))
        ]));

        var steps = Assert.IsType<Sequence>(BacklogFirst<SpellAbility>(BacklogRoundTrip(ability)).Do).Steps;
        var distribute = Assert.IsType<DistributeCounters>(steps[0]);
        Assert.Equal(new Fixed(3), distribute.N);
        Assert.Equal("+1/+1", distribute.Counter);
        Assert.Equal(3, distribute.MaxTargets);
        var change = Assert.IsType<ChangeTarget>(steps[1]);
        Assert.Null(change.To);
        Assert.False(change.AllTargets);
    }

    [Fact]
    public void Escape_carries_a_cost_that_is_not_a_mana_string()
    {
        var back = BacklogFirst<KeywordAbility>(BacklogRoundTrip(BacklogEscape()));

        Assert.Null(back.Cost);
        Assert.Equal(2, back.Costs!.Count);
        Assert.Equal("{1}{B}{B}", Assert.IsType<ManaCost>(back.Costs[0]).Mana);
        Assert.Equal(Zone.Graveyard, Assert.IsType<ExileFromZoneCost>(back.Costs[1]).From);
    }

    [Fact]
    public void A_keyword_that_sets_both_spellings_of_its_cost_fails_the_linter()
    {
        var both = new KeywordAbility(KeywordKind.Escape, "{1}{B}{B}", Costs: [new ExileFromZoneCost(Zone.Graveyard)]);

        var report = Linter.Check(BacklogFileWith(both));

        Assert.Contains(report.Findings, f => f is { Severity: LintSeverity.Error, Code: "L005" });
    }

    [Fact]
    public void Escape_with_only_a_Costs_list_satisfies_the_keyword_parameter_check()
    {
        var report = Linter.Check(BacklogFileWith(BacklogEscape()));

        Assert.DoesNotContain(report.Findings, f => f.Code == "L005");
    }

    [Fact]
    public void Flip_is_its_own_verb_and_not_transform_or_a_coin()
    {
        var json = BacklogJson(new SpellAbility(new Flip(new Self())));

        Assert.Contains("\"Flip\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Transform\"", json, StringComparison.Ordinal);
        Assert.IsType<Flip>(BacklogFirst<SpellAbility>(CardFileSerializer.ReadDefinition(json)).Do);
    }

    [Fact]
    public void Two_searched_lands_that_share_a_type_are_Remember_plus_SharesTypeWith()
    {
        // The relation the translators asked for a record for. It was already expressible; the
        // worked example now lives in the SharesTypeWith doc comment, which the schema carries.
        var basicLand = new ObjectFilter(Types: CardType.Land, Supertypes: Supertype.Basic);
        var ability = new SpellAbility(new Sequence([
            new Search(basicLand, Zone.Battlefield, RememberAs: "first"),
            new Search(basicLand with { Also = new SharesTypeWith(new Remembered("first")) }, Zone.Battlefield)
        ]));

        var report = Linter.Check(BacklogFileWith(ability));
        Assert.Empty(report.Findings);

        var steps = Assert.IsType<Sequence>(BacklogFirst<SpellAbility>(BacklogRoundTrip(ability)).Do).Steps;
        var shares = Assert.IsType<SharesTypeWith>(Assert.IsType<Search>(steps[1]).For.Also);
        Assert.Equal("first", Assert.IsType<Remembered>(shares.With).Key);
        Assert.False(shares.CreatureTypeOnly);
    }

    [Fact]
    public void The_schema_names_every_backlog_record_and_field()
    {
        var node = CardFileSerializer.DefinitionSchema();
        var defs = node["$defs"]!.AsObject();

        foreach (var name in new[]
        {
            "PayManaCost", "PayCost", "TimesKicked", "AttackedThisTurn", "GiftPromised",
            "IsPrepared", "PlayerCant", "PlayerProtectionFromEverything", "DistributeCounters",
            "ChangeTarget", "Flip", "Prepare", "Unprepare",
        })
        {
            Assert.True(defs.ContainsKey(name), $"{name} is not in the definition schema; the translator cannot emit it.");
            Assert.Equal(name, defs[name]!["properties"]!["kind"]!["const"]!.GetValue<string>());
        }

        // A nullable bool is written as an anyOf with a null arm, a plain bool as a bare type.
        Assert.Contains("boolean", defs["ObjectFilter"]!["properties"]!["inCombat"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("boolean", defs["KeywordAbility"]!["properties"]!["repeatable"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.NotNull(defs["KeywordAbility"]!["properties"]!["costs"]);
        Assert.NotNull(defs["ActivatedAbility"]!["properties"]!["payOnlyWith"]);
        Assert.NotNull(defs["AddMana"]!["properties"]!["spendOnlyOn"]);
        Assert.NotNull(defs["Search"]!["properties"]!["zones"]);
        Assert.NotNull(defs["Devotion"]!["properties"]!["chosenColorKey"]);

        var costs = defs["Cost"]!["anyOf"]!.AsArray().Select(o => o!["$ref"]!.GetValue<string>()).ToList();
        Assert.Contains("#/$defs/PayManaCost", costs, StringComparer.Ordinal);
        var effects = defs["Effect"]!["anyOf"]!.AsArray().Select(o => o!["$ref"]!.GetValue<string>()).ToList();
        Assert.Contains("#/$defs/PayCost", effects, StringComparer.Ordinal);
        Assert.Contains("#/$defs/Flip", effects, StringComparer.Ordinal);
    }

    [Fact]
    public void The_schema_carries_the_rule_text_the_translator_reads()
    {
        var defs = CardFileSerializer.DefinitionSchema()["$defs"]!;

        // Each phrase is in the doc comment, not in the [RuleRef] fallback, so a record whose doc
        // comment was dropped fails here rather than shipping a bare rule number to the model.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PayManaCost"] = "118.8",
            ["PayCost"] = "118.12",
            ["TimesKicked"] = "702.32b",
            ["AttackedThisTurn"] = "506.2",
            ["GiftPromised"] = "702.174b",
            ["IsPrepared"] = "702.180",
            ["PlayerCant"] = "player rather than to an object",
            ["PlayerProtectionFromEverything"] = "702.16j",
            ["DistributeCounters"] = "601.2d",
            ["ChangeTarget"] = "115.7",
            ["Flip"] = "710.2",
            ["SharesTypeWith"] = "share a land type",
            ["Search"] = "701.23c",
            ["Devotion"] = "chosen color",
            ["AddMana"] = "106.6a",
            ["KeywordAbility"] = "702.138a",
            ["ActivatedAbility"] = "106.6b",
            ["ObjectFilter"] = "attacking or",
        };

        foreach (var (name, phrase) in expected)
        {
            var description = defs[name]!["description"]?.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(description), $"{name} has no description in the schema; the build's XML docs are how it gets one.");
            Assert.Contains(phrase, description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_linter_walks_every_backlog_record()
    {
        var file = BacklogFileWith(
            BacklogUnlessPaysPower(),
            BacklogMayPayThenDraw(),
            BacklogMultikicker(),
            BacklogSearchTwoZones(),
            BacklogEscape());

        var walked = Linter.Walk(file.Definition).Select(n => n.GetType()).ToHashSet();

        Assert.Contains(typeof(PayManaCost), walked);
        Assert.Contains(typeof(PayCost), walked);
        Assert.Contains(typeof(ExileFromZoneCost), walked);
        Assert.Contains(typeof(Search), walked);
    }

    [Fact]
    public void Every_backlog_addition_lints_clean_together()
    {
        var report = Linter.Check(BacklogFileWith(
            BacklogUnlessPaysPower(),
            BacklogMayPayThenDraw(),
            BacklogMultikicker(),
            BacklogSearchTwoZones(),
            BacklogEscape(),
            new StaticAbility(new PlayerCant(PlayerRestriction.BeTargeted), Players: new You()),
            new SpellAbility(new DistributeCounters(new Fixed(3), "+1/+1", new Target(ObjectFilter.Creature), 3))));

        Assert.Empty(report.Findings);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void A_whole_file_carrying_the_additions_round_trips_unchanged()
    {
        var file = BacklogFileWith(BacklogUnlessPaysPower(), BacklogMultikicker(), BacklogEscape());

        var json = CardFileSerializer.Write(file);
        var back = CardFileSerializer.Read(json);

        Assert.Equal(json, CardFileSerializer.Write(back));
        Assert.Equal(3, back.Definition.Faces[0].AbilityList.Count);
    }

    [Fact]
    public void A_definition_written_before_the_additions_existed_still_reads()
    {
        // IrVersion is not bumped: every addition defaults, so old files deserialise unchanged.
        const string keyword = """
            {"kind": "KeywordAbility", "keyword": "Kicker", "cost": "{2}"}
            """;
        var kw = Assert.IsType<KeywordAbility>(JsonSerializer.Deserialize<Ability>(keyword, CardFileSerializer.Options));
        Assert.False(kw.Repeatable);
        Assert.Null(kw.Costs);

        const string search = """
            {"kind": "Search", "for": {}, "to": "Hand"}
            """;
        var found = Assert.IsType<Search>(JsonSerializer.Deserialize<Effect>(search, CardFileSerializer.Options));
        Assert.Null(found.Zones);
        Assert.False(found.ShuffleOnlyIfFound);
        Assert.Equal(Zone.Library, found.From);

        const string mana = """
            {"kind": "AddMana", "mana": {"fixed": "{G}"}}
            """;
        Assert.Null(Assert.IsType<AddMana>(JsonSerializer.Deserialize<Effect>(mana, CardFileSerializer.Options)).SpendOnlyOn);
    }
}
