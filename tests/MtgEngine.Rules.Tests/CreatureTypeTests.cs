using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Layers;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Changeling and its opposite, through the real layer system and the real query layer:
/// CR 702.72a grants every creature type (CR 205.3d), "loses all creature types" takes exactly
/// those away (CR 205.3m), and both are read by whoever asks whether an object has a subtype.
/// The creature-type list itself is generated from the rulebook into MtgEngine.Cards, so no
/// creature type is written in engine source and none of these definitions is a real card.
/// </summary>
public sealed class CreatureTypeTests
{
    private static readonly PlayerId Mine = new(0);
    private static readonly PlayerId Theirs = new(1);

    private readonly LayerSystem _layers = new();

    [Fact]
    public void A_changeling_is_every_creature_type()
    {
        // CR 702.72a: the ability is characteristic-defining and applies in layer 4.
        var changeling = CreatureTypeObj(1, ChangelingFace(), Mine);
        var chars = _layers.Of(Board(changeling), changeling.Id);

        Assert.True(chars.EveryCreatureType);
        // The list itself is not filled in: CR 205.3m's names are rules data, not a snapshot
        // carried around on every object.
        Assert.Equal("Shapeshifter", Assert.Single(chars.Subtypes));
    }

    [Fact]
    public void A_changeling_matches_a_filter_naming_any_creature_type_and_no_other_subtype()
    {
        var changeling = CreatureTypeObj(1, ChangelingFace(), Mine);
        var world = Board(changeling);
        var query = new MtgEngine.Rules.Query.Query(_layers);

        Assert.True(query.Matches(world, new ObjectFilter(Subtypes: ["Goblin"]), changeling.Id, Context));
        Assert.True(query.Matches(world, new ObjectFilter(Subtypes: ["Elf"]), changeling.Id, Context));
        Assert.True(query.Matches(world, new ObjectFilter(Subtypes: ["Time Lord"]), changeling.Id, Context));
        // Equipment is an artifact subtype (CR 205.3g), so being every creature type is not it.
        Assert.False(query.Matches(world, new ObjectFilter(Subtypes: ["Equipment"]), changeling.Id, Context));
        Assert.False(query.Matches(world, new ObjectFilter(Subtypes: ["Aura"]), changeling.Id, Context));
    }

    [Fact]
    public void A_creature_that_is_not_a_changeling_still_only_has_its_printed_types()
    {
        var goblin = CreatureTypeObj(1, CreatureFace("Test Raider", 2, 2, ["Goblin"]), Mine);
        var world = Board(goblin);
        var query = new MtgEngine.Rules.Query.Query(_layers);

        Assert.False(_layers.Of(world, goblin.Id).EveryCreatureType);
        Assert.True(query.Matches(world, new ObjectFilter(Subtypes: ["Goblin"]), goblin.Id, Context));
        Assert.False(query.Matches(world, new ObjectFilter(Subtypes: ["Elf"]), goblin.Id, Context));
    }

    [Fact]
    public void Losing_all_creature_types_keeps_an_equipment_subtype()
    {
        // CR 205.3d: only the creature types go. A living Equipment that stops being a creature
        // is still an Equipment, and its aura-like subtypes are not creature types to lose.
        var living = CreatureTypeObj(1, CreatureFace("Test Blade", 3, 3, ["Equipment", "Golem"], CardType.Artifact | CardType.Creature), Mine);
        var world = Board(living) with
        {
            ActiveEffects = [Active(1, new RemoveTypes(AllCreatureTypes: true), living.Id, Mine, timestamp: 2)],
        };
        var chars = _layers.Of(world, living.Id);

        Assert.Equal("Equipment", Assert.Single(chars.Subtypes));
        Assert.False(chars.EveryCreatureType);

        // Both were there before the effect: the Golem is what "all creature types" means here.
        var printed = _layers.Of(Board(living), living.Id).Subtypes;
        Assert.Contains("Equipment", printed);
        Assert.Contains("Golem", printed);
    }

    [Fact]
    public void A_changeling_that_loses_all_creature_types_has_none()
    {
        var changeling = CreatureTypeObj(1, ChangelingFace(), Mine);
        var world = Board(changeling) with
        {
            // Later than the object's own timestamp, so it is applied after the changeling
            // ability in layer 4 (CR 613.7).
            ActiveEffects = [Active(1, new RemoveTypes(AllCreatureTypes: true), changeling.Id, Mine, timestamp: 5)],
        };
        var query = new MtgEngine.Rules.Query.Query(_layers);
        var chars = _layers.Of(world, changeling.Id);

        Assert.False(chars.EveryCreatureType);
        Assert.Empty(chars.Subtypes);
        Assert.False(query.Matches(world, new ObjectFilter(Subtypes: ["Goblin"]), changeling.Id, Context));
    }

