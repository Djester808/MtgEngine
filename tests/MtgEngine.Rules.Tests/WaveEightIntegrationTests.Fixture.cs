using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A real table for the wave-8 joins: <see cref="Subsystems.Wave2"/> composed, made-up
/// definitions that exercise what waves 3 to 8 added, and a way to drive the game one command
/// at a time while keeping the log. Nothing here fakes a subsystem, so a join that only holds
/// against a fake fails here. The wave-2 table is the shape; every card below is invented.
/// </summary>
internal sealed class WaveEightIntegrationTable
{
    internal const string GroveName = "Wave Eight Grove";
    internal const string OxName = "Wave Eight Ox";
    internal const string PlayerAuraName = "Wave Eight Shroud";
    internal const string CreatureAuraName = "Wave Eight Cling";
    internal const string SparkName = "Wave Eight Spark";
    internal const string RecallName = "Wave Eight Recall";
    internal const string TollName = "Wave Eight Toll";
    internal const string RiteName = "Wave Eight Rite";
    internal const string ShieldName = "Wave Eight Ward";

    internal const string GroveId = "wave-eight-grove";
    internal const string OxId = "wave-eight-ox";
    internal const string PlayerAuraId = "wave-eight-shroud";
    internal const string CreatureAuraId = "wave-eight-cling";
    internal const string SparkId = "wave-eight-spark";
    internal const string RecallId = "wave-eight-recall";
    internal const string TollId = "wave-eight-toll";
    internal const string RiteId = "wave-eight-rite";
    internal const string ShieldId = "wave-eight-ward";

    private readonly Game _game;

    internal WaveEightIntegrationTable(
        ImmutableList<string> deckZero,
        ImmutableList<string> deckOne,
        int startingLife = 20,
        ulong seed = 7)
    {
        _game = new Game(Subsystems.Wave2(), new WaveEightDefinitions());
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

    /// <summary>A land whose only ability is "{T}: Add {G}", CR 605.1a.</summary>
    [RuleRef("605.1a")]
    internal static CardDefinition Grove { get; } = Definition(
        GroveId,
        GroveName,
        new TypeLine(CardType.Land, Supertype.Basic),
        cost: null,
        abilities: [new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{G}")))]);

    /// <summary>A vanilla 2/2 for {1}{G}: nothing but the rules moves it.</summary>
    internal static CardDefinition Ox { get; } = Definition(
        OxId,
        OxName,
        new TypeLine(CardType.Creature, Subtypes: ["Ox"]),
        cost: "{1}{G}",
        power: 2,
        toughness: 2);

    /// <summary>"Enchant player", CR 702.5b: the enchant ability names a player filter, not an object one.</summary>
    [RuleRef("702.5b")]
    internal static CardDefinition PlayerAura { get; } = Definition(
        PlayerAuraId,
        PlayerAuraName,
        new TypeLine(CardType.Enchantment, Subtypes: ["Aura"]),
        cost: "{G}",
        abilities: [new KeywordAbility(KeywordKind.Enchant, OfPlayer: new PlayerFilter())]);

    /// <summary>"Enchant creature", CR 702.5b: an object restriction, cast at a player by the test on purpose.</summary>
    [RuleRef("702.5b")]
    internal static CardDefinition CreatureAura { get; } = Definition(
        CreatureAuraId,
        CreatureAuraName,
        new TypeLine(CardType.Enchantment, Subtypes: ["Aura"]),
        cost: "{G}",
        abilities: [new KeywordAbility(KeywordKind.Enchant, Of: new ObjectFilter(Types: CardType.Creature))]);

    /// <summary>An instant that draws a card, so that casting it leaves a card in the graveyard, CR 608.2m.</summary>
    [RuleRef("608.2m")]
    internal static CardDefinition Spark { get; } = Definition(
        SparkId,
        SparkName,
        new TypeLine(CardType.Instant),
        cost: "{G}",
        abilities: [new SpellAbility(new Draw(new Fixed(1)))]);

    /// <summary>
    /// "Until end of turn, you may cast an instant card from your graveyard without paying its
    /// mana cost", CR 601.3a with 118.9: a lasting permission, not a cast.
    /// </summary>
    [RuleRef("601.3a")]
    internal static CardDefinition Recall { get; } = Definition(
        RecallId,
        RecallName,
        new TypeLine(CardType.Sorcery),
        cost: "{G}",
        abilities:
        [
            new SpellAbility(new MayCast(
                new Each(new ObjectFilter(In: Cards.Zone.Graveyard, Types: CardType.Instant, OwnedBy: new You())),
                WithoutPayingManaCost: true,
                Until: new UntilEndOfTurn())),
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

    /// <summary>"You may cast an instant card from your hand", CR 601.2c: the cost's objects are chosen then.</summary>
    [RuleRef("601.2c")]
    internal static CardDefinition Rite { get; } = Definition(
        RiteId,
        RiteName,
        new TypeLine(CardType.Sorcery),
        cost: "{G}",
        abilities:
        [
            new SpellAbility(new MayCast(
                new Each(new ObjectFilter(In: Cards.Zone.Hand, Types: CardType.Instant, OwnedBy: new You())))),
        ]);

    /// <summary>"Prevent the next 1 damage that would be dealt to you this game", CR 615.1.</summary>
    [RuleRef("615.1")]
    internal static CardDefinition Shield { get; } = Definition(
        ShieldId,
        ShieldName,
        new TypeLine(CardType.Sorcery),
        cost: "{G}",
        abilities:
        [
            new SpellAbility(new ApplyToPlayer(
                new PreventDamage(new Fixed(1), ToPlayersToo: true),
                new You(),
                new Permanently())),
        ]);

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

    /// <summary>Play a land from hand and return the permanent it became, CR 400.7.</summary>
    internal ObjectId PlayALand(PlayerId player)
    {
        var played = Do(new Commands.PlayLand(player, AnyLandInHand(player)));
        return Assert.Single(played.OfType<Events.LandPlayed>()).Land;
    }

    /// <summary>Tap this many untapped lands for mana, CR 605.3a.</summary>
    internal void TapForMana(PlayerId player, int count)
    {
        var lands = UntappedLands(player).Take(count).ToList();
        Assert.Equal(count, lands.Count);
        foreach (var land in lands)
            Do(new ActivateAbility(player, land, AbilityIndexOf(land)));
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

    private int AbilityIndexOf(ObjectId land)
    {
        var abilities = State.Objects[land].Face.AbilityList;
        for (var i = 0; i < abilities.Count; i++)
        {
            if (abilities[i] is ActivatedAbility { Do: AddMana })
                return i;
        }

        throw new InvalidOperationException("That land has no mana ability.");
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
    private sealed class WaveEightDefinitions : IDefinitionStore
    {
        private static readonly CardDefinition[] All =
            [Grove, Ox, PlayerAura, CreatureAura, Spark, Recall, Toll, Rite, Shield];

        public CardDefinition? ByName(string name) => Array.Find(All, d => string.Equals(d.Name, name, StringComparison.Ordinal));

        public CardDefinition? ByOracleId(string oracleId) => Array.Find(All, d => string.Equals(d.OracleId, oracleId, StringComparison.Ordinal));
    }
}
