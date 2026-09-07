using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The layer system, CR 613, on made-up definitions built here: no card name and no card file
/// takes part. The rulebook's own examples under 613.4d, 613.5 and 613.9 are the acceptance
/// tests; the rest are one test per behaviour the layer system owns.
/// </summary>
public sealed class LayerTests
{
    private static readonly PlayerId Mine = new(0);
    private static readonly PlayerId Theirs = new(1);

    private readonly LayerSystem _layers = new();

    [Fact]
    public void A_printed_creature_is_its_printed_characteristics()
    {
        var bear = Obj(1, CreatureFace("Test Bear", 2, 2), Mine);
        var chars = _layers.Of(Board(bear), bear.Id);

        Assert.Equal("Test Bear", chars.Name);
        Assert.Equal(CardType.Creature, chars.Types);
        Assert.Equal(2, chars.Power);
        Assert.Equal(2, chars.Toughness);
        // CR 202.2: the colors are the colors of the mana cost's symbols.
        Assert.Equal(Color.Green, chars.Colors);
        Assert.Equal(Mine, chars.Controller);
    }

    [Fact]
    public void A_lords_static_bonus_reaches_your_other_creatures_and_not_the_opponents()
    {
        var lord = Obj(1, LordFace(1, 1, new ObjectFilter(Types: CardType.Creature, ControlledBy: new You(), Other: true)), Mine);
        var yours = Obj(2, CreatureFace("Test Bear", 2, 2), Mine);
        var theirs = Obj(3, CreatureFace("Test Bear", 2, 2), Theirs);
        var board = Board(lord, yours, theirs);

        Assert.Equal(3, _layers.Of(board, yours.Id).Power);
        Assert.Equal(3, _layers.Of(board, yours.Id).Toughness);
        Assert.Equal(2, _layers.Of(board, theirs.Id).Power);
        // "each other creature": the lord does not pump itself.
        Assert.Equal(2, _layers.Of(board, lord.Id).Power);
    }

    [Fact]
    public void A_minus_one_minus_one_counter_makes_a_one_one_a_zero_zero()
    {
        // CR 613.4c: counters that modify power and toughness apply in layer 7c.
        var creature = Obj(1, CreatureFace("Test Rat", 1, 1), Mine) with
        {
            Counters = ImmutableDictionary<string, int>.Empty.Add(Counters.MinusOneMinusOne, 1),
        };
        var chars = _layers.Of(Board(creature), creature.Id);

        Assert.Equal(0, chars.Power);
        Assert.Equal(0, chars.Toughness);
    }

    [Fact]
    public void A_control_changing_effect_changes_who_controls_the_object()
    {
        // CR 613.1b, layer 2. "You" in the effect is the controller of the effect, CR 109.5.
        var creature = Obj(1, CreatureFace("Test Bear", 2, 2), Mine);
        var board = Board(creature) with
        {
            ActiveEffects = [Active(1, new ControlBy(new You()), creature.Id, Theirs, timestamp: 2)],
        };

        Assert.Equal(Theirs, _layers.ControllerOf(board, creature.Id));
        Assert.Equal(Theirs, _layers.Of(board, creature.Id).Controller);
    }

    [Fact]
    public void With_no_effect_on_it_the_controller_is_the_one_it_started_with()
    {
        var creature = Obj(1, CreatureFace("Test Bear", 2, 2), Theirs);
        Assert.Equal(Theirs, _layers.ControllerOf(Board(creature), creature.Id));
    }

    [Fact]
    public void Two_set_pt_effects_apply_in_timestamp_order()
    {
        // CR 613.7: within a sublayer the later timestamp is applied last, so it wins.
        var creature = Obj(1, CreatureFace("Test Bear", 2, 2), Mine);
        var later = Board(creature) with
        {
            ActiveEffects =
            [
                Active(1, new SetPt(new Fixed(0), new Fixed(1)), creature.Id, Mine, timestamp: 2),
                Active(2, new SetPt(new Fixed(5), new Fixed(5)), creature.Id, Mine, timestamp: 3),
            ],
        };
        var earlier = Board(creature) with
        {
            ActiveEffects =
            [
                Active(1, new SetPt(new Fixed(0), new Fixed(1)), creature.Id, Mine, timestamp: 4),
                Active(2, new SetPt(new Fixed(5), new Fixed(5)), creature.Id, Mine, timestamp: 3),
            ],
        };

        Assert.Equal(5, _layers.Of(later, creature.Id).Power);
        Assert.Equal(0, _layers.Of(earlier, creature.Id).Power);
        Assert.Equal(1, _layers.Of(earlier, creature.Id).Toughness);
    }

