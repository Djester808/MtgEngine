using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// The query layer, built on states made by hand out of events. Every definition here is
/// invented: no card name reaches the engine or its tests, and the characteristics come from a
/// fake layer system so a test says exactly what an object is.
/// </summary>
public sealed partial class QueryTests
{
    private static readonly PlayerId Alice = new(0);
    private static readonly PlayerId Bob = new(1);

    [Fact]
    public void A_filter_with_no_zone_means_the_battlefield()
    {
        var world = World.New().Creature(1, Alice, 2, 2).Creature(2, Alice, 2, 2, Cards.Zone.Hand);

        Assert.True(world.Query.Matches(world.State, ObjectFilter.Creature, new ObjectId(1), world.Context));
        Assert.False(world.Query.Matches(world.State, ObjectFilter.Creature, new ObjectId(2), world.Context));
        Assert.True(world.Query.Matches(world.State, new ObjectFilter(In: Cards.Zone.Hand, Types: CardType.Creature), new ObjectId(2), world.Context));
    }

    [Fact]
    public void Controller_and_owner_are_separate_questions()
    {
        // Object 2 is Bob's card but Alice controls it, so "creature you control" finds both and
        // "creature you own" finds only the first. CR 108.3 and CR 613.1a.
        var world = World.New()
            .Creature(1, Alice, 1, 1)
            .Creature(2, Bob, 1, 1)
            .ControlledBy(2, Alice);

        var controlled = world.Count(new ObjectFilter(Types: CardType.Creature, ControlledBy: new You()));
        var owned = world.Count(new ObjectFilter(Types: CardType.Creature, OwnedBy: new You()));

        Assert.Equal(2, controlled);
        Assert.Equal(1, owned);
        Assert.Equal(1, world.Count(new ObjectFilter(Types: CardType.Creature, OwnedBy: new EachOpponent())));
    }

    [Fact]
    public void Types_are_conjunctive_and_subtypes_are_disjunctive()
    {
        var world = World.New()
            .Creature(1, Alice, 1, 1, types: CardType.Artifact | CardType.Creature, subtypes: ["Beast"])
            .Creature(2, Alice, 1, 1, subtypes: ["Spirit"]);

        Assert.Equal(1, world.Count(new ObjectFilter(Types: CardType.Artifact | CardType.Creature)));
        Assert.Equal(2, world.Count(new ObjectFilter(Types: CardType.Creature)));
        Assert.Equal(1, world.Count(new ObjectFilter(NotTypes: CardType.Artifact, Types: CardType.Creature)));
        Assert.Equal(2, world.Count(new ObjectFilter(Subtypes: ["Beast", "Spirit"])));
        Assert.Equal(1, world.Count(new ObjectFilter(Subtypes: ["Spirit"])));
        Assert.Equal(1, world.Count(new ObjectFilter(NotSubtypes: ["Spirit"], Types: CardType.Creature)));
    }

    [Fact]
    public void Comparisons_read_power_toughness_and_mana_value()
    {
        var world = World.New()
            .Creature(1, Alice, 4, 1, manaCost: "{2}{G}")
            .Creature(2, Alice, 1, 5, manaCost: "{W/U}{2/B}{X}");

        Assert.Equal(1, world.Count(new ObjectFilter(Power: Ge(4))));
        Assert.Equal(1, world.Count(new ObjectFilter(Toughness: Ge(5))));
        // Hybrid counts as its greater half and X as zero off the stack, CR 202.3b: both are 3.
        Assert.Equal(2, world.Count(new ObjectFilter(ManaValue: new Comparison(ComparisonOp.Eq, new Fixed(3)))));
        Assert.Equal(0, world.Count(new ObjectFilter(ManaValue: new Comparison(ComparisonOp.Ge, new Fixed(4)))));

        // A permanent with no power satisfies no power comparison, CR 208.4.
        var withLand = world.Land(3, Alice);
        Assert.Equal(1, withLand.Count(new ObjectFilter(Power: new Comparison(ComparisonOp.Le, new Fixed(1)))));
    }

