using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A real table for the wave-13 and wave-14 joins: <see cref="Subsystems.Wave2"/> composed, made-up
/// definitions that exercise a keyed target player remembered from the announcement (CR 601.2c with
/// 608.2h), a departed target's controller read as last-known information (CR 400.7), an Aura's
/// object target judged at announcement (CR 601.2c with 702.5b), a creature type chosen as a land
/// enters (CR 614.1c with 614.12) and an any-colour activation whose mana carries a delayed trigger
/// (CR 106.6a with 603.7). Nothing here fakes a subsystem, so a join that only holds against a fake
/// fails here. Every card below is invented.
/// </summary>
internal sealed class WaveFourteenIntegrationTable
{
    internal const string GroveName = "Wave Fourteen Grove";
    internal const string DrainName = "Wave Fourteen Drain";
    internal const string EchoName = "Wave Fourteen Echo";
    internal const string BanishName = "Wave Fourteen Banish";
    internal const string OxName = "Wave Fourteen Ox";
    internal const string ClingName = "Wave Fourteen Cling";
    internal const string HallName = "Wave Fourteen Hall";
    internal const string RaiderName = "Wave Fourteen Raider";
    internal const string SpringName = "Wave Fourteen Spring";
    internal const string DrakeName = "Wave Fourteen Drake";

    internal const string GroveId = "wave-fourteen-grove";
    internal const string DrainId = "wave-fourteen-drain";
    internal const string EchoId = "wave-fourteen-echo";
    internal const string BanishId = "wave-fourteen-banish";
    internal const string OxId = "wave-fourteen-ox";
    internal const string ClingId = "wave-fourteen-cling";
    internal const string HallId = "wave-fourteen-hall";
    internal const string RaiderId = "wave-fourteen-raider";
    internal const string SpringId = "wave-fourteen-spring";
    internal const string DrakeId = "wave-fourteen-drake";

    /// <summary>The key a spell's target player is remembered under, CR 601.2c with 608.2h: "that player".</summary>
    internal const string PlayerKey = "wave-fourteen-player";

    /// <summary>The key an exiled target is remembered under, CR 608.2h: "its controller" after it has gone.</summary>
    internal const string CreatureKey = "wave-fourteen-creature";

    /// <summary>The key the land's "as this enters, choose a creature type" writes under, CR 608.2d.</summary>
    internal const string TypeKey = "wave-fourteen-type";

    /// <summary>The key the spring gives its mana, CR 106.6: "that mana" said without an object.</summary>
    internal const string ManaKey = "wave-fourteen-mana";

    /// <summary>Which ability of the hall's face is the entering replacement; not the first, so an index that travels was computed (CR 101.4).</summary>
    internal const int HallReplacementIndex = 1;

    private readonly Game _game;