    [Fact]
    public void A_granted_keyword_shows_in_has()
    {
        var creature = Obj(1, CreatureFace("Test Bear", 2, 2), Mine);
        var board = Board(creature) with
        {
            ActiveEffects = [Active(1, new GrantKeyword(KeywordKind.Flying), creature.Id, Mine, timestamp: 2)],
        };

        Assert.True(_layers.Of(board, creature.Id).Has(KeywordKind.Flying));
        Assert.False(_layers.Of(Board(creature), creature.Id).Has(KeywordKind.Flying));
    }

    [Fact]
    public void Of_a_grant_and_a_loss_of_the_same_keyword_the_later_timestamp_wins()
    {
        // The example under CR 613.9: neither effect depends on the other, so timestamp order decides.
        var creature = Obj(1, CreatureFace("Test Bird", 2, 2, [new KeywordAbility(KeywordKind.Flying)]), Mine);
        var lost = Board(creature) with
        {
            ActiveEffects = [Active(1, new LoseAbility(KeywordKind.Flying), creature.Id, Mine, timestamp: 3)],
        };
        var regained = Board(creature) with
        {
            ActiveEffects =
            [
                Active(1, new LoseAbility(KeywordKind.Flying), creature.Id, Mine, timestamp: 3),
                Active(2, new GrantKeyword(KeywordKind.Flying), creature.Id, Mine, timestamp: 4),
            ],
        };

        Assert.False(_layers.Of(lost, creature.Id).Has(KeywordKind.Flying));
        Assert.True(_layers.Of(regained, creature.Id).Has(KeywordKind.Flying));
    }

    [Fact]
    public void A_face_down_object_is_a_two_two_with_no_name_and_no_abilities()
    {
        // CR 708.2a, applied in layer 1b (CR 613.2b).
        var face = CreatureFace("Test Dragon", 5, 5, [new KeywordAbility(KeywordKind.Flying)]);
        var creature = Obj(1, face, Mine) with { FaceDown = true };
        var chars = _layers.Of(Board(creature), creature.Id);

        Assert.Equal(2, chars.Power);
        Assert.Equal(2, chars.Toughness);
        Assert.Equal(string.Empty, chars.Name);
        Assert.Empty(chars.Abilities);
        Assert.False(chars.Has(KeywordKind.Flying));
        Assert.Equal(Color.Colorless, chars.Colors);
        Assert.Equal(CardType.Creature, chars.Types);
        Assert.Empty(chars.Subtypes);
        Assert.Null(chars.ManaCost);
    }

    [Fact]
    public void Later_layers_still_apply_to_a_face_down_object()
    {
        var creature = Obj(1, CreatureFace("Test Dragon", 5, 5), Mine) with
        {
            FaceDown = true,
            Counters = ImmutableDictionary<string, int>.Empty.Add(Counters.PlusOnePlusOne, 1),
        };

        Assert.Equal(3, _layers.Of(Board(creature), creature.Id).Power);
    }

    [Fact]
    public void A_keyword_counter_grants_the_keyword()
    {
        // CR 122.1a: a permanent with a keyword counter on it has that keyword.
        var creature = Obj(1, CreatureFace("Test Bear", 2, 2), Mine) with
        {
            Counters = ImmutableDictionary<string, int>.Empty.Add("first strike", 1),
        };
        var chars = _layers.Of(Board(creature), creature.Id);

        Assert.True(chars.Has(KeywordKind.FirstStrike));
        Assert.Equal(2, chars.Power);
    }