    [Fact]
    public void Other_excludes_the_object_asking()
    {
        var world = World.New().Creature(1, Alice, 1, 1).Creature(2, Alice, 1, 1).As(1);

        Assert.Equal(1, world.Count(new ObjectFilter(Types: CardType.Creature, Other: true)));
        Assert.False(world.Query.Matches(world.State, new ObjectFilter(Types: CardType.Creature, Other: true), new ObjectId(1), world.Context));
        Assert.True(world.Query.Matches(world.State, new ObjectFilter(Types: CardType.Creature, Other: true), new ObjectId(2), world.Context));
    }

    [Fact]
    public void An_also_predicate_narrows_the_filter()
    {
        var world = World.New()
            .Creature(1, Alice, 1, 1, colors: Color.Green)
            .Creature(2, Alice, 1, 1, colors: Color.Red)
            .As(1);

        var sharesColor = new ObjectFilter(Types: CardType.Creature, Also: new SharesColorWith(new Self()));
        Assert.Equal(0, world.Count(sharesColor));

        var world2 = World.New()
            .Creature(1, Alice, 1, 1, colors: Color.Green)
            .Creature(2, Alice, 1, 1, colors: Color.Green | Color.Red)
            .As(1);
        Assert.Equal(1, world2.Count(sharesColor));
        Assert.Equal(1, world2.Count(new ObjectFilter(Types: CardType.Creature, Also: new NotPredicate(new SharesColorWith(new Self())))));
    }

    [Fact]
    public void Tapped_counters_and_attachment_are_read_from_the_object()
    {
        var world = World.New()
            .Creature(1, Alice, 1, 1)
            .Creature(2, Alice, 1, 1)
            .Tap(1)
            .Counters(2, Counters.PlusOnePlusOne, 2)
            .Attach(2, 1);

        Assert.Equal(1, world.Count(new ObjectFilter(Tapped: true, Types: CardType.Creature)));
        Assert.Equal(1, world.Count(new ObjectFilter(WithCounter: Counters.PlusOnePlusOne)));
        Assert.Equal(1, world.Count(new ObjectFilter(WithAnyCounter: true)));
        Assert.Equal(1, world.Count(new ObjectFilter(Types: CardType.Creature, Also: new IsAttached())));
    }

    /// <summary>
    /// CR 301.5 with CR 303.4f: an Aura attached to a player is attached, so <c>IsAttached</c>
    /// answers for it as it does for one on a creature. The object half of the vocabulary rightly
    /// answers nothing about it — "enchanted creature" is no creature, and "attached to
    /// [selector]" names objects — which is the shape of the gap <c>seam-requests/E5c.md</c> asks
    /// to close with an "enchanted player" <c>PlayerRef</c>: today no record can name that player,
    /// so the state and the view carry the attachment and no card can read it.
    /// </summary>
    [Fact]
    [RuleRef("303.4f")]
    public void An_aura_attached_to_a_player_is_attached_and_names_no_object()
    {
        var world = World.New()
            .Creature(1, Alice, 1, 1)
            .Creature(2, Alice, 1, 1)
            .AttachToPlayer(2, Bob)
            .As(2);

        Assert.Equal(1, world.Count(new ObjectFilter(Types: CardType.Creature, Also: new IsAttached())));
        Assert.Equal(1, world.Count(new ObjectFilter(Types: CardType.Creature, Also: new IsAttached(false))));

        // The object half answers none, because there is no object to answer with.
        Assert.Empty(world.Query.Resolve(world.State, new Attached(), world.Context));
        Assert.Equal(0, world.Count(new ObjectFilter(Types: CardType.Creature, Also: new AttachedTo(new Self()))));
    }

