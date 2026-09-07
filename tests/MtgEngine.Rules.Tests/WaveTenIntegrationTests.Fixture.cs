using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A real table for the wave-10 joins: <see cref="Subsystems.Wave2"/> composed, made-up definitions
/// that exercise a tapped-for-mana trigger reading the colour the firing produced (CR 106.12a with
/// 608.2d), an Aura whose enchant ability names a player (CR 601.2c with 702.5b), an activation that
/// chooses the colour it adds (CR 608.2d), the player prohibitions read by the state-based actions
/// (CR 613.10 with 704.5a), protection from everything on a player (CR 702.16) and a combat damage
/// batch halted twice by CR 616.1 (CR 510.3). Nothing here fakes a subsystem, so a join that only
/// holds against a fake fails here. Every card below is invented.
/// </summary>
internal sealed class WaveTenIntegrationTable
{
    internal const string GroveName = "Wave Ten Grove";
    internal const string ValeName = "Wave Ten Vale";
    internal const string LensName = "Wave Ten Lens";
    internal const string FacetName = "Wave Ten Facet";
    internal const string FontName = "Wave Ten Font";
    internal const string OxName = "Wave Ten Ox";
    internal const string CreatureAuraName = "Wave Ten Cling";
    internal const string PlayerAuraName = "Wave Ten Shroud";
    internal const string WardName = "Wave Ten Ward";
    internal const string BoltName = "Wave Ten Bolt";
    internal const string AegisName = "Wave Ten Aegis";
    internal const string ShieldName = "Wave Ten Screen";

    internal const string GroveId = "wave-ten-grove";
    internal const string ValeId = "wave-ten-vale";
    internal const string LensId = "wave-ten-lens";
    internal const string FacetId = "wave-ten-facet";
    internal const string FontId = "wave-ten-font";
    internal const string OxId = "wave-ten-ox";
    internal const string CreatureAuraId = "wave-ten-cling";
    internal const string PlayerAuraId = "wave-ten-shroud";
    internal const string WardId = "wave-ten-ward";
    internal const string BoltId = "wave-ten-bolt";
    internal const string AegisId = "wave-ten-aegis";
    internal const string ShieldId = "wave-ten-screen";

    /// <summary>
    /// The key a tapped-for-mana firing carries its colour under, CR 106.12a. It is the trigger
    /// system's own constant rather than a key a card chose: this card never says "choose a color",
    /// it says "of that color", and the colour is the one the firing produced.
    /// </summary>
    internal const string FiringColorKey = Rules.Triggers.TriggerSystem.TappedForManaColor;

    private readonly Game _game;

