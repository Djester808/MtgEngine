using MtgEngine.Cards;
using MtgEngine.Rules.Faces;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// Faces and layouts, CR 707–715 and 720, on made-up definitions built here: no card name and
/// no card file takes part. Every test names the rule it holds the engine to.
/// </summary>
public sealed class FaceTests
{
    private static readonly PlayerId Mine = new(0);

    // ---- CR 712.8: which face a double-faced card has, and where ----

    [Fact]
    [RuleRef("712.8a")]
    public void A_transforming_card_is_its_front_face_in_hand_and_library()
    {
        var card = Transformer();

        Assert.Equal(0, FaceRules.ActiveFace(card, Zone.Hand, 0));
        Assert.Equal(0, FaceRules.ActiveFace(card, Zone.Library, 0));
        // CR 712.8a: even with the back face recorded as up, no zone but the battlefield or the
        // stack shows it. A card shuffled away mid-transform is its front face again.
        Assert.Equal(0, FaceRules.ActiveFace(card, Zone.Graveyard, 1));
        Assert.Equal(0, FaceRules.ActiveFace(card, Zone.Exile, 1));
    }

    [Fact]
    [RuleRef("712.8e")]
    public void A_transforming_card_is_its_back_face_after_transforming_on_the_battlefield()
    {
        var card = Transformer();

        var turned = FaceRules.Transform(card, 0);

        Assert.True(turned.Turned);
        Assert.Equal(1, turned.FaceIndex);
        Assert.Equal(1, FaceRules.ActiveFace(card, Zone.Battlefield, turned.FaceIndex));
        Assert.Equal("Back", card.Faces[FaceRules.ActiveFace(card, Zone.Battlefield, turned.FaceIndex)].Name);
        // CR 701.27a: turning it over again puts the front face up.
        Assert.Equal(0, FaceRules.Transform(card, 1).FaceIndex);
    }

    [Fact]
    [RuleRef("712.10")]
    public void A_permanent_does_not_transform_into_an_instant_or_sorcery_face()
    {
        var card = new CardDefinition("t-instant-back", "Front", Layout.Transform,
            [Creature("Front", 1, 1, FaceRole.Main), Spell("Back", CardType.Sorcery, FaceRole.Back)]);

        var turned = FaceRules.Transform(card, 0);

        Assert.False(turned.Turned);
        Assert.Equal(0, turned.FaceIndex);
        Assert.Equal("712.10", turned.Rule);
    }

    [Fact]
    [RuleRef("712.4c")]
    public void A_meld_card_does_not_transform()
    {
        var turned = FaceRules.Transform(MeldPart("m-left", "Left"), 0);

        Assert.False(turned.Turned);
        Assert.Equal("712.4c", turned.Rule);
    }

    [Fact]
    [RuleRef("701.28a")]
    public void Convert_is_transform_under_another_word()
    {
        var card = Transformer();

        Assert.Equal(FaceRules.Transform(card, 0), FaceRules.Convert(card, 0));
    }

    // ---- CR 712.11b: a modal double-faced card cast as its back face ----

    [Fact]
    [RuleRef("712.11b")]
    public void A_modal_double_faced_card_cast_as_its_back_face_is_that_face_on_the_stack_and_battlefield()
    {
        var card = Modal();

        var options = FaceRules.CastableFaces(card, Zone.Hand);

        Assert.Equal(2, options.Count);
        Assert.Equal(new[] { 0, 1 }, options.Select(o => o.FaceIndex));
        // CR 712.8f: on the stack and on the battlefield it is the face that is up.
        Assert.Equal(1, FaceRules.ActiveFace(card, Zone.Stack, 1));
        Assert.Equal(1, FaceRules.ActiveFace(card, Zone.Battlefield, 1));
        // CR 712.8a: in hand it is still the front face, whichever face was chosen.
        Assert.Equal(0, FaceRules.ActiveFace(card, Zone.Hand, 1));
    }

    [Fact]
    [RuleRef("712.12")]
    public void A_modal_land_face_is_flagged_as_played_rather_than_cast()
    {
        var card = new CardDefinition("m-land", "Front", Layout.ModalDfc,
            [Creature("Front", 2, 2, FaceRole.Main), Spell("Back", CardType.Land, FaceRole.Back)]);

        var options = FaceRules.CastableFaces(card, Zone.Hand);

        Assert.False(options[0].IsLandFace);
        Assert.True(options[1].IsLandFace);
    }

    // ---- CR 709: split cards ----

    [Fact]
    [RuleRef("709.3")]
    public void Both_halves_of_a_split_card_are_castable()
    {
        var card = Split();

        var options = FaceRules.CastableFaces(card, Zone.Hand);

        Assert.Equal(2, options.Count);
        Assert.Equal(new[] { FaceRole.Left, FaceRole.Right }, options.Select(o => o.Role));
        Assert.All(options, o => Assert.Equal("709.3", o.Rule));
    }