    internal WaveFourteenIntegrationTable(
        ImmutableList<string> deckZero,
        ImmutableList<string> deckOne,
        int startingLife = 20,
        ulong seed = 7)
    {
        _game = new Game(Subsystems.Wave2(), new WaveFourteenDefinitions());
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

    /// <summary>"{T}: Add {G}", CR 605.1a: the plain green land every deck here is built on.</summary>
    [RuleRef("605.1a")]
    internal static CardDefinition Grove { get; } = Definition(
        GroveId,
        GroveName,
        new TypeLine(CardType.Land, Supertype.Basic),
        cost: null,
        abilities: [new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{G}")))]);

    /// <summary>
    /// "Target player loses 2 life and you gain 2 life", CR 601.2c with 608.2d: the target is keyed,
    /// so the announcement is what the resolution reads the player back from.
    /// </summary>
    [RuleRef("601.2c")]
    internal static CardDefinition Drain { get; } = Definition(
        DrainId,
        DrainName,
        new TypeLine(CardType.Sorcery),
        cost: "{G}",
        abilities:
        [
            new SpellAbility(new Sequence(
            [
                new LoseLife(new Fixed(2), new TargetPlayer(PlayerFilter.Any, Key: PlayerKey)),
                new GainLife(new Fixed(2), new You()),
            ])),
        ]);

    /// <summary>
    /// "Target player loses 1 life, then that player loses 1 life", CR 608.2h: the second step
    /// says "that player" through the key the first step's target declared.
    /// </summary>
    [RuleRef("608.2h")]
    internal static CardDefinition Echo { get; } = Definition(
        EchoId,
        EchoName,
        new TypeLine(CardType.Sorcery),
        cost: "{G}",
        abilities:
        [
            new SpellAbility(new Sequence(
            [
                new LoseLife(new Fixed(1), new TargetPlayer(PlayerFilter.Any, Key: PlayerKey)),
                new LoseLife(new Fixed(1), new RememberedPlayer(PlayerKey)),
            ])),
        ]);

    /// <summary>
    /// "Exile target creature, then its controller draws a card", CR 400.7 with 608.2h: by the
    /// time the draw is read the creature is a new object in exile, so "its controller" is the
    /// last-known information of the one that was targeted.
    /// </summary>
    [RuleRef("400.7")]
    internal static CardDefinition Banish { get; } = Definition(
        BanishId,
        BanishName,
        new TypeLine(CardType.Instant),
        cost: "{G}",
        abilities:
        [
            new SpellAbility(new Sequence(
            [
                new Exile(new Target(new ObjectFilter(Types: CardType.Creature), Key: CreatureKey)),
                new Draw(new Fixed(1), new ControllerOf(new Remembered(CreatureKey))),
            ])),
        ]);

    /// <summary>A vanilla 1/1 Ox for {1}: something to enchant, exile or pay for, and nothing else.</summary>
    internal static CardDefinition Ox { get; } = Definition(
        OxId,
        OxName,
        new TypeLine(CardType.Creature, Subtypes: ["Ox"]),
        cost: "{1}",
        power: 1,
        toughness: 1);

    /// <summary>"Enchant creature", CR 702.5b: an object restriction, cast at a land by the test on purpose.</summary>
    [RuleRef("702.5b")]
    internal static CardDefinition Cling { get; } = Definition(
        ClingId,
        ClingName,
        new TypeLine(CardType.Enchantment, Subtypes: ["Aura"]),
        cost: "{G}",
        abilities: [new KeywordAbility(KeywordKind.Enchant, Of: new ObjectFilter(Types: CardType.Creature))]);

    /// <summary>
    /// "{T}: Add {C}." "As this land enters, choose a creature type." "Whenever a creature of the
    /// chosen type enters under your control, you gain 1 life." CR 614.1c: the choosing is part of
    /// the entering; CR 608.2d: the land keeps what it chose and its trigger reads it back.
    /// </summary>
    [RuleRef("614.1c")]
    internal static CardDefinition Hall { get; } = Definition(
        HallId,
        HallName,
        new TypeLine(CardType.Land),
        cost: null,
        abilities:
        [
            new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{C}"))),
            new ReplacementAbility(
                new WouldEnterBattlefield(Itself),
                new ChooseValue(ChooseWhat.CreatureType, TypeKey),
                SelfOnly: true),
            new TriggeredAbility(
                new EntersBattlefield(new ObjectFilter(Types: CardType.Creature, ControlledBy: new You(), Also: new IsChosenType(TypeKey))),
                new GainLife(new Fixed(1), new You())),
        ]);

    /// <summary>A vanilla 1/1 Goblin for {1}: a creature of the type the hall is told to watch.</summary>
    internal static CardDefinition Raider { get; } = Definition(
        RaiderId,
        RaiderName,
        new TypeLine(CardType.Creature, Subtypes: ["Goblin"]),
        cost: "{1}",
        power: 1,
        toughness: 1);

    /// <summary>
    /// "{T}: Add one mana of any color. When that mana is spent to cast a spell, draw a card",
    /// CR 106.6a with 608.2d: the colour is announced with the activation and the same ability
    /// creates the delayed ability that watches the mana (CR 603.7a).
    /// </summary>
    [RuleRef("106.6a")]
    internal static CardDefinition Spring { get; } = Definition(
        SpringId,
        SpringName,
        new TypeLine(CardType.Land),
        cost: null,
        abilities:
        [
            new ActivatedAbility(
                [new TapSelf()],
                new Sequence(
                [
                    new AddMana(new ManaProduction(AnyColor: true), Key: ManaKey),
                    new DelayedTrigger(new ManaSpent(ManaKey), new Draw(new Fixed(1))),
                ])),
        ]);

    /// <summary>A vanilla 1/1 for {U}: only the announced colour can pay for it on a table of green lands.</summary>
    internal static CardDefinition Drake { get; } = Definition(
        DrakeId,
        DrakeName,
        new TypeLine(CardType.Creature, Subtypes: ["Drake"]),
        cost: "{U}",
        power: 1,
        toughness: 1);

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

    /// <summary>Pass priority until this step of this turn, or until the game ends. A question stops the walk, CR 101.4.</summary>
    internal void PassTo(int turn, Step step)
    {
        for (var guard = 0; guard < 500; guard++)
        {
            if (State.Outcome is not null)
                return;
            // CR 514.1: a player who only draws lands outgrows the maximum hand size. Discarding
            // down is an ordinary turn action and never what a test is about, so the walk answers
            // it and carries on; anything else asked stops the walk.
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

    /// <summary>Pass until nothing is on the stack, CR 608.1: everything put there has resolved. Returns at once when nothing is.</summary>
    [RuleRef("608.1")]
    internal ImmutableList<GameEvent> PassUntilStackIsEmpty()
    {
        var all = ImmutableList<GameEvent>.Empty;
        for (var guard = 0; guard < 200; guard++)
        {
            if (State.Outcome is not null || State.Pending is not null)
                return all;
            if (State.In(ZoneKey.Shared(Cards.Zone.Stack)).IsEmpty)
                return all;
            if (State.Priority.Holder is null)
                throw new InvalidOperationException($"Nobody holds priority with a stack of {State.In(ZoneKey.Shared(Cards.Zone.Stack)).Count} in turn {State.Turn.Number} {State.Turn.Step}.");
            all = all.AddRange(PassRound());
        }

        throw new InvalidOperationException($"The stack never emptied in turn {State.Turn.Number} {State.Turn.Step}.");
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

    internal ImmutableList<ObjectId> Exiled(PlayerId owner, string name) =>
        [.. State.Objects.Values
            .Where(o => o.Zone.Zone == Cards.Zone.Exile && o.Owner == owner && o.Definition.Name == name)
            .OrderBy(o => o.Id.Value)
            .Select(o => o.Id)];

    internal int LibraryCount(PlayerId player) => State.In(ZoneKey.Of(Cards.Zone.Library, player)).Count;

    internal int HandCount(PlayerId player) => State.In(ZoneKey.Of(Cards.Zone.Hand, player)).Count;

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
        IReadOnlyList<Ability>? abilities = null) =>
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
                    Abilities: abilities),
            ]);

    /// <summary>The definitions this table is played with. A store, not a card file: no card here is a real one.</summary>
    private sealed class WaveFourteenDefinitions : IDefinitionStore
    {
        private static readonly CardDefinition[] All =
            [Grove, Drain, Echo, Banish, Ox, Cling, Hall, Raider, Spring, Drake];

        public CardDefinition? ByName(string name) => Array.Find(All, d => string.Equals(d.Name, name, StringComparison.Ordinal));

        public CardDefinition? ByOracleId(string oracleId) => Array.Find(All, d => string.Equals(d.OracleId, oracleId, StringComparison.Ordinal));
    }
}
