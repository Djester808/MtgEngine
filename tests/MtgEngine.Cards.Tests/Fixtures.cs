namespace MtgEngine.Cards.Tests;

/// <summary>Made-up cards in the vocabulary, for round trips and the linter. No real card is named here.</summary>
internal static class Fixtures
{
    /// <summary>An Equipment: equipped creature gets +1/-1; whenever it dies, draw two; Equip {1}.</summary>
    public static CardDefinition Clamp() => new(
        OracleId: "00000000-0000-0000-0000-000000000001",
        Name: "Test Clamp",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Test Clamp",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Artifact, Subtypes: ["Equipment"]),
                ManaCost: "{1}",
                Abilities:
                [
                    new StaticAbility(new ModifyPt(new Fixed(1), new Fixed(-1)), To: new Attached()) { Text = "Equipped creature gets +1/-1." },
                    // CR 301.5c: "equipped creature" is the creature this Equipment is attached to, said in a
                    // filter as IsOneOf(Attached). AttachedTo(Self) would name the Auras on this Equipment.
                    new TriggeredAbility(new Dies(new ObjectFilter(Also: new IsOneOf(new Attached()))), new Draw(new Fixed(2))) { Text = "Whenever equipped creature dies, draw two cards." },
                    new KeywordAbility(KeywordKind.Equip, Cost: "{1}") { Text = "Equip {1}" },
                ],
                OracleText: "Equipped creature gets +1/-1.\nWhenever equipped creature dies, draw two cards.\nEquip {1}")
        ]);

    /// <summary>A sorcery: destroy target permanent; its controller creates a 3/3 green Beast.</summary>
    public static CardDefinition Within() => new(
        OracleId: "00000000-0000-0000-0000-000000000002",
        Name: "Test Within",
        Layout: Layout.Normal,
        Faces:
        [
            new Face(
                Name: "Test Within",
                Role: FaceRole.Main,
                Types: new TypeLine(CardType.Instant),
                ManaCost: "{2}{G}",
                Abilities:
                [
                    new SpellAbility(new Sequence(
                    [
                        new Destroy(new Target(new ObjectFilter(Permanent: true), Key: "it")),
                        new CreateToken(
                            new TokenSpec("Beast", Color.Green, CardType.Creature, ["Beast"], Power: 3, Toughness: 3),
                            Who: new ControllerOf(new Remembered("it"))),
                    ])),
                ])
        ]);

    public static CardFile FileFor(CardDefinition definition, ImplementationStatus status = ImplementationStatus.Translated, IReadOnlyList<string>? scryfallKeywords = null) => new(
        OracleId: definition.OracleId,
        Name: definition.Name,
        OracleHash: OracleHashing.Of(definition.Faces.Select(f => f.OracleText)),
        IrVersion: IrVersion.Current,
        Definition: definition,
        Status: status,
        Source: new TranslatedProvenance(["model-a", "model-b"], "test", Agreed: true),
        ScryfallKeywords: scryfallKeywords);
}