    [Fact]
    public void CountOf_and_Controls_count_the_same_objects()
    {
        var world = World.New()
            .Creature(1, Alice, 1, 1)
            .Creature(2, Alice, 1, 1)
            .Creature(3, Bob, 1, 1);

        Assert.Equal(2, world.Query.Evaluate(world.State, new CountOf(new ObjectFilter(Types: CardType.Creature, ControlledBy: new You())), world.Context));
        Assert.Equal(3, world.Query.Evaluate(world.State, new CountOf(ObjectFilter.Creature), world.Context));
        Assert.True(world.Holds(new Controls(new You(), ObjectFilter.Creature, Ge(2))));
        Assert.False(world.Holds(new Controls(new You(), ObjectFilter.Creature, Ge(3))));
        Assert.True(world.Holds(new Controls(new EachOpponent(), ObjectFilter.Creature, Ge(1))));
    }

    [Fact]
    public void Compare_and_the_boolean_conditions_combine()
    {
        var world = World.New().Creature(1, Alice, 3, 3);

        var lifeIsTwenty = new Cards.Compare(new LifeOf(new You()), ComparisonOp.Eq, new Fixed(20));
        var controlsTwo = new Controls(new You(), ObjectFilter.Creature, Ge(2));

        Assert.True(world.Holds(lifeIsTwenty));
        Assert.False(world.Holds(controlsTwo));
        Assert.False(world.Holds(new And([lifeIsTwenty, controlsTwo])));
        Assert.True(world.Holds(new Or([lifeIsTwenty, controlsTwo])));
        Assert.True(world.Holds(new NotCondition(controlsTwo)));
    }

    [Fact]
    public void Arithmetic_amounts_fold_their_parts()
    {
        var world = World.New().Creature(1, Alice, 5, 5).Creature(2, Alice, 2, 2);

        Assert.Equal(7, world.Value(new TotalAmong(ObjectFilter.Creature, new PowerOf(new Self()))));
        Assert.Equal(5, world.Value(new GreatestAmong(ObjectFilter.Creature, new PowerOf(new Self()))));
        Assert.Equal(3, world.Value(new Cards.Half(new Fixed(5), RoundUp: true)));
        Assert.Equal(2, world.Value(new Cards.Half(new Fixed(5), RoundUp: false)));
        Assert.Equal(6, world.Value(new Times(new Fixed(3), 2)));
        Assert.Equal(1, world.Value(new Difference(new Fixed(3), new Fixed(2))));
        Assert.Equal(3, world.Value(new Min(new Fixed(3), new Fixed(9))));
        Assert.Equal(9, world.Value(new Max(new Fixed(3), new Fixed(9))));
        Assert.Equal(20, world.Value(new LifeOf(new You())));
        Assert.Equal(1, world.Value(new PlayerCount(new EachOpponent())));
        Assert.Equal(2, world.Value(new PlayerCount(new EachPlayer())));
    }

    [Fact]
    public void Each_names_every_matching_object_and_Top_reads_the_library_in_order()
    {
        var world = World.New()
            .Creature(1, Alice, 1, 1)
            .Creature(2, Bob, 1, 1)
            .Library(10, Alice)
            .Library(11, Alice);

        var each = world.Query.Resolve(world.State, new Each(ObjectFilter.Creature), world.Context);
        Assert.Equal(new[] { new ObjectId(1), new ObjectId(2) }, each);

        var top = world.Query.Resolve(world.State, new Cards.Top(new You(), new Fixed(2)), world.Context);
        Assert.Equal(new[] { new ObjectId(10), new ObjectId(11) }, top);
        Assert.Equal(new[] { new ObjectId(10) }, world.Query.Resolve(world.State, new Cards.Top(new You()), world.Context));
    }

    [Fact]
    public void Remembered_and_stored_come_out_of_the_context()
    {
        var world = World.New().Creature(1, Alice, 1, 1).Remember("exiled", new ObjectId(1)).Store("discarded", 2);

        Assert.Equal(new[] { new ObjectId(1) }, world.Query.Resolve(world.State, new Remembered("exiled"), world.Context));
        Assert.Empty(world.Query.Resolve(world.State, new Remembered("nothing"), world.Context));
        Assert.Equal(2, world.Value(new Stored("discarded")));
        Assert.True(world.Holds(new StoredIs("discarded", Ge(2))));
        Assert.False(world.Holds(new StoredIs("discarded", Ge(3))));
    }

