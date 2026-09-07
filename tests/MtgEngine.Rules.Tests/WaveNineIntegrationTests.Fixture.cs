using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A real table for the wave-9 and wave-10 joins: <see cref="Subsystems.Wave2"/> composed, made-up
/// definitions that exercise keyed mana (CR 106.6a), a tapped-for-mana trigger reading a chosen
/// colour (CR 106.12a with 608.2d), a may-pay inside a resolution (CR 118.12), a spend restriction
/// by source (CR 106.6b), an instructed cast whose cost names objects (CR 601.2c) and the player
/// prohibitions (CR 613.10). Nothing here fakes a subsystem, so a join that only holds against a
/// fake fails here. Every card below is invented.
/// </summary>
internal sealed class WaveNineIntegrationTable
{
    internal const string GroveName = "Wave Nine Grove";
    internal const string ValeName = "Wave Nine Vale";
    internal const string KeyedGroveName = "Wave Nine Keyed Grove";
    internal const string OxName = "Wave Nine Ox";
    internal const string PrismName = "Wave Nine Prism";
    internal const string ScholarName = "Wave Nine Scholar";
    internal const string RockName = "Wave Nine Rock";
    internal const string GateName = "Wave Nine Gate";
    internal const string TollName = "Wave Nine Toll";
    internal const string RiteName = "Wave Nine Rite";
    internal const string BoltName = "Wave Nine Bolt";
    internal const string StasisName = "Wave Nine Stasis";
    internal const string AegisName = "Wave Nine Aegis";

    internal const string GroveId = "wave-nine-grove";
    internal const string ValeId = "wave-nine-vale";
    internal const string KeyedGroveId = "wave-nine-keyed-grove";
    internal const string OxId = "wave-nine-ox";
    internal const string PrismId = "wave-nine-prism";
    internal const string ScholarId = "wave-nine-scholar";
    internal const string RockId = "wave-nine-rock";
    internal const string GateId = "wave-nine-gate";
    internal const string TollId = "wave-nine-toll";
    internal const string RiteId = "wave-nine-rite";
    internal const string BoltId = "wave-nine-bolt";
    internal const string StasisId = "wave-nine-stasis";
    internal const string AegisId = "wave-nine-aegis";

    /// <summary>The key the keyed land gives its mana, CR 106.6: "that mana" said without an object.</summary>
    internal const string ManaKey = "wave-nine-keyed";

    /// <summary>The key the artifact's "choose a color" writes under, read by both of its halves.</summary>
    internal const string ColorKey = "wave-nine-color";

    private readonly Game _game;

    internal WaveNineIntegrationTable(
        ImmutableList<string> deckZero,
        ImmutableList<string> deckOne,
        int startingLife = 20,
        ulong seed = 7)
    {
        _game = new Game(Subsystems.Wave2(), new WaveNineDefinitions());
        var created = _game.Create(new GameSetup(
            [new PlayerStart("One", deckZero, []), new PlayerStart("Two", deckOne, [])],
            Seed: seed,
            StartingLife: startingLife,
            Format: "test"));
        State = created.State;
        Log.AddRange(created.Events);
    }

    internal static PlayerId P0 { get; } = new(0);

    internal static PlayerId P1 { get; } = new(1);

    internal GameState State { get; private set; }

    internal List<GameEvent> Log { get; } = [];

    internal Subsystems Engine => _game.Engine;