    [Fact]
    [RuleRef("709.4")]
    public void A_split_card_is_both_halves_combined_outside_the_stack()
    {
        var card = Split();

        Assert.Equal(new[] { 0, 1 }, FaceRules.ActiveFaces(card, Zone.Hand, 0));
        Assert.True(FaceRules.IsCombined(card, Zone.Graveyard));
        // CR 709.3b: on the stack only the half being cast exists.
        Assert.Equal(new[] { 1 }, FaceRules.ActiveFaces(card, Zone.Stack, 1));
        Assert.False(FaceRules.IsCombined(card, Zone.Stack));
    }

    // ---- CR 715: adventures ----

    [Fact]
    [RuleRef("715.3d")]
    public void An_adventure_cast_as_its_adventure_goes_to_exile_and_may_be_cast_later()
    {
        var card = Adventurer();

        var options = FaceRules.CastableFaces(card, Zone.Hand);
        var adventure = options.Single(o => o.IsAlternative);
        var resolution = FaceRules.ResolvesTo(card, adventure.FaceIndex);

        Assert.Equal(2, options.Count);
        Assert.Equal(Zone.Exile, resolution.Zone);
        Assert.True(resolution.MayCastLater);
        // CR 715.3b: on the stack as an Adventure it has only the alternative characteristics.
        Assert.Equal(adventure.FaceIndex, FaceRules.ActiveFace(card, Zone.Stack, adventure.FaceIndex));
        // CR 715.4: in exile it has its normal characteristics again.
        Assert.Equal(0, FaceRules.ActiveFace(card, Zone.Exile, adventure.FaceIndex));
        // CR 715.3d: it may be played from exile, but not as an Adventure a second time.
        var fromExile = FaceRules.CastableFaces(card, Zone.Exile, onAdventure: true);
        Assert.Equal(0, Assert.Single(fromExile).FaceIndex);
    }

    [Fact]
    [RuleRef("720.3d")]
    public void An_omen_resolves_into_its_owners_library()
    {
        var card = new CardDefinition("o-1", "Front", Layout.Omen,
            [Creature("Front", 2, 2, FaceRole.Main), Spell("Omen half", CardType.Instant, FaceRole.Omen)]);

        var resolution = FaceRules.ResolvesTo(card, 1);

        Assert.Equal(Zone.Library, resolution.Zone);
        Assert.False(resolution.MayCastLater);
    }

    [Fact]
    [RuleRef("608.2m")]
    public void An_ordinary_spell_resolves_into_the_graveyard()
    {
        var resolution = FaceRules.ResolvesTo(Transformer(), 0);

        Assert.Equal(Zone.Graveyard, resolution.Zone);
        Assert.False(resolution.MayCastLater);
    }

    // ---- CR 710: flip cards ----

    [Fact]
    [RuleRef("710.2")]
    public void A_flip_card_is_its_alternative_face_only_once_flipped_on_the_battlefield()
    {
        var card = Flipper();

        var flipped = FaceRules.Flip(card, 0);

        Assert.True(flipped.Turned);
        Assert.Equal(1, flipped.FaceIndex);
        Assert.Equal(1, FaceRules.ActiveFace(card, Zone.Battlefield, 1));
        // CR 710.2: before it flips, and in every other zone, it is the normal face.
        Assert.Equal(0, FaceRules.ActiveFace(card, Zone.Battlefield, 0));
        Assert.Equal(0, FaceRules.ActiveFace(card, Zone.Graveyard, 1));
    }

    [Fact]
    [RuleRef("710.4")]
    public void Flipping_is_one_way()
    {
        var again = FaceRules.Flip(Flipper(), 1);

        Assert.False(again.Turned);
        Assert.Equal("710.4", again.Rule);
    }

    // ---- CR 701.42: meld ----

    [Fact]
    [RuleRef("701.42a")]
    public void Two_cards_of_a_meld_pair_meld_into_one_object()
    {
        var melded = FaceRules.Meld(MeldPart("m-left", "Left"), MeldPart("m-right", "Right"));

        Assert.True(melded.Success);
        Assert.NotNull(melded.Combined);
        Assert.Equal("Combined", melded.Combined!.Faces[melded.FaceIndex].Name);
        // CR 712.8g: on the battlefield the melded permanent has only the combined back face.
        Assert.Equal(melded.FaceIndex, FaceRules.ActiveFace(melded.Combined, Zone.Battlefield, melded.FaceIndex));
        // CR 712.8b: a meld card on the stack has only its front face.
        Assert.Equal(0, FaceRules.ActiveFace(melded.Combined, Zone.Stack, melded.FaceIndex));
    }

    [Fact]
    [RuleRef("701.42b")]
    public void Cards_that_are_not_a_meld_pair_do_not_meld()
    {
        var odd = new CardDefinition("m-odd", "Odd", Layout.Meld,
            [Creature("Odd", 1, 1, FaceRole.Main), Creature("Another combined", 8, 8, FaceRole.MeldResult)]);

        Assert.False(FaceRules.Meld(MeldPart("m-left", "Left"), odd).Success);
        Assert.False(FaceRules.Meld(MeldPart("m-left", "Left"), Transformer()).Success);
    }

    // ---- CR 708.2: face down ----

