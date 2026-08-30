using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Events;

/// <summary>
/// Writes a game's log to text and reads it back.
/// </summary>
/// <remarks>
/// This is all persistence needs. The log <em>is</em> the game — state is a fold of it — so
/// storing a game means storing its events, and loading one means replaying them. There is no
/// state schema to design, no migration when state gains a field, and no way for a stored game
/// to disagree with the engine that wrote it.
/// <para>
/// Two things are named explicitly rather than reflected over, both for the same reason: a
/// stored game that cannot be read is a game the players lose.
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Event names.</b> A discriminator taken from the CLR type name would change silently when
/// an event was renamed or moved namespace. The table below is checked against the assembly by a
/// test, so adding an event without registering it fails a build rather than a load.
/// </item>
/// <item>
/// <b>Printed characteristics.</b> Cards are written as the twelve fields the rules engine
/// actually reads, not as whole <see cref="CardDefinition"/>s. Prices, image URIs, flavour text
/// and legalities are not part of a game — they change without the game changing, and a game log
/// is the wrong place to have recorded what a card was worth on the day it was played.
/// </item>
/// </list>
/// <para>
/// Cards are written once into a table and referenced by index, so a sixty-card library costs
/// sixty index numbers rather than sixty copies of a card. Reading rebuilds one instance per
/// table entry, so objects that shared a definition still share one.
/// </para>
/// </remarks>
public static class EventLogSerializer
{
    /// <summary>The format this build writes. Present so a future change can be detected.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Every event that can appear in a log, and the name it is stored under.
    /// </summary>
    /// <remarks>
    /// Names are the type's own, but written down: renaming the type then becomes a deliberate
    /// decision about already-stored games rather than an accident.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, Type> KnownEvents =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["GameStarted"] = typeof(GameStarted),
            ["LibraryShuffled"] = typeof(LibraryShuffled),
            ["ObjectMoved"] = typeof(ObjectMoved),
            ["ObjectCreated"] = typeof(ObjectCreated),
            ["ObjectCeasedToExist"] = typeof(ObjectCeasedToExist),
            ["LifeChanged"] = typeof(LifeChanged),
            ["DrawFromEmptyLibraryAttempted"] = typeof(DrawFromEmptyLibraryAttempted),
            ["TurnBegan"] = typeof(TurnBegan),
            ["StepBegan"] = typeof(StepBegan),
            ["PriorityGranted"] = typeof(PriorityGranted),
            ["PriorityPassed"] = typeof(PriorityPassed),
            ["PriorityWithdrawn"] = typeof(PriorityWithdrawn),
            ["PermanentsUntapped"] = typeof(PermanentsUntapped),
            ["PermanentTapped"] = typeof(PermanentTapped),
            ["PermanentAttached"] = typeof(PermanentAttached),
            ["RegenerationShieldsChanged"] = typeof(RegenerationShieldsChanged),
            ["LookAtTopRequested"] = typeof(LookAtTopRequested),
            ["PoisonCountersChanged"] = typeof(PoisonCountersChanged),
            ["PreventionChanged"] = typeof(PreventionChanged),
            ["PreventionEffectCreated"] = typeof(PreventionEffectCreated),
            ["PlayerPreventionChanged"] = typeof(PlayerPreventionChanged),
            ["CardManifested"] = typeof(CardManifested),
            ["ConniveRequested"] = typeof(ConniveRequested),
            ["ManifestDreadRequested"] = typeof(ManifestDreadRequested),
            ["PopulateRequested"] = typeof(PopulateRequested),
            ["ExploitRequested"] = typeof(ExploitRequested),
            ["SoulbondPairRequested"] = typeof(SoulbondPairRequested),
            ["CreaturesPaired"] = typeof(CreaturesPaired),
            ["CreaturesUnpaired"] = typeof(CreaturesUnpaired),
            ["DiscoverRequested"] = typeof(DiscoverRequested),
            ["RingTempted"] = typeof(RingTempted),
            ["RingBearerChosen"] = typeof(RingBearerChosen),
            ["RingBearerRequested"] = typeof(RingBearerRequested),
            ["VentureMarkerMoved"] = typeof(VentureMarkerMoved),
            ["VentureRoomRequested"] = typeof(VentureRoomRequested),
            ["DungeonCompleted"] = typeof(DungeonCompleted),
            ["ClashRequested"] = typeof(ClashRequested),
            ["ClashRevealed"] = typeof(ClashRevealed),
            ["CardPutOnBottom"] = typeof(CardPutOnBottom),
            ["CardMayBePlayed"] = typeof(CardMayBePlayed),
            ["PlayWindowClosed"] = typeof(PlayWindowClosed),
            ["ExtraLandDropGranted"] = typeof(ExtraLandDropGranted),
            ["SuspendOnResolveRequested"] = typeof(SuspendOnResolveRequested),
            ["SacrificeUnlessPaidRequested"] = typeof(SacrificeUnlessPaidRequested),
            ["CipherRequested"] = typeof(CipherRequested),
            ["SpellEncoded"] = typeof(SpellEncoded),
            ["LibraryEndChoiceRequested"] = typeof(LibraryEndChoiceRequested),
            ["SpellPrototyped"] = typeof(SpellPrototyped),
            ["FreerunningEnabled"] = typeof(FreerunningEnabled),
            ["HandLookedAt"] = typeof(HandLookedAt),
            ["RedirectionChanged"] = typeof(RedirectionChanged),
            ["ExtraTurnCreated"] = typeof(ExtraTurnCreated),
            ["ExtraTurnTaken"] = typeof(ExtraTurnTaken),
            ["ManaMadePersistent"] = typeof(ManaMadePersistent),
            ["ManaPersistenceEnded"] = typeof(ManaPersistenceEnded),
            ["BecamePrepared"] = typeof(BecamePrepared),
            ["Unprepared"] = typeof(Unprepared),
            ["SpellEscaped"] = typeof(SpellEscaped),
            ["SpellWarped"] = typeof(SpellWarped),
            ["CardWarpedToExile"] = typeof(CardWarpedToExile),
            ["CreatureExploited"] = typeof(CreatureExploited),
            ["DiscardRequested"] = typeof(DiscardRequested),
            ["OptionalPaymentRequested"] = typeof(OptionalPaymentRequested),
            ["DelayedTriggerCreated"] = typeof(DelayedTriggerCreated),
            ["DelayedTriggerFired"] = typeof(DelayedTriggerFired),
            ["LookAndTakeRequested"] = typeof(LookAndTakeRequested),
            ["LibrarySearchRequested"] = typeof(LibrarySearchRequested),
            ["SeekRequested"] = typeof(SeekRequested),
            ["ModesChosen"] = typeof(ModesChosen),
            ["CardsSpliced"] = typeof(CardsSpliced),
            ["SpellSquadded"] = typeof(SpellSquadded),
            ["SpellKicked"] = typeof(SpellKicked),
            ["SpellCleaved"] = typeof(SpellCleaved),
            ["GiftPromised"] = typeof(GiftPromised),
            ["ProliferateRequested"] = typeof(ProliferateRequested),
            ["ChoosePermanentRequested"] = typeof(ChoosePermanentRequested),
            ["CoinFlipRequested"] = typeof(CoinFlipRequested),
            ["DiceRollRequested"] = typeof(DiceRollRequested),
            ["DiceRolled"] = typeof(DiceRolled),
            ["PermanentTurned"] = typeof(PermanentTurned),
            ["EnergyChanged"] = typeof(EnergyChanged),
            ["ExperienceCountersChanged"] = typeof(ExperienceCountersChanged),
            ["SpeedChanged"] = typeof(SpeedChanged),
            ["SpellDashed"] = typeof(SpellDashed),
            ["SpellBlitzed"] = typeof(SpellBlitzed),
            ["CitysBlessingGained"] = typeof(CitysBlessingGained),
            ["MonarchChanged"] = typeof(MonarchChanged),
            ["InitiativeTaken"] = typeof(InitiativeTaken),
            ["PermanentTransformed"] = typeof(PermanentTransformed),
            ["DayNightChanged"] = typeof(DayNightChanged),
            ["PermanentSaddled"] = typeof(PermanentSaddled),
            ["SpellBargained"] = typeof(SpellBargained),
            ["SpellMultikicked"] = typeof(SpellMultikicked),
            ["SpellKickedWith"] = typeof(SpellKickedWith),
            ["SpellEvoked"] = typeof(SpellEvoked),
            ["SpellOverloaded"] = typeof(SpellOverloaded),
            ["SpellAwakened"] = typeof(SpellAwakened),
            ["SpellTeamwork"] = typeof(SpellTeamwork),
            ["RippleRequested"] = typeof(RippleRequested),
            ["PermanentPhasedOut"] = typeof(PermanentPhasedOut),
            ["RemovedFromCombat"] = typeof(RemovedFromCombat),
            ["PermanentPhasedIn"] = typeof(PermanentPhasedIn),
            ["SpellSneaked"] = typeof(SpellSneaked),
            ["SpellBestowed"] = typeof(SpellBestowed),
            ["SpellMutating"] = typeof(SpellMutating),
            ["SpellMutationLapsed"] = typeof(SpellMutationLapsed),
            ["PermanentMutated"] = typeof(PermanentMutated),
            ["MergedPermanentSeparated"] = typeof(MergedPermanentSeparated),
            ["SpellOffspring"] = typeof(SpellOffspring),
            ["DamageRemoved"] = typeof(DamageRemoved),
            ["UntapSkipped"] = typeof(UntapSkipped),
            ["CardsRevealed"] = typeof(CardsRevealed),
            ["CardSuspended"] = typeof(CardSuspended),
            ["CardPlotted"] = typeof(CardPlotted),
            ["JoinedCombat"] = typeof(JoinedCombat),
            ["ShuffleRequested"] = typeof(ShuffleRequested),
            ["TimeCounterRemoved"] = typeof(TimeCounterRemoved),
            ["SpellCopied"] = typeof(SpellCopied),
            ["ExiledUntilLeaves"] = typeof(ExiledUntilLeaves),
            ["HandChoiceRequested"] = typeof(HandChoiceRequested),
            ["ColorChoiceRequested"] = typeof(ColorChoiceRequested),
            ["ManaColorChoiceRequested"] = typeof(ManaColorChoiceRequested),
            ["CreatureTypeChoiceRequested"] = typeof(CreatureTypeChoiceRequested),
            ["UntapChoiceRequested"] = typeof(UntapChoiceRequested),
            ["CounterChoiceRequested"] = typeof(CounterChoiceRequested),
            ["LibraryOrderRequested"] = typeof(LibraryOrderRequested),
            ["LibraryOrdered"] = typeof(LibraryOrdered),
            ["SpellBoughtBack"] = typeof(SpellBoughtBack),
            ["CardForetold"] = typeof(CardForetold),
            ["FreeCastOffered"] = typeof(FreeCastOffered),
            ["FreeCastLapsed"] = typeof(FreeCastLapsed),
            ["ProtectorChosen"] = typeof(ProtectorChosen),
            ["CascadeRequested"] = typeof(CascadeRequested),
            ["CoinFlipped"] = typeof(CoinFlipped),
            ["SummoningSicknessCleared"] = typeof(SummoningSicknessCleared),
            ["LandDropUsed"] = typeof(LandDropUsed),
            ["SpellCastEvent"] = typeof(SpellCastEvent),
            ["StackObjectResolved"] = typeof(StackObjectResolved),
            ["DamageCleared"] = typeof(DamageCleared),
            ["PlayerLost"] = typeof(PlayerLost),
            ["GameEnded"] = typeof(GameEnded),
            ["DamageMarked"] = typeof(DamageMarked),
            ["HalfUnlocked"] = typeof(HalfUnlocked),
            ["WentOnAdventure"] = typeof(WentOnAdventure),
            ["BecameRenowned"] = typeof(BecameRenowned),
            ["CaseSolved"] = typeof(CaseSolved),
            ["ClassLevelChanged"] = typeof(ClassLevelChanged),
            ["CountersChanged"] = typeof(CountersChanged),
            ["AbilityTriggered"] = typeof(AbilityTriggered),
            ["TriggerPutOnStack"] = typeof(TriggerPutOnStack),
            ["TriggerRemovedForNoTargets"] = typeof(TriggerRemovedForNoTargets),
            ["ContinuousEffectCreated"] = typeof(ContinuousEffectCreated),
            ["ContinuousEffectEnded"] = typeof(ContinuousEffectEnded),
            ["EventReplaced"] = typeof(EventReplaced),
            ["AttackersDeclared"] = typeof(AttackersDeclared),
            ["BlockersDeclared"] = typeof(BlockersDeclared),
            ["PlayerDamaged"] = typeof(PlayerDamaged),
            ["CombatDamageDealt"] = typeof(CombatDamageDealt),
            ["BecameMonstrous"] = typeof(BecameMonstrous),
            ["ManaColorsSpent"] = typeof(ManaColorsSpent),
            ["CombatDamageStepDone"] = typeof(CombatDamageStepDone),
            ["CombatEnded"] = typeof(CombatEnded),
            ["NothingHappened"] = typeof(NothingHappened),
            ["ManaAdded"] = typeof(ManaAdded),
            ["StateTriggerArmed"] = typeof(StateTriggerArmed),
            ["CharacteristicChosen"] = typeof(CharacteristicChosen),
            ["ManaSpent"] = typeof(ManaSpent),
            ["ManaPoolsEmptied"] = typeof(ManaPoolsEmptied),
            ["TargetsChosen"] = typeof(TargetsChosen),
            ["FizzledForIllegalTargets"] = typeof(FizzledForIllegalTargets),
            ["AbilityActivated"] = typeof(AbilityActivated),
            ["ChoiceRequested"] = typeof(ChoiceRequested),
            ["ChoiceMade"] = typeof(ChoiceMade),
            ["MulligansBegan"] = typeof(MulligansBegan),
            ["MulliganKept"] = typeof(MulliganKept),
            ["MulliganTaken"] = typeof(MulliganTaken),
            ["MulligansFinished"] = typeof(MulligansFinished),
            ["OpeningHandActionsTaken"] = typeof(OpeningHandActionsTaken),
            ["CommanderDesignated"] = typeof(CommanderDesignated),
            ["CommanderCastFromCommandZone"] = typeof(CommanderCastFromCommandZone),
            ["CommanderDamageDealt"] = typeof(CommanderDamageDealt),
        };

    /// <summary>Writes a log as one JSON document.</summary>
    public static string Write(IEnumerable<GameEvent> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        var cards = new CardTable();
        var events = JsonSerializer.Serialize(log.ToList(), OptionsFor(cards));

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", CurrentVersion);

            writer.WritePropertyName("cards");
            JsonSerializer.Serialize(writer, cards.Written, PlainOptions);

            // Written last because it is the only part whose size grows with the game; a reader
            // has the card table in hand before it meets the first index into it.
            writer.WritePropertyName("events");
            using (var parsed = JsonDocument.Parse(events))
                parsed.RootElement.WriteTo(writer);

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads a log back. Throws rather than skipping anything it does not recognise.
    /// </summary>
    /// <remarks>
    /// A log containing an event this build cannot read is not a game that can be resumed
    /// correctly. Dropping the line would produce a game that folds to a subtly different
    /// position — a wrong game presented as the players' own, which is worse than refusing it.
    /// </remarks>
    public static IReadOnlyList<GameEvent> Read(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("version", out var version) || version.GetInt32() > CurrentVersion)
            throw new InvalidOperationException("That game was stored by a newer build.");

        var printed = JsonSerializer.Deserialize<List<PrintedCard>>(
            root.GetProperty("cards").GetRawText(), PlainOptions) ?? [];

        var cards = new CardTable([.. printed.Select(p => p.ToDefinition())]);

        return JsonSerializer.Deserialize<List<GameEvent>>(
                   root.GetProperty("events").GetRawText(), OptionsFor(cards))
               ?? throw new InvalidOperationException("That is not a game log.");
    }

    private static readonly JsonSerializerOptions PlainOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <remarks>
    /// Built per call because the card table is per document. Saving a game is not a hot path,
    /// and the alternative — a shared table — would leak one game's cards into another's.
    /// </remarks>
    private static JsonSerializerOptions OptionsFor(CardTable cards) => new()
    {
        // Enums as names, not numbers: reordering an enum must not quietly turn every stored
        // "Graveyard" into "Exile".
        Converters = { new JsonStringEnumConverter(), new ObjectIdConverter(), cards },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { AddEventDiscriminators },
        },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static void AddEventDiscriminators(JsonTypeInfo info)
    {
        if (info.Type != typeof(GameEvent))
            return;

        info.PolymorphismOptions = new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = "$event",
            IgnoreUnrecognizedTypeDiscriminators = false,
            UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
        };

        foreach (var (name, type) in KnownEvents)
            info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(type, name));
    }

    /// <summary>
    /// An object id as a bare GUID, including when it is a dictionary key.
    /// </summary>
    /// <remarks>
    /// Without the property-name half, <c>ImmutableDictionary&lt;ObjectId, …&gt;</c> — which is
    /// how attackers and blockers are recorded — cannot be written at all.
    /// </remarks>
    private sealed class ObjectIdConverter : JsonConverter<ObjectId>
    {
        public override ObjectId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions o) =>
            new(reader.GetGuid());

        public override void Write(Utf8JsonWriter writer, ObjectId value, JsonSerializerOptions o) =>
            writer.WriteStringValue(value.Value);

        public override ObjectId ReadAsPropertyName(
            ref Utf8JsonReader reader, Type type, JsonSerializerOptions o) =>
            new(Guid.Parse(reader.GetString()!));

        public override void WriteAsPropertyName(
            Utf8JsonWriter writer, ObjectId value, JsonSerializerOptions o) =>
            writer.WritePropertyName(value.Value.ToString("D"));
    }

    /// <summary>
    /// Cards, written once and referenced by index.
    /// </summary>
    /// <remarks>
    /// Identity is by instance rather than by oracle id: two definitions sharing an oracle id but
    /// differing in printed characteristics are different cards to the engine, and collapsing
    /// them would quietly change a stored game's board.
    /// </remarks>
    private sealed class CardTable(IReadOnlyList<CardDefinition>? loaded = null)
        : JsonConverter<CardDefinition>
    {
        private readonly Dictionary<CardDefinition, int> _indices =
            new((IEqualityComparer<CardDefinition>)ReferenceEqualityComparer.Instance);

        private readonly List<PrintedCard> _written = [];

        public IReadOnlyList<PrintedCard> Written => _written;

        public override CardDefinition Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions o)
        {
            var index = reader.GetInt32();

            if (loaded is null || index < 0 || index >= loaded.Count)
                throw new InvalidOperationException("That game log refers to a card it does not carry.");

            return loaded[index];
        }

        public override void Write(Utf8JsonWriter writer, CardDefinition value, JsonSerializerOptions o)
        {
            if (!_indices.TryGetValue(value, out var index))
            {
                index = _written.Count;
                _indices[value] = index;
                _written.Add(PrintedCard.Of(value));
            }

            writer.WriteNumberValue(index);
        }
    }

    /// <summary>
    /// A card as a game log records it: what it is, not what it looks like or costs to buy.
    /// </summary>
    private sealed record PrintedCard(
        string OracleId,
        string Name,
        string ManaCostRaw,
        int Cmc,
        CardType CardTypes,
        IReadOnlyList<string> Subtypes,
        IReadOnlyList<string> Supertypes,
        string OracleText,
        int? Power,
        int? Toughness,
        int? StartingLoyalty,

        // A battle's printed defense (CR 310.4a). Without it a stored game rebuilt every battle
        // with no number to enter with, so the replacement that puts its defense counters on
        // (CR 310.4b) silently put none.
        int? Defense,
        KeywordAbility Keywords,
        IReadOnlyList<ManaColor> ColorIdentity,
        IReadOnlyList<ManaColor> Colors,

        // Without this a stored game forgot every transform. A re-read card had no faces, so
        // GameReducer.Transform - which correctly refuses a face index the card does not have -
        // dropped every PermanentTransformed event in the log, and a saved game came back with its
        // werewolves on their day faces and unable to flip again. 837 corpus cards carry faces.
        //
        // CardFace is already only what the compiler reads - no oracle id, no prices, no images -
        // so it travels whole rather than being trimmed into a second shape that could drift.
        IReadOnlyList<CardFace> Faces)
    {
        public static PrintedCard Of(CardDefinition card) => new(
            card.OracleId,
            card.Name,
            card.ManaCostRaw,
            card.Cmc,
            card.CardTypes,
            card.Subtypes,
            card.Supertypes,
            card.OracleText,
            card.Power,
            card.Toughness,
            card.StartingLoyalty,
            card.Defense,
            card.Keywords,
            card.ColorIdentity,
            card.Colors,
            card.Faces);

        public CardDefinition ToDefinition() => new()
        {
            OracleId = OracleId,
            Name = Name,
            ManaCostRaw = ManaCostRaw,
            Cmc = Cmc,
            CardTypes = CardTypes,
            Subtypes = [.. Subtypes],
            Supertypes = [.. Supertypes],
            OracleText = OracleText,
            Power = Power,
            Toughness = Toughness,
            StartingLoyalty = StartingLoyalty,
            Defense = Defense,
            Keywords = Keywords,
            ColorIdentity = [.. ColorIdentity],
            Colors = [.. Colors],
            Faces = [.. Faces],
        };
    }
}