    [Fact]
    public void A_creature_that_is_every_creature_type_shares_a_creature_type_with_a_goblin()
    {
        var changeling = CreatureTypeObj(1, ChangelingFace(), Mine);
        var goblin = CreatureTypeObj(2, CreatureFace("Test Raider", 2, 2, ["Goblin"]), Theirs);
        var equipment = CreatureTypeObj(3, EquipmentFace(), Theirs);
        var world = Board(changeling, goblin, equipment);
        var query = new MtgEngine.Rules.Query.Query(_layers);

        Assert.True(query.Matches(world, SharesACreatureTypeWith(["Goblin"]), changeling.Id, Context));
        Assert.True(query.Matches(world, SharesACreatureTypeWith(["Goblin"]), goblin.Id, Context));
        // Both directions: the goblin shares with the changeling as much as the other way round.
        Assert.True(query.Matches(world, SharesACreatureTypeWith(["Shapeshifter"]), goblin.Id, Context));
        // An Equipment has no creature type at all, so there is nothing to share (CR 205.3m).
        Assert.False(query.Matches(world, SharesACreatureTypeWith(["Equipment"]), changeling.Id, Context));
        Assert.False(query.Matches(world, SharesACreatureTypeWith(["Equipment"]), equipment.Id, Context));
    }

    /// <summary>"... that shares a creature type with" the objects the filter names, CR 205.3m.</summary>
    private static ObjectFilter SharesACreatureTypeWith(IReadOnlyList<string> subtypes) =>
        new(Also: new SharesTypeWith(new Each(new ObjectFilter(Subtypes: subtypes)), CreatureTypeOnly: true));

    private static readonly EffectContext Context = new(
        null,
        Mine,
        [],
        [],
        null,
        ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
        ImmutableDictionary<string, string>.Empty,
        ImmutableDictionary<string, int>.Empty,
        [],
        null);

    private static GameState Board(params GameObject[] objects) => new()
    {
        Players = [new PlayerState(Mine, "A", 20), new PlayerState(Theirs, "B", 20)],
        Objects = objects.ToImmutableDictionary(o => o.Id),
        Zones = ImmutableDictionary<ZoneKey, ImmutableList<ObjectId>>.Empty
            .Add(ZoneKey.Shared(Cards.Zone.Battlefield), [.. objects.Select(o => o.Id)]),
        Turn = new TurnState(1, Mine, Step.PrecombatMain, 0),
    };

    private static ActiveEffect Active(int id, ContinuousEffect effect, ObjectId appliesTo, PlayerId controller, int timestamp) =>
        new(id, effect, null, controller, [appliesTo], [], new UntilEndOfTurn(), timestamp, false);

    private static GameObject CreatureTypeObj(int id, Face face, PlayerId controller, int timestamp = 1) =>
        new(
            new ObjectId(id),
            new CardDefinition($"oracle-{id}", face.Name, Layout.Normal, [face]),
            0,
            controller,
            controller,
            ZoneKey.Shared(Cards.Zone.Battlefield),
            timestamp);

    private static Face CreatureFace(
        string name,
        int power,
        int toughness,
        IReadOnlyList<string> subtypes,
        CardType types = CardType.Creature,
        IReadOnlyList<Ability>? abilities = null) =>
        new(
            name,
            FaceRole.Main,
            new TypeLine(types, Subtypes: subtypes),
            ManaCost: "{1}{G}",
            Power: new PrintedStat(power.ToString(CultureInfo.InvariantCulture)),
            Toughness: new PrintedStat(toughness.ToString(CultureInfo.InvariantCulture)),
            Abilities: abilities);

    /// <summary>
    /// Changeling, CR 702.72a: a characteristic-defining ability that makes the object every
    /// creature type. It is written as the vocabulary writes it, an <see cref="AddTypes"/> with
    /// the flag, applied to the object it is printed on.
    /// </summary>
    private static Face ChangelingFace() =>
        CreatureFace(
            "Test Changeling",
            2,
            2,
            ["Shapeshifter"],
            abilities:
            [
                new StaticAbility(new AddTypes(AllCreatureTypes: true), To: new Self(), CharacteristicDefining: true),
            ]);

    private static Face EquipmentFace() =>
        new(
            "Test Blade",
            FaceRole.Main,
            new TypeLine(CardType.Artifact, Subtypes: ["Equipment"]),
            ManaCost: "{2}");
}