    [Fact]
    public void An_amount_the_interpreter_has_not_evaluated_throws_naming_it()
    {
        var creature = Obj(1, CreatureFace("Test Bear", 2, 2), Mine);
        var board = Board(creature) with
        {
            ActiveEffects = [Active(1, new ModifyPt(new X(), new Fixed(0)), creature.Id, Mine, timestamp: 2)],
        };

        var ex = Assert.Throws<NotImplementedException>(() => _layers.Of(board, creature.Id));
        Assert.Contains(nameof(X), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_changing_effect_can_make_a_land_a_creature_and_set_its_pt()
    {
        // The example under CR 613.6: layer 4 then layer 7b, on the same object.
        var land = Obj(1, new Face("Test Waste", FaceRole.Main, new TypeLine(CardType.Land)), Mine);
        var board = Board(land) with
        {
            ActiveEffects =
            [
                Active(1, new AddTypes(CardType.Creature), land.Id, Mine, timestamp: 2),
                Active(2, new SetPt(new Fixed(3), new Fixed(3)), land.Id, Mine, timestamp: 2),
            ],
        };
        var chars = _layers.Of(board, land.Id);

        Assert.Equal(CardType.Land | CardType.Creature, chars.Types);
        Assert.Equal(3, chars.Power);
        Assert.Equal(3, chars.Toughness);
    }

    [Fact]
    public void Rulebook_example_613_5_a_set_in_7b_does_not_undo_the_modifications_in_7c()
    {
        // CR 613.5: a 2/2 with a +1/+1 counter, +4/+4 from a resolved spell, +0/+2 from an
        // enchantment, then "becomes 0/1" — 5/8, because 7b is applied before 7c.
        var creature = Obj(1, CreatureFace("Test Ogre", 2, 2), Mine, timestamp: 1) with
        {
            Counters = ImmutableDictionary<string, int>.Empty.Add(Counters.PlusOnePlusOne, 1),
        };
        var enchantment = Obj(2, EnchantmentFace(0, 2, new ObjectFilter(Types: CardType.Creature, ControlledBy: new You())), Mine, timestamp: 4);
        var board = Board(creature, enchantment) with
        {
            ActiveEffects =
            [
                Active(1, new ModifyPt(new Fixed(4), new Fixed(4)), creature.Id, Mine, timestamp: 3),
                Active(2, new SetPt(new Fixed(0), new Fixed(1)), creature.Id, Mine, timestamp: 5),
            ],
        };
        var chars = _layers.Of(board, creature.Id);

        Assert.Equal(5, chars.Power);
        Assert.Equal(8, chars.Toughness);
    }

    [Fact]
    public void Rulebook_example_613_4d_a_switch_applies_after_every_modification()
    {
        // CR 613.4d: 1/3, +0/+1, switched, is 4/1; a later +5/+0 makes it 4/6.
        var creature = Obj(1, CreatureFace("Test Wall", 1, 3), Mine);
        var switched = Board(creature) with
        {
            ActiveEffects =
            [
                Active(1, new ModifyPt(new Fixed(0), new Fixed(1)), creature.Id, Mine, timestamp: 2),
                Active(2, new SwitchPt(), creature.Id, Mine, timestamp: 3),
            ],
        };
        var andPumped = switched with
        {
            ActiveEffects = switched.ActiveEffects.Add(Active(3, new ModifyPt(new Fixed(5), new Fixed(0)), creature.Id, Mine, timestamp: 4)),
        };
        var twice = switched with
        {
            ActiveEffects = switched.ActiveEffects.Add(Active(3, new SwitchPt(), creature.Id, Mine, timestamp: 4)),
        };

        Assert.Equal(4, _layers.Of(switched, creature.Id).Power);
        Assert.Equal(1, _layers.Of(switched, creature.Id).Toughness);
        Assert.Equal(4, _layers.Of(andPumped, creature.Id).Power);
        Assert.Equal(6, _layers.Of(andPumped, creature.Id).Toughness);
        // Two switches cancel each other out.
        Assert.Equal(1, _layers.Of(twice, creature.Id).Power);
        Assert.Equal(4, _layers.Of(twice, creature.Id).Toughness);
    }

    [Fact]
    public void Rulebook_example_613_9_a_color_change_in_layer_5_decides_whether_layer_7_applies()
    {
        // CR 613.9: "White creatures get +1/+1" and an effect making a creature white.
        var lord = Obj(1, LordFace(1, 1, new ObjectFilter(Types: CardType.Creature, Colors: Color.White, ControlledBy: new You())), Mine);
        var black = Obj(2, CreatureFace("Test Zombie", 2, 2, manaCost: "{1}{B}"), Mine);
        var asPrinted = Board(lord, black);
        var madeWhite = asPrinted with
        {
            ActiveEffects = [Active(1, new SetColors(Color.White), black.Id, Mine, timestamp: 3)],
        };

        Assert.Equal(2, _layers.Of(asPrinted, black.Id).Power);
        Assert.Equal(3, _layers.Of(madeWhite, black.Id).Power);
        Assert.Equal(3, _layers.Of(madeWhite, black.Id).Toughness);
    }

    [Fact]
    public void A_copy_effect_takes_the_printed_values_of_what_it_copies()
    {
        // Layer 1, CR 613.2a and 707.2.
        var original = Obj(1, CreatureFace("Test Sphinx", 4, 5, [new KeywordAbility(KeywordKind.Flying)]), Theirs);
        var shapeshifter = Obj(2, CreatureFace("Test Shape", 1, 1), Mine);
        var board = Board(original, shapeshifter) with
        {
            ActiveEffects =
            [
                Active(1, new BecomesCopyOf(new Each(new ObjectFilter(Named: "Test Sphinx"))), shapeshifter.Id, Mine, timestamp: 2),
            ],
        };
        var chars = _layers.Of(board, shapeshifter.Id);

        Assert.Equal("Test Sphinx", chars.Name);
        Assert.Equal(4, chars.Power);
        Assert.Equal(5, chars.Toughness);
        Assert.True(chars.Has(KeywordKind.Flying));
        // A copy effect does not change control, CR 707.2.
        Assert.Equal(Mine, chars.Controller);
    }

    [Fact]
    public void A_static_ability_of_a_face_down_permanent_does_not_apply()
    {
        // CR 708.2a: a face-down permanent has no text, so it grants nothing.
        var lord = Obj(1, LordFace(1, 1, new ObjectFilter(Types: CardType.Creature, ControlledBy: new You())), Mine) with { FaceDown = true };
        var bear = Obj(2, CreatureFace("Test Bear", 2, 2), Mine);

        Assert.Equal(2, _layers.Of(Board(lord, bear), bear.Id).Power);
    }

    [Fact]
    [RuleRef("613.1a")]
    public void The_printed_reading_answers_for_an_object_the_state_no_longer_holds()
    {
        // CR 613.1a through the seam: a look-back (CR 603.10, CR 111.7) asks what an object was,
        // and Of(state, id) cannot answer, because the layers are computed from what exists now
        // and a departed object has no id to hand them. PrintedOf takes the object instead.
        ICharacteristics seam = _layers;
        var departed = Obj(9, CreatureFace("Test Wurm", 5, 5), Mine);
        var board = Board(Obj(1, CreatureFace("Test Bear", 2, 2), Mine));

        Assert.False(board.Objects.ContainsKey(departed.Id));

        var printed = seam.PrintedOf(departed);
        Assert.Equal("Test Wurm", printed.Name);
        Assert.Equal(CardType.Creature, printed.Types);
        Assert.Equal(5, printed.Power);
        Assert.Equal(5, printed.Toughness);
        Assert.Contains("Beast", printed.Subtypes, StringComparer.Ordinal);
        // CR 202.2: the colour is the mana cost's, not a continuous effect's.
        Assert.Equal(Color.Green, printed.Colors);

        // The class's own static is the same reading, so the seam adds a caller and not a rule.
        // The two list fields are compared by their contents: a record's synthesized equality
        // compares an ImmutableList by reference, and each call snapshots its own.
        var direct = LayerSystem.PrintedCharacteristics(departed);
        Assert.Equal(direct.Subtypes, printed.Subtypes, StringComparer.Ordinal);
        Assert.Equal(direct.Abilities.Count, printed.Abilities.Count);
        Assert.Equal(direct with { Subtypes = printed.Subtypes, Abilities = printed.Abilities }, printed);

        // CR 613.2b with 708.2a: face down it was a nameless colourless 2/2 creature.
        var hidden = seam.PrintedOf(departed with { FaceDown = true });
        Assert.Equal(string.Empty, hidden.Name);
        Assert.Equal(CardType.Creature, hidden.Types);
        Assert.Equal(2, hidden.Power);
        Assert.Equal(2, hidden.Toughness);
        Assert.Equal(Color.Colorless, hidden.Colors);
    }

    [Fact]
    [RuleRef("613.10")]
    public void A_player_cant_carries_every_restriction_it_names()
    {
        // CR 613.10: a player-scoped effect is read off the state, not applied in the layers.
        // Every flag PlayerRestriction has is one prohibition PlayerCant can name, and naming
        // several at once sets all of them — a card that says two things says both.
        var every = PlayerRestriction.LifeTotalChanges | PlayerRestriction.Lose
            | PlayerRestriction.BeTargeted | PlayerRestriction.BeDealtDamage;
        var state = Restricted(new PlayerCant(every));
        var limits = _layers.PlayerRestrictions(state, Mine);

        Assert.True(limits.LifeTotalCantChange);   // CR 119.3
        Assert.True(limits.CantLose);              // CR 104.3
        Assert.True(limits.CantBeTargeted);        // CR 115.6
        Assert.True(limits.CantBeDealtDamage);     // CR 120.3
        Assert.True(limits.CantGainLifeNow);       // a total that can't change can't go up
        foreach (var one in new[] { PlayerRestriction.LifeTotalChanges, PlayerRestriction.Lose, PlayerRestriction.BeTargeted, PlayerRestriction.BeDealtDamage })
            Assert.True(limits.Has(one));
        Assert.False(limits.Has(PlayerRestriction.None));

        // Nothing here is protection, and the effect names one player: the other is unrestricted.
        Assert.False(limits.CantBeEnchanted);
        Assert.False(limits.CantWin);
        Assert.Equal(PlayerLimits.None, _layers.PlayerRestrictions(state, Theirs));
    }

    [Fact]
    [RuleRef("702.16j")]
    public void Protection_from_everything_gives_a_player_the_three_limits_it_implies()
    {
        // CR 702.16j names exactly three for a player: can't be targeted, all damage that would
        // be dealt to them is prevented, and they can't be enchanted. It is not a "can't lose"
        // and it is not a life-total lock, so those two stay off.
        var state = Restricted(new PlayerProtectionFromEverything());
        var limits = _layers.PlayerRestrictions(state, Mine);

        Assert.True(limits.CantBeTargeted);
        Assert.True(limits.CantBeDealtDamage);
        Assert.True(limits.CantBeEnchanted);
        Assert.False(limits.CantLose);
        Assert.False(limits.LifeTotalCantChange);
        Assert.False(limits.CantGainLifeNow);
        Assert.Equal(PlayerLimits.None, _layers.PlayerRestrictions(state, Theirs));

        // A restriction flag is the card-text form and never implies the keyword's other two.
        var targeted = _layers.PlayerRestrictions(Restricted(new PlayerCant(PlayerRestriction.BeTargeted)), Mine);
        Assert.True(targeted.CantBeTargeted);
        Assert.False(targeted.CantBeEnchanted);
        Assert.False(targeted.CantBeDealtDamage);
    }

    /// <summary>An empty board carrying one player-scoped continuous effect on <see cref="Mine"/>, CR 611.2c.</summary>
    private static GameState Restricted(ContinuousEffect effect) => Board() with
    {
        ActiveEffects = [new ActiveEffect(1, effect, null, Mine, [], [Mine], new UntilEndOfTurn(), 1, false)],
    };

    private static GameState Board(params GameObject[] objects) => new()
    {
        Players = [new PlayerState(Mine, "A", 20), new PlayerState(Theirs, "B", 20)],
        Objects = objects.ToImmutableDictionary(o => o.Id),
        Zones = ImmutableDictionary<ZoneKey, ImmutableList<ObjectId>>.Empty
            .Add(ZoneKey.Shared(Zone.Battlefield), [.. objects.Select(o => o.Id)]),
        Turn = new TurnState(1, Mine, Step.PrecombatMain, 0),
    };

    private static ActiveEffect Active(int id, ContinuousEffect effect, ObjectId appliesTo, PlayerId controller, int timestamp) =>
        new(id, effect, null, controller, [appliesTo], [], new UntilEndOfTurn(), timestamp, false);

    private static GameObject Obj(int id, Face face, PlayerId controller, int timestamp = 1) =>
        new(
            new ObjectId(id),
            new CardDefinition($"oracle-{id}", face.Name, Layout.Normal, [face]),
            0,
            controller,
            controller,
            ZoneKey.Shared(Zone.Battlefield),
            timestamp);

    private static Face CreatureFace(string name, int power, int toughness, IReadOnlyList<Ability>? abilities = null, string manaCost = "{1}{G}") =>
        new(
            name,
            FaceRole.Main,
            new TypeLine(CardType.Creature, Subtypes: ["Beast"]),
            ManaCost: manaCost,
            Power: new PrintedStat(power.ToString(CultureInfo.InvariantCulture)),
            Toughness: new PrintedStat(toughness.ToString(CultureInfo.InvariantCulture)),
            Abilities: abilities);

    /// <summary>A creature whose static ability modifies the power and toughness of what the filter names.</summary>
    private static Face LordFace(int power, int toughness, ObjectFilter what) =>
        CreatureFace(
            "Test Lord",
            2,
            2,
            [new StaticAbility(new ModifyPt(new Fixed(power), new Fixed(toughness)), To: new Each(what))],
            "{2}{W}");

    /// <summary>An enchantment with the same static ability, so its own type does not match the filter.</summary>
    private static Face EnchantmentFace(int power, int toughness, ObjectFilter what) =>
        new(
            "Test Banner",
            FaceRole.Main,
            new TypeLine(CardType.Enchantment),
            ManaCost: "{2}{W}",
            Abilities: [new StaticAbility(new ModifyPt(new Fixed(power), new Fixed(toughness)), To: new Each(what))]);
}