    internal WaveTenIntegrationTable(
        ImmutableList<string> deckZero,
        ImmutableList<string> deckOne,
        int startingLife = 20,
        ulong seed = 7)
    {
        _game = new Game(Subsystems.Wave2(), new WaveTenDefinitions());
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

    /// <summary>"{T}: Add {W}", CR 605.1a: a land of the other colour, so a colour filter has something to miss.</summary>
    [RuleRef("605.1a")]
    internal static CardDefinition Vale { get; } = Definition(
        ValeId,
        ValeName,
        new TypeLine(CardType.Land, Supertype.Basic),
        cost: null,
        abilities: [new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(Fixed: "{W}")))]);

    /// <summary>
    /// "Whenever a land you control is tapped for green mana, add an additional one mana of that
    /// color", CR 106.12a with 608.2d, spelled the way <c>TappedForMana</c>'s own documentation says
    /// the two halves agree: the effect's <c>ManaProduction.ChosenColorKey</c> is the trigger
    /// system's colour key and nothing else is set.
    /// </summary>
    [RuleRef("106.12a")]
    internal static CardDefinition Lens { get; } = Definition(
        LensId,
        LensName,
        new TypeLine(CardType.Artifact),
        cost: "{G}",
        abilities:
        [
            new TriggeredAbility(
                new TappedForMana(new ObjectFilter(Types: CardType.Land, ControlledBy: new You()), OfColor: Color.Green),
                new AddMana(new ManaProduction(ChosenColorKey: FiringColorKey))),
        ]);

    /// <summary>The same card with <c>ChosenColor</c> also set, which is the shape the interpreter reads, CR 608.2d.</summary>
    [RuleRef("608.2d")]
    internal static CardDefinition Facet { get; } = Definition(
        FacetId,
        FacetName,
        new TypeLine(CardType.Artifact),
        cost: "{G}",
        abilities:
        [
            new TriggeredAbility(
                new TappedForMana(new ObjectFilter(Types: CardType.Land, ControlledBy: new You()), OfColor: Color.Green),
                new AddMana(new ManaProduction(ChosenColor: true, ChosenColorKey: FiringColorKey))),
        ]);

    /// <summary>"{T}: Add one mana of any color", CR 608.2d: the colour is chosen with the activation.</summary>
    [RuleRef("608.2d")]
    internal static CardDefinition Font { get; } = Definition(
        FontId,
        FontName,
        new TypeLine(CardType.Artifact),
        cost: "{G}",
        abilities: [new ActivatedAbility([new TapSelf()], new AddMana(new ManaProduction(AnyColor: true)))]);

    /// <summary>A vanilla 2/2 for {G}{G}: something to attack with, and nothing else.</summary>
    internal static CardDefinition Ox { get; } = Definition(
        OxId,
        OxName,
        new TypeLine(CardType.Creature, Subtypes: ["Ox"]),
        cost: "{G}{G}",
        power: 2,
        toughness: 2);

    /// <summary>"Enchant creature", CR 702.5b: an object restriction, cast at a player by the test on purpose.</summary>
    [RuleRef("702.5b")]
    internal static CardDefinition CreatureAura { get; } = Definition(
        CreatureAuraId,
        CreatureAuraName,
        new TypeLine(CardType.Enchantment, Subtypes: ["Aura"]),
        cost: "{G}",
        abilities: [new KeywordAbility(KeywordKind.Enchant, Of: new ObjectFilter(Types: CardType.Creature))]);

    /// <summary>"Enchant player", CR 702.5b: the enchant ability names a player filter, not an object one.</summary>
    [RuleRef("702.5b")]
    internal static CardDefinition PlayerAura { get; } = Definition(
        PlayerAuraId,
        PlayerAuraName,
        new TypeLine(CardType.Enchantment, Subtypes: ["Aura"]),
        cost: "{G}",
        abilities: [new KeywordAbility(KeywordKind.Enchant, OfPlayer: new PlayerFilter())]);

    /// <summary>"You can't lose the game this turn", CR 613.10 read by CR 704.5a.</summary>
    [RuleRef("613.10")]
    internal static CardDefinition Ward { get; } = Definition(
        WardId,
        WardName,
        new TypeLine(CardType.Sorcery),
        cost: "{G}",
        abilities:
        [
            new SpellAbility(new ApplyToPlayer(
                new PlayerCant(PlayerRestriction.Lose),
                new You(),
                new UntilEndOfTurn())),
        ]);

    /// <summary>"Deal 2 damage to target player", CR 601.2c: the way a life total is brought to zero here.</summary>
    [RuleRef("601.2c")]
    internal static CardDefinition Bolt { get; } = Definition(
        BoltId,
        BoltName,
        new TypeLine(CardType.Instant),
        cost: "{G}",
        abilities: [new SpellAbility(new DealDamage(new Fixed(2), ToPlayer: new TargetPlayer(PlayerFilter.Any)))]);

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
    private sealed class WaveTenDefinitions : IDefinitionStore
    {
        private static readonly CardDefinition[] All =
            [Grove, Vale, Lens, Facet, Font, Ox, CreatureAura, PlayerAura, Ward, Bolt, Aegis, Shield];

        public CardDefinition? ByName(string name) => Array.Find(All, d => string.Equals(d.Name, name, StringComparison.Ordinal));

        public CardDefinition? ByOracleId(string oracleId) => Array.Find(All, d => string.Equals(d.OracleId, oracleId, StringComparison.Ordinal));
    }
}