    [Fact]
    [RuleRef("708.2a")]
    public void A_face_down_permanent_is_a_two_two_colourless_creature_with_nothing_else()
    {
        var face = FaceRules.FaceDown;

        Assert.Equal(string.Empty, face.Name);
        Assert.Equal(CardType.Creature, face.Types.Types);
        Assert.Equal(Supertype.None, face.Types.Supertypes);
        Assert.Empty(face.Types.SubtypeList);
        Assert.Equal(2, face.Power!.Number);
        Assert.Equal(2, face.Toughness!.Number);
        Assert.Empty(face.AbilityList);
        // No mana cost and no color indicator, so CR 202.2 leaves it colorless.
        Assert.Null(face.ManaCost);
        Assert.Null(face.ColorIndicator);
        Assert.Null(face.OracleText);
    }

    // ---- CR 707.2: copies ----

    [Fact]
    [RuleRef("707.2")]
    public void CopyOf_drops_counters_damage_tapped_status_and_the_rest_of_status()
    {
        var battlefield = ZoneKey.Of(Zone.Battlefield, Mine);
        var original = new GameObject(new ObjectId(1), Transformer(), 1, Mine, Mine, battlefield, 1)
        {
            Tapped = true,
            Damage = 3,
            PhasedOut = true,
            DealtDeathtouchDamage = true,
            SummoningSick = false,
            Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 2),
            Designations = ["monstrous"],
        };

        var values = CopyRules.CopyOf(original);
        var token = CopyRules.NewCopy(values, new ObjectId(2), Mine, Mine, battlefield, 2);

        // Copiable: the definition and the face that is up, CR 707.2 and 712.8e.
        Assert.Equal(original.Definition, token.Definition);
        Assert.Equal(1, token.FaceIndex);
        // Not copiable, CR 707.2: status, counters, damage.
        Assert.False(token.Tapped);
        Assert.Equal(0, token.Damage);
        Assert.False(token.PhasedOut);
        Assert.False(token.DealtDeathtouchDamage);
        Assert.Empty(token.Counters);
        Assert.Empty(token.Designations);
        Assert.True(token.IsToken);
    }

    [Fact]
    [RuleRef("708.2")]
    public void A_copy_of_a_face_down_permanent_is_face_down_too()
    {
        var battlefield = ZoneKey.Of(Zone.Battlefield, Mine);
        var hidden = new GameObject(new ObjectId(1), Transformer(), 0, Mine, Mine, battlefield, 1) { FaceDown = true };

        Assert.True(CopyRules.CopyOf(hidden).FaceDown);
    }

    [Fact]
    [RuleRef("707.2")]
    public void A_permanent_that_becomes_a_copy_keeps_its_own_counters_and_damage()
    {
        var battlefield = ZoneKey.Of(Zone.Battlefield, Mine);
        var clone = new GameObject(new ObjectId(1), Flipper(), 0, Mine, Mine, battlefield, 1)
        {
            Damage = 1,
            Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 1),
        };

        var copied = CopyRules.Become(clone, CopyRules.CopyOf(Transformer(), 1));

        Assert.Equal(1, copied.FaceIndex);
        Assert.Equal(Layout.Transform, copied.Definition.Layout);
        // CR 707.2: the copy's own counters and damage were never part of what was copied.
        Assert.Equal(1, copied.Damage);
        Assert.Equal(1, copied.Counters["+1/+1"]);
    }

    // ---- made-up definitions ----

    private static CardDefinition Transformer() => new("t-1", "Front", Layout.Transform,
        [Creature("Front", 2, 2, FaceRole.Main), Creature("Back", 3, 3, FaceRole.Back)]);

    private static CardDefinition Modal() => new("md-1", "Front", Layout.ModalDfc,
        [Creature("Front", 2, 2, FaceRole.Main), Creature("Back", 4, 4, FaceRole.Back)]);

    private static CardDefinition Split() => new("s-1", "Left half", Layout.Split,
        [Spell("Left half", CardType.Instant, FaceRole.Left), Spell("Right half", CardType.Sorcery, FaceRole.Right)]);

    private static CardDefinition Adventurer() => new("a-1", "Front", Layout.Adventure,
        [Creature("Front", 2, 2, FaceRole.Main), Spell("The adventure", CardType.Instant, FaceRole.Adventure)]);

    private static CardDefinition Flipper() => new("f-1", "Upright", Layout.Flip,
        [Creature("Upright", 1, 1, FaceRole.Main), Creature("Upside down", 3, 3, FaceRole.Flipped)]);

    private static CardDefinition MeldPart(string oracleId, string name) => new(oracleId, name, Layout.Meld,
        [Creature(name, 2, 2, FaceRole.Main), Creature("Combined", 7, 7, FaceRole.MeldResult)]);

    private static Face Creature(string name, int power, int toughness, FaceRole role) => new(
        Name: name,
        Role: role,
        Types: new TypeLine(CardType.Creature),
        ManaCost: "{1}{G}",
        Power: new PrintedStat(power.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Toughness: new PrintedStat(toughness.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static Face Spell(string name, CardType type, FaceRole role) => new(
        Name: name,
        Role: role,
        Types: new TypeLine(type),
        ManaCost: type == CardType.Land ? null : "{1}{U}");
}