    [Fact]
    public void Self_and_targets_come_out_of_the_context_too()
    {
        var world = World.New().Creature(1, Alice, 1, 1).Creature(2, Bob, 1, 1).As(1).Targeting(new ObjectId(2));

        Assert.Equal(new[] { new ObjectId(1) }, world.Query.Resolve(world.State, new Self(), world.Context));
        Assert.Equal(new[] { new ObjectId(2) }, world.Query.Resolve(world.State, new Target(ObjectFilter.Creature), world.Context));
        Assert.Equal(new[] { new ObjectId(2) }, world.Query.Resolve(world.State, new ThisSpellsTargets(), world.Context));
        Assert.True(world.Holds(new TargetsAre(new ObjectFilter(Types: CardType.Creature, ControlledBy: new EachOpponent()))));
        Assert.False(world.Holds(new TargetsAre(new ObjectFilter(Types: CardType.Creature, ControlledBy: new You()))));
    }

    [Fact]
    public void Player_references_resolve_without_a_choice()
    {
        var world = World.New().Creature(1, Bob, 1, 1);

        Assert.Equal(new[] { Alice }, world.Players(new You()));
        Assert.Equal(new[] { Bob }, world.Players(new EachOpponent()));
        Assert.Equal(new[] { Alice, Bob }, world.Players(new EachPlayer()));
        Assert.Equal(new[] { Alice }, world.Players(new ActivePlayer()));
        Assert.Equal(new[] { Bob }, world.Players(new OwnerOf(new Each(ObjectFilter.Creature))));
        Assert.Equal(new[] { Alice, Bob }, world.Players(new PlayerWithMostOf(new LifeOf(new You()))));
    }

    [Fact]
    public void A_question_the_state_cannot_answer_names_the_record_instead_of_guessing()
    {
        var world = World.New().Creature(1, Alice, 1, 1);

        // It is a RefusedException, so Game.Apply turns it into a Refused and the table is told
        // which primitive is missing rather than the game dying mid-command.
        var commander = Assert.Throws<RefusedException>(
            () => world.Query.Resolve(world.State, new Cards.Commander(new You()), world.Context));
        Assert.Contains("Commander", commander.Reason, StringComparison.Ordinal);
    }

    [Fact]
    [RuleRef("903.3")]
    public void A_commander_is_matched_by_the_designation_the_object_carries()
    {
        // CR 903.3: the designation is made as the game begins and travels with the object
        // through every zone change, CR 400.7, so the query layer reads it rather than deducing
        // it. Combat damage asks this of every source that hits a player, CR 903.10a.
        var world = World.New().Commander(1, Alice).Creature(2, Alice, 2, 2);

        Assert.Equal(1, world.Count(new ObjectFilter(IsCommander: true)));
        Assert.True(world.Query.Matches(world.State, new ObjectFilter(IsCommander: true), new ObjectId(1), world.Context));
        Assert.False(world.Query.Matches(world.State, new ObjectFilter(IsCommander: true), new ObjectId(2), world.Context));
        Assert.True(world.Query.Matches(world.State, new ObjectFilter(IsCommander: false), new ObjectId(2), world.Context));
    }

    private static Comparison Ge(int n) => new(ComparisonOp.Ge, new Fixed(n));

    /// <summary>A state built out of events, with a fake layer system that says what each object is.</summary>
    private sealed record World(GameState State, FakeLayers Layers, EffectContext Context)
    {
        public MtgEngine.Rules.Query.Query Query => new(Layers);

        public static World New(int players = 2)
        {
            var starts = Enumerable.Range(0, players)
                .Select(i => new PlayerStart($"P{i}", [], []))
                .ToImmutableList();
            var state = GameReducer.Replay([new GameStarted(starts, 1, 20, "commander")]);
            state = state with { Turn = state.Turn with { Number = 1, ActivePlayer = new PlayerId(0) } };
            var context = new EffectContext(
                null, new PlayerId(0), [], [], null,
                ImmutableDictionary<string, ImmutableList<ObjectId>>.Empty,
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, int>.Empty,
                [], null);
            return new World(state, new FakeLayers(), context);
        }

