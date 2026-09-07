using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A real table for the wave-2 exit test: <see cref="Subsystems.Wave2"/> composed, made-up
/// definitions that have abilities, and a way to drive the game one command at a time while
/// keeping the log. Nothing here fakes a subsystem, so a rule that only holds against a fake
/// fails here. The wave-1 table is the shape; what is new is that these cards ask questions,
/// so <see cref="PassTo"/> stops rather than loops when the game is waiting on one (CR 101.4).
/// </summary>
internal sealed class WaveTwoIntegrationTable
{
    internal const string GroveName = "Wave Two Grove";
    internal const string BogName = "Wave Two Bog";
    internal const string OxName = "Wave Two Ox";
    internal const string BoltName = "Wave Two Bolt";
    internal const string SageName = "Wave Two Sage";

    internal const string GroveId = "wave-two-grove";
    internal const string BogId = "wave-two-bog";
    internal const string OxId = "wave-two-ox";
    internal const string BoltId = "wave-two-bolt";
    internal const string SageId = "wave-two-sage";

    private readonly Game _game;

    internal WaveTwoIntegrationTable(
        ImmutableList<string> deckZero,
        ImmutableList<string> deckOne,
        int startingLife = 3,
        ulong seed = 7)
    {
        _game = new Game(Subsystems.Wave2(), new WaveTwoDefinitions());
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

    /// <summary>The composed subsystems, for the two seams the exit test reads rather than commands: views and legal actions.</summary>
    internal Subsystems Engine => _game.Engine;

    /// <summary>A land whose only ability is "{T}: Add {G}".</summary>
    [RuleRef("605")]
    internal static CardDefinition Grove { get; } = Definition(
        GroveId,
        GroveName,
        new TypeLine(CardType.Land, Supertype.Basic),
        cost: null,
        abilities: [new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{G}")))]);

    /// <summary>
    /// "This land enters tapped. {T}: Add {G}." CR 614.1c: a replacement that modifies how a
    /// permanent enters the battlefield, spelled as the vocabulary spells it — the event it
    /// replaces is this object entering, so the filter says <see cref="IsThisObject"/>.
    /// </summary>
    [RuleRef("614.1c")]
    internal static CardDefinition Bog { get; } = Definition(
        BogId,
        BogName,
        new TypeLine(CardType.Land),
        cost: null,
        abilities:
        [
            new ReplacementAbility(new WouldEnterBattlefield(Itself), new EntersTapped()),
            new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{G}"))),
        ]);

    /// <summary>
    /// A legendary 2/2 for {1}{G} with "when this enters, draw a card": CR 603.2 for the
    /// trigger, and CR 704.5j for what a second copy under one controller costs.
    /// </summary>
    [RuleRef("603.2")]
    internal static CardDefinition Ox { get; } = Definition(
        OxId,
        OxName,
        new TypeLine(CardType.Creature, Supertype.Legendary, ["Ox"]),
        cost: "{1}{G}",
        power: 2,
        toughness: 2,
        abilities: [new TriggeredAbility(new EntersBattlefield(Itself), new Draw(new Fixed(1)))]);

    /// <summary>"Deal 3 damage to any target", CR 601.2c: an object target or a player target, chosen as it is cast.</summary>
    [RuleRef("601.2c")]
    internal static CardDefinition Bolt { get; } = Definition(
        BoltId,
        BoltName,
        new TypeLine(CardType.Instant),
        cost: "{G}",
        abilities:
        [
            new SpellAbility(new DealDamage(
                new Fixed(3),
                To: new Target(new ObjectFilter(AnyTypes: CardType.Creature | CardType.Planeswalker | CardType.Battle)),
                ToPlayer: new TargetPlayer(PlayerFilter.Any))),
        ]);

    /// <summary>A 1/1 for {G} with "when this enters, you may draw a card": CR 101.4, a question the game stops for.</summary>
    [RuleRef("101.4")]
    internal static CardDefinition Sage { get; } = Definition(
        SageId,
        SageName,
        new TypeLine(CardType.Creature, Subtypes: ["Sage"]),
        cost: "{G}",
        power: 1,
        toughness: 1,
        abilities: [new TriggeredAbility(new EntersBattlefield(Itself), new May(new Draw(new Fixed(1))))]);

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

    /// <summary>
    /// Pass priority until the game reaches this step of this turn, or ends. A question stops the
    /// walk with the question in the message rather than spinning: CR 101.4 means nothing else
    /// happens until it is answered, so the test has to answer it.
    /// </summary>
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

    /// <summary>
    /// Play a land from hand and return the permanent it became, CR 400.7. With no name, any
    /// land in hand will do: every land here taps for {G}, so which one is played is the
    /// shuffle's business and not the test's.
    /// </summary>
    internal ObjectId PlayALand(PlayerId player, string? name = null)
    {
        var card = name is null ? AnyLandInHand(player) : InHand(player, name);
        var played = Do(new Commands.PlayLand(player, card));
        return Assert.Single(played.OfType<Events.LandPlayed>()).Land;
    }

    /// <summary>Tap this many untapped lands for mana, CR 605.3a.</summary>
    internal void TapForMana(PlayerId player, int count)
    {
        var lands = State.Objects.Values
            .Where(o => o.Zone.Zone == Zone.Battlefield
                && o.InitialController == player
                && !o.Tapped
                && o.Face.Types.Types.HasFlag(CardType.Land))
            .OrderBy(o => o.Id.Value)
            .Take(count)
            .Select(o => o.Id)
            .ToList();
        Assert.Equal(count, lands.Count);
        foreach (var land in lands)
            Do(new ActivateAbility(player, land, AbilityIndexOf(land)));
    }

    private ObjectId AnyLandInHand(PlayerId player) =>
        State.In(ZoneKey.Of(Zone.Hand, player))
            .FirstOrDefault(id => State.Objects[id].Face.Types.Types.HasFlag(CardType.Land)) is { Value: > 0 } land
            ? land
            : throw new InvalidOperationException($"{player} has no land in hand in turn {State.Turn.Number}; they hold {string.Join(", ", State.In(ZoneKey.Of(Zone.Hand, player)).Select(o => State.Objects[o].Definition.Name))}.");

    internal ObjectId InHand(PlayerId player, string name) => Hand(player, name).FirstOrDefault() is { Value: > 0 } id
        ? id
        : throw new InvalidOperationException($"{player} has no {name} in hand in turn {State.Turn.Number}; they hold {string.Join(", ", State.In(ZoneKey.Of(Zone.Hand, player)).Select(o => State.Objects[o].Definition.Name))}.");

    internal ImmutableList<ObjectId> Hand(PlayerId player, string name) =>
        [.. State.Objects.Values
            .Where(o => o.Zone.Zone == Zone.Hand && o.Owner == player && o.Definition.Name == name)
            .OrderBy(o => o.Id.Value)
            .Select(o => o.Id)];

    internal ImmutableList<ObjectId> Battlefield(PlayerId controller, string name) =>
        [.. State.Objects.Values
            .Where(o => o.Zone.Zone == Zone.Battlefield && o.Definition.Name == name && o.InitialController == controller)
            .OrderBy(o => o.Id.Value)
            .Select(o => o.Id)];

    /// <summary>A zone change makes a new object, CR 400.7; this is the id the move gave it.</summary>
    internal static ObjectId Became(IEnumerable<GameEvent> events, ObjectId from) =>
        events.OfType<ObjectMoved>().Last(m => m.From == from).To.Id;

    /// <summary>Which of a land's abilities is the mana ability, so the index is read and never guessed.</summary>
    private int AbilityIndexOf(ObjectId land)
    {
        var abilities = State.Objects[land].Face.AbilityList;
        for (var i = 0; i < abilities.Count; i++)
        {
            if (abilities[i] is ActivatedAbility { Do: AddMana })
                return i;
        }

        throw new InvalidOperationException($"{State.Objects[land].Definition.Name} has no mana ability.");
    }

    private static CardDefinition Definition(string oracleId, string name, TypeLine types, string? cost, int? power = null, int? toughness = null, IReadOnlyList<Ability>? abilities = null) =>
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

    /// <summary>The five definitions this table is played with. A store, not a card file: no card here is a real one.</summary>
    private sealed class WaveTwoDefinitions : IDefinitionStore
    {
        public CardDefinition? ByName(string name) => name switch
        {
            GroveName => Grove,
            BogName => Bog,
            OxName => Ox,
            BoltName => Bolt,
            SageName => Sage,
            _ => null,
        };

        public CardDefinition? ByOracleId(string oracleId) => oracleId switch
        {
            GroveId => Grove,
            BogId => Bog,
            OxId => Ox,
            BoltId => Bolt,
            SageId => Sage,
            _ => null,
        };
    }
}