    /// <summary>"{T}: Add {G}", CR 605.1a: the plain mana land every deck here is built on.</summary>
    [RuleRef("605.1a")]
    internal static CardDefinition Grove { get; } = Definition(
        GroveId,
        GroveName,
        new TypeLine(CardType.Land, Supertype.Basic),
        cost: null,
        abilities: [new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{G}")))]);

    /// <summary>"{T}: Add {W}", CR 605.1a: a land of the other colour, so a colour filter has something to miss.</summary>
    [RuleRef("605.1a")]
    internal static CardDefinition Vale { get; } = Definition(
        ValeId,
        ValeName,
        new TypeLine(CardType.Land, Supertype.Basic),
        cost: null,
        abilities: [new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{W}")))]);

    /// <summary>
    /// "{T}: Add {G}. When that mana is spent to cast a spell, draw a card", CR 106.6a: the mana
    /// carries a key and the same ability creates the delayed ability that watches it (CR 603.7a).
    /// </summary>
    [RuleRef("106.6a")]
    internal static CardDefinition KeyedGrove { get; } = Definition(
        KeyedGroveId,
        KeyedGroveName,
        new TypeLine(CardType.Land),
        cost: null,
        abilities:
        [
            new ActivatedAbility(
                [new TapSelf()],
                new Sequence(
                [
                    new AddMana(new ManaProduction(Fixed: "{G}"), Key: ManaKey),
                    new DelayedTrigger(new ManaSpent(ManaKey), new Draw(new Fixed(1))),
                ])),
        ]);

    /// <summary>A vanilla 1/1 for {G}: something to spend one green mana on, and nothing else.</summary>
    internal static CardDefinition Ox { get; } = Definition(
        OxId,
        OxName,
        new TypeLine(CardType.Creature, Subtypes: ["Ox"]),
        cost: "{G}",
        power: 1,
        toughness: 1);

    /// <summary>
    /// "As this enters, choose a color. Whenever a land you control is tapped for mana of the
    /// chosen color, add one additional mana of that color", CR 106.12a with 608.2d. Both halves
    /// name the same key, which is the only way "the chosen color" is said twice.
    /// </summary>
    [RuleRef("106.12a")]
    internal static CardDefinition Prism { get; } = Definition(
        PrismId,
        PrismName,
        new TypeLine(CardType.Artifact),
        cost: "{G}",
        abilities:
        [
            new TriggeredAbility(new EntersBattlefield(Itself), new ChooseValue(ChooseWhat.Color, ColorKey)),
            new TriggeredAbility(
                new TappedForMana(new ObjectFilter(Types: CardType.Land, ControlledBy: new You()), OfChosenColorKey: ColorKey),
                new AddMana(new ManaProduction(ChosenColorKey: ColorKey))),
        ]);

    /// <summary>"When this enters, you may pay {2}. If you do, draw a card", CR 118.12.</summary>
    [RuleRef("118.12")]
    internal static CardDefinition Scholar { get; } = Definition(
        ScholarId,
        ScholarName,
        new TypeLine(CardType.Creature, Subtypes: ["Scholar"]),
        cost: "{G}",
        power: 1,
        toughness: 1,
        abilities:
        [
            new TriggeredAbility(
                new EntersBattlefield(Itself),
                new MayPay(new ManaCost("{2}"), new Draw(new Fixed(1)))),
        ]);

    /// <summary>"{T}: Add {G}" on an artifact, so the mana's source is an artifact, CR 106.6.</summary>
    [RuleRef("106.6")]
    internal static CardDefinition Rock { get; } = Definition(
        RockId,
        RockName,
        new TypeLine(CardType.Artifact),
        cost: "{G}",
        abilities: [new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{G}")))]);

    /// <summary>"{G}: Draw a card. Activate only using mana produced by an artifact", CR 106.6b.</summary>
    [RuleRef("106.6b")]
    internal static CardDefinition Gate { get; } = Definition(
        GateId,
        GateName,
        new TypeLine(CardType.Artifact),
        cost: "{G}",
        abilities:
        [
            new ActivatedAbility(
                [new ManaCost("{G}")],
                new Draw(new Fixed(1)),
                PayOnlyFrom: new ObjectFilter(Types: CardType.Artifact)),
        ]);

    /// <summary>An instant with "as an additional cost to cast this spell, sacrifice a land", CR 601.2b.</summary>
    [RuleRef("601.2b")]
    internal static CardDefinition Toll { get; } = Definition(
        TollId,
        TollName,
        new TypeLine(CardType.Instant),
        cost: "{G}",
        abilities: [new SpellAbility(new Draw(new Fixed(1)))],
        additionalCosts: [new SacrificeCost(new ObjectFilter(Types: CardType.Land), new Fixed(1))]);

    /// <summary>"You may cast an instant card from your graveyard", CR 601.2c: its cost's objects are chosen then.</summary>
    [RuleRef("601.2c")]
    internal static CardDefinition Rite { get; } = Definition(
        RiteId,
        RiteName,
        new TypeLine(CardType.Sorcery),
        cost: "{G}",
        abilities:
        [
            new SpellAbility(new MayCast(
                new Each(new ObjectFilter(In: Cards.Zone.Graveyard, Types: CardType.Instant, OwnedBy: new You())))),
        ]);

    /// <summary>"Deal 2 damage to target player", CR 601.2c.</summary>
    [RuleRef("601.2c")]
    internal static CardDefinition Bolt { get; } = Definition(
        BoltId,
        BoltName,
        new TypeLine(CardType.Instant),
        cost: "{G}",
        abilities: [new SpellAbility(new DealDamage(new Fixed(2), ToPlayer: new TargetPlayer(PlayerFilter.Any)))]);

    /// <summary>"Your life total can't change", CR 613.10 read through CR 119.1.</summary>
    [RuleRef("613.10")]
    internal static CardDefinition Stasis { get; } = Definition(
        StasisId,
        StasisName,
        new TypeLine(CardType.Sorcery),
        cost: "{G}",
        abilities:
        [
            new SpellAbility(new ApplyToPlayer(
                new PlayerCant(PlayerRestriction.LifeTotalChanges),
                new You(),
                new Permanently())),
        ]);

    /// <summary>"You have protection from everything", CR 702.16j on a player.</summary>
    [RuleRef("702.16j")]
    internal static CardDefinition Aegis { get; } = Definition(
        AegisId,
        AegisName,
        new TypeLine(CardType.Sorcery),
        cost: "{G}",
        abilities:
        [
            new SpellAbility(new ApplyToPlayer(
                new PlayerProtectionFromEverything(),
                new You(),
                new Permanently())),
        ]);

    /// <summary>CR 109.5: the object the ability is on, and no other copy of the same card.</summary>
    private static ObjectFilter Itself => new(Also: new IsThisObject());

    /// <summary>Apply one command and keep its events. Anything the rules refuse fails the test here, with the rule.</summary>
    internal ImmutableList<GameEvent> Do(Command command)
    {
        var result = _game.Apply(State, command);
        if (result is Refused refused)
            Assert.Fail($"{command.GetType().Name} was refused: {refused.Reason} (CR {refused.Rule}) in turn {State.Turn.Number} {State.Turn.Step}.");

        var applied = (Applied)result;
        State = applied.State;
        Log.AddRange(applied.Events);
        return applied.Events;
    }

    /// <summary>The refusal a command is given now. Used where a rule is proved by what the engine will not do.</summary>
    internal Refused Refuse(Command command) => Assert.IsType<Refused>(_game.Apply(State, command));

    /// <summary>Whatever the engine says to this command — a refusal or an application. The test says which it wanted.</summary>
    internal GameResult Try(Command command) => _game.Apply(State, command);

    /// <summary>Pass priority until this step of this turn, or until the game ends. A question stops the walk, CR 101.4.</summary>
    internal void PassTo(int turn, Step step)
    {
        for (var guard = 0; guard < 500; guard++)
        {
            if (State.Outcome is not null)
                return;
            // CR 514.1: the other player only ever plays lands here, so their hand outgrows the
            // maximum. Discarding down is an ordinary turn action and never what a test is about,
            // so the walk answers it and carries on; anything else asked stops the walk.
            if (State.Pending is { Kind: Choices.ChoiceKinds.Discard } discard)
            {
                Do(new AnswerChoice(discard.Player, discard.Id, [.. discard.Options.Take(Math.Max(discard.Min, 1)).Select(o => o.Id)]));
                continue;
            }

            if (State.Pending is { } pending)
                throw new InvalidOperationException($"{pending.Player} was asked \"{pending.Prompt}\" ({pending.Kind}) in turn {State.Turn.Number} {State.Turn.Step}; nothing else happens until it is answered (CR 101.4).");
            if (State.Turn.Number == turn && State.Turn.Step == step)
                return;

            var holder = State.Priority.Holder ?? throw new InvalidOperationException($"Nobody holds priority in turn {State.Turn.Number} {State.Turn.Step}.");
            Do(new PassPriority(holder));
        }

        throw new InvalidOperationException($"Never reached turn {turn} {step}; stopped in turn {State.Turn.Number} {State.Turn.Step}.");
    }

    /// <summary>Both players pass, which resolves the top of the stack, CR 608.1. Stops early at a question.</summary>
    internal ImmutableList<GameEvent> PassRound()
    {
        var events = Do(new PassPriority(State.Priority.Holder!.Value));
        return State.Outcome is null && State.Pending is null && State.Priority.Holder is { } next
            ? events.AddRange(Do(new PassPriority(next)))
            : events;
    }

    /// <summary>Pass until nothing is on the stack, CR 608.1: everything put there has resolved.</summary>
    [RuleRef("608.1")]
    internal void PassUntilStackIsEmpty()
    {
        for (var guard = 0; guard < 200; guard++)
        {
            if (State.Outcome is not null || State.Pending is not null)
                return;
            if (State.In(ZoneKey.Shared(Cards.Zone.Stack)).IsEmpty)
                return;
            if (State.Priority.Holder is null)
                throw new InvalidOperationException($"Nobody holds priority with a stack of {State.In(ZoneKey.Shared(Cards.Zone.Stack)).Count} in turn {State.Turn.Number} {State.Turn.Step}.");
            PassRound();
        }

        throw new InvalidOperationException($"The stack never emptied in turn {State.Turn.Number} {State.Turn.Step}.");
    }

    /// <summary>Pass priority until the game stops to ask something, CR 101.4: the question is the destination.</summary>
    [RuleRef("101.4")]
    internal PendingChoice PassUntilQuestion()
    {
        for (var guard = 0; guard < 200; guard++)
        {
            if (State.Pending is { } pending)
                return pending;
            if (State.Outcome is not null || State.Priority.Holder is not { } holder)
                break;
            Do(new PassPriority(holder));
        }

        throw new InvalidOperationException($"Nothing was asked; the game reached turn {State.Turn.Number} {State.Turn.Step}.");
    }

    /// <summary>Answer the question that stands with the option whose id or label contains this text.</summary>
    internal ImmutableList<GameEvent> Answer(string contains)
    {
        var pending = State.Pending ?? throw new InvalidOperationException("Nothing was asked.");
        if (pending.Options.IsEmpty)
            return Do(new AnswerChoice(pending.Player, pending.Id, [contains]));

        var option = pending.Options.FirstOrDefault(o =>
                o.Id.Contains(contains, StringComparison.OrdinalIgnoreCase)
                || o.Label.Contains(contains, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"\"{contains}\" is not one of [{string.Join(", ", pending.Options.Select(o => $"{o.Id}/{o.Label}"))}] for \"{pending.Prompt}\".");
        return Do(new AnswerChoice(pending.Player, pending.Id, [option.Id]));
    }

    /// <summary>Play a land of this name from hand and return the permanent it became, CR 400.7.</summary>
    internal ObjectId PlayALand(PlayerId player, string? name = null)
    {
        var card = name is null ? AnyLandInHand(player) : InHand(player, name);
        return Assert.Single(Do(new Commands.PlayLand(player, card)).OfType<Events.LandPlayed>()).Land;
    }

    /// <summary>Tap this many untapped lands for mana, CR 605.3a.</summary>
    internal void TapForMana(PlayerId player, int count)
    {
        var lands = UntappedLands(player).Take(count).ToList();
        Assert.Equal(count, lands.Count);
        foreach (var land in lands)
            Do(new ActivateAbility(player, land, ManaAbilityIndexOf(land)));
    }

    /// <summary>Tap this named permanent for mana, CR 605.3a: the source is what the test is about.</summary>
    internal ImmutableList<GameEvent> TapNamed(PlayerId player, string name)
    {
        var source = Battlefield(player, name).First(id => !State.Objects[id].Tapped);
        return Do(new ActivateAbility(player, source, ManaAbilityIndexOf(source)));
    }

    internal ImmutableList<ObjectId> Lands(PlayerId player) =>
        [.. State.Objects.Values
            .Where(o => o.Zone.Zone == Cards.Zone.Battlefield && o.InitialController == player && o.Face.Types.Types.HasFlag(CardType.Land))
            .OrderBy(o => o.Id.Value)
            .Select(o => o.Id)];

    internal ObjectId InHand(PlayerId player, string name) => Hand(player, name).FirstOrDefault() is { Value: > 0 } id
        ? id
        : throw new InvalidOperationException($"{player} has no {name} in hand in turn {State.Turn.Number}; they hold {string.Join(", ", State.In(ZoneKey.Of(Cards.Zone.Hand, player)).Select(o => State.Objects[o].Definition.Name))}.");

    internal ImmutableList<ObjectId> Hand(PlayerId player, string name) =>
        [.. State.Objects.Values
            .Where(o => o.Zone.Zone == Cards.Zone.Hand && o.Owner == player && o.Definition.Name == name)
            .OrderBy(o => o.Id.Value)
            .Select(o => o.Id)];

    internal ImmutableList<ObjectId> Battlefield(PlayerId controller, string name) =>
        [.. State.Objects.Values
            .Where(o => o.Zone.Zone == Cards.Zone.Battlefield && o.Definition.Name == name && o.InitialController == controller)
            .OrderBy(o => o.Id.Value)
            .Select(o => o.Id)];

    internal ImmutableList<ObjectId> Graveyard(PlayerId owner, string name) =>
        [.. State.Objects.Values
            .Where(o => o.Zone.Zone == Cards.Zone.Graveyard && o.Owner == owner && o.Definition.Name == name)
            .OrderBy(o => o.Id.Value)
            .Select(o => o.Id)];

    /// <summary>A zone change makes a new object, CR 400.7; this is the id the move gave it.</summary>
    internal static ObjectId Became(IEnumerable<GameEvent> events, ObjectId from) =>
        events.OfType<ObjectMoved>().Last(m => m.From == from).To.Id;

    private IEnumerable<ObjectId> UntappedLands(PlayerId player) =>
        State.Objects.Values
            .Where(o => o.Zone.Zone == Cards.Zone.Battlefield
                && o.InitialController == player
                && !o.Tapped
                && o.Face.Types.Types.HasFlag(CardType.Land))
            .OrderBy(o => o.Id.Value)
            .Select(o => o.Id);

    private ObjectId AnyLandInHand(PlayerId player) =>
        State.In(ZoneKey.Of(Cards.Zone.Hand, player))
            .FirstOrDefault(id => State.Objects[id].Face.Types.Types.HasFlag(CardType.Land)) is { Value: > 0 } land
            ? land
            : throw new InvalidOperationException($"{player} has no land in hand in turn {State.Turn.Number}.");

    private int ManaAbilityIndexOf(ObjectId source)
    {
        var abilities = State.Objects[source].Face.AbilityList;
        for (var i = 0; i < abilities.Count; i++)
        {
            if (abilities[i] is ActivatedAbility { Do: AddMana or Sequence })
                return i;
        }

        throw new InvalidOperationException($"{State.Objects[source].Definition.Name} has no mana ability.");
    }

    private static CardDefinition Definition(
        string oracleId,
        string name,
        TypeLine types,
        string? cost,
        int? power = null,
        int? toughness = null,
        IReadOnlyList<Ability>? abilities = null,
        IReadOnlyList<Cost>? additionalCosts = null) =>
        new(
            oracleId,
            name,
            Layout.Normal,
            [
                new Face(
                    name,
                    FaceRole.Main,
                    types,
                    cost,
                    power is null ? null : new PrintedStat(power.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    toughness is null ? null : new PrintedStat(toughness.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    Abilities: abilities,
                    AdditionalCosts: additionalCosts),
            ]);

    /// <summary>The definitions this table is played with. A store, not a card file: no card here is a real one.</summary>
    private sealed class WaveNineDefinitions : IDefinitionStore
    {
        private static readonly CardDefinition[] All =
            [Grove, Vale, KeyedGrove, Ox, Prism, Scholar, Rock, Gate, Toll, Rite, Bolt, Stasis, Aegis];

        public CardDefinition? ByName(string name) => Array.Find(All, d => string.Equals(d.Name, name, StringComparison.Ordinal));

        public CardDefinition? ByOracleId(string oracleId) => Array.Find(All, d => string.Equals(d.OracleId, oracleId, StringComparison.Ordinal));
    }
}