        public World Creature(
            int id,
            PlayerId owner,
            int power,
            int toughness,
            Cards.Zone zone = Cards.Zone.Battlefield,
            CardType types = CardType.Creature,
            Color colors = Color.Green,
            IReadOnlyList<string>? subtypes = null,
            string? manaCost = null) =>
            Put(id, owner, zone, new Characteristics(
                $"Object {id}", types, Supertype.None, [.. subtypes ?? ["Beast"]], colors,
                power, toughness, null, null, manaCost, [], owner));

        public World Land(int id, PlayerId owner) =>
            Put(id, owner, Cards.Zone.Battlefield, new Characteristics(
                $"Object {id}", CardType.Land, Supertype.None, ["Forest"], Color.Colorless,
                null, null, null, null, null, [], owner));

        /// <summary>An object designated a commander as the game began, CR 903.3, and still one wherever it is.</summary>
        public World Commander(int id, PlayerId owner) =>
            Put(
                id,
                owner,
                Cards.Zone.Battlefield,
                new Characteristics($"Object {id}", CardType.Creature, Supertype.None, ["Beast"], Color.Green, 3, 3, null, null, null, [], owner),
                commander: true);

        /// <summary>A land whose subtypes the test names, so CR 205.3i can be asked of them.</summary>
        public World LandOf(int id, PlayerId owner, IReadOnlyList<string> subtypes) =>
            Put(id, owner, Cards.Zone.Battlefield, new Characteristics(
                $"Object {id}", CardType.Land, Supertype.None, [.. subtypes], Color.Colorless,
                null, null, null, null, null, [], owner));

        /// <summary>
        /// A token created onto the battlefield, CR 111.1. Its printed face carries the types the
        /// test names, because the turn's record of a token is read against that face once the
        /// token itself has stopped existing (CR 111.7) and the layers can no longer be asked.
        /// </summary>
        public World Token(int id, PlayerId controller, CardType types, IReadOnlyList<string> subtypes)
        {
            var line = new TypeLine(types, Supertype.None, [.. subtypes]);
            var definition = new CardDefinition(
                $"oracle-token-{id}",
                $"Token {id}",
                Layout.Normal,
                [new Face($"Token {id}", FaceRole.Main, line)]);
            var obj = new GameObject(
                new ObjectId(id), definition, 0, controller, controller, new ZoneKey(Cards.Zone.Battlefield, null), id)
            { IsToken = true };
            Layers.Set(new ObjectId(id), new Characteristics(
                $"Token {id}", types, Supertype.None, [.. subtypes], Color.Colorless,
                null, null, null, null, null, [], controller));
            return this with { State = new ObjectCreated(obj, -1).Apply(State) };
        }

        /// <summary>CR 111.7: a token that has left the battlefield stops existing.</summary>
        public World CeaseToExist(int id) =>
            this with { State = new ObjectCeasedToExist(new ObjectId(id)).Apply(State) };

        /// <summary>
        /// A spell on the stack, CR 111.1. <c>copy</c> is the mark CR 707.10 puts on a copied
        /// spell, and it lives on the stack entry because only a spell can carry one.
        /// </summary>
        public World Spell(int id, PlayerId owner, bool copy) =>
            Put(
                id,
                owner,
                Cards.Zone.Stack,
                new Characteristics($"Object {id}", CardType.Instant, Supertype.None, [], Color.Red, null, null, null, null, null, [], owner),
                stack: new StackEntry(null, null, [], [], [], null, copy));

        /// <summary>
        /// CR 707.12: a copy of a card, made in the zone the original is in. The zone system marks
        /// it with a designation rather than a stack entry — such a copy has no stack entry, and it
        /// is not a token (CR 111.1), so neither of the other two marks describes it.
        /// </summary>
        public World CopyOfCard(int id) =>
            this with
            {
                State = State with
                {
                    Objects = State.Objects.SetItem(
                        new ObjectId(id),
                        State.Objects[new ObjectId(id)] with { Designations = [Zones.ZoneSystem.CopyOfCard] }),
                },
            };

        public World Library(int id, PlayerId owner) =>
            Put(id, owner, Cards.Zone.Library, new Characteristics(
                $"Object {id}", CardType.Creature, Supertype.None, ["Beast"], Color.Green,
                1, 1, null, null, null, [], owner));

        private World Put(int id, PlayerId owner, Cards.Zone zone, Characteristics characteristics, bool commander = false, StackEntry? stack = null)
        {
            var definition = new CardDefinition(
                $"oracle-{id}",
                $"Object {id}",
                Layout.Normal,
                [new Face($"Object {id}", FaceRole.Main, new TypeLine(characteristics.Types))]);
            var obj = new GameObject(new ObjectId(id), definition, 0, owner, owner, new ZoneKey(zone, zone == Cards.Zone.Battlefield ? null : (PlayerId?)owner), id) { IsCommander = commander, Stack = stack };
            Layers.Set(new ObjectId(id), characteristics);
            return this with { State = new ObjectCreated(obj, -1).Apply(State) };
        }

        public World ControlledBy(int id, PlayerId controller)
        {
            Layers.Set(new ObjectId(id), Layers.Of(State, new ObjectId(id)) with { Controller = controller });
            return this;
        }

        public World Tap(int id) => this with { State = new ObjectChanged(new ObjectId(id), Tapped: true).Apply(State) };

        public World Counters(int id, string kind, int n) =>
            this with { State = new CountersChanged(new ObjectId(id), kind, n).Apply(State) };

        public World Attach(int id, int to) =>
            this with { State = new ObjectChanged(new ObjectId(id), AttachedTo: new ObjectId(to)).Apply(State) };

        /// <summary>CR 303.4f: the other half of an attachment — an Aura on a player.</summary>
        public World AttachToPlayer(int id, PlayerId player) =>
            this with { State = new ObjectChanged(new ObjectId(id), AttachedToPlayer: player).Apply(State) };

        public World As(int source) => this with { Context = Context with { Source = new ObjectId(source) } };

        public World Targeting(params ObjectId[] targets) => this with { Context = Context with { Targets = [.. targets] } };

        public World Remember(string key, params ObjectId[] objects) =>
            this with { Context = Context with { Remembered = Context.Remembered.SetItem(key, [.. objects]) } };

        /// <summary>A value an earlier step of the same effect chose and stored under a key.</summary>
        public World Choose(string key, string value) =>
            this with { Context = Context with { Chosen = Context.Chosen.SetItem(key, value) } };

        public World Store(string key, int value) =>
            this with { Context = Context with { Stored = Context.Stored.SetItem(key, value) } };

        public int Count(ObjectFilter filter) => Query.Evaluate(State, new CountOf(filter), Context);

        public bool Holds(Condition condition) => Query.Holds(State, condition, Context);

        public int Value(Amount amount) => Query.Evaluate(State, amount, Context);

        public ImmutableList<PlayerId> Players(PlayerRef player) => Query.Players(State, player, Context);
    }

    /// <summary>The layer system a test controls: each object is exactly what the test said it is.</summary>
    private sealed class FakeLayers : ICharacteristics
    {
        private readonly Dictionary<ObjectId, Characteristics> _of = [];

        public string Name => "fake layers";

        public void Set(ObjectId id, Characteristics characteristics) => _of[id] = characteristics;

        public Characteristics Of(GameState state, ObjectId id) =>
            _of.TryGetValue(id, out var c) ? c : throw new InvalidOperationException($"no characteristics set for {id}");

        public PlayerId ControllerOf(GameState state, ObjectId id) => Of(state, id).Controller;

        /// <summary>
        /// CR 613.1a: the printed reading is not the test's to invent — a look-back asks what an
        /// object <em>was</em>, and no <c>Set</c> can answer that for an object the state no
        /// longer holds. The real layer system's printed snapshot is the answer here too.
        /// </summary>
        public Characteristics PrintedOf(GameObject obj) =>
            MtgEngine.Rules.Layers.LayerSystem.PrintedCharacteristics(obj);
    }
}
