using System.Globalization;
using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>
/// A board built by hand for the choice system: a few players, a few cards in named zones and
/// a question already pending. Nothing here reads a card file — a choice is answered against
/// the state and the options it was asked with, never against a card's translation.
/// </summary>
internal sealed class ChoiceResumptionBoard
{
    public ChoiceResumptionBoard(int players = 2) =>
        State = GameReducer.Replay(
        [
            new GameStarted([.. Enumerable.Range(0, players).Select(i => new PlayerStart($"seat {i}", [], []))], 11UL, 20, "commander"),
        ]);

    public GameState State { get; private set; }

    public static CardDefinition Definition(string name, CardType types = CardType.Creature, Supertype supertypes = Supertype.None) =>
        new(name, name, Layout.Normal, [new Face(name, FaceRole.Main, new TypeLine(types, supertypes, ["Beast"]))]);

    public ObjectId Put(string name, Zone zone = Zone.Hand, int owner = 0, Supertype supertypes = Supertype.None)
    {
        var seat = new PlayerId(owner);
        var id = new ObjectId(State.NextObjectId);
        var created = new ObjectCreated(
            new GameObject(id, Definition(name, supertypes: supertypes), 0, seat, seat, ZoneKey.Of(zone, seat), State.NextTimestamp) { SummoningSick = false },
            -1);
        State = created.Apply(State);
        return id;
    }

    /// <summary>Put a question to a player, as the turn system or a state-based action would.</summary>
    public PendingChoice Ask(string kind, IEnumerable<ChoiceOption> options, int min = 1, int max = 1, int owner = 0, ChoiceContext? context = null)
    {
        var choice = new PendingChoice(
            new ChoiceId(State.NextChoiceId),
            new PlayerId(owner),
            kind,
            $"answer the {kind}",
            [.. options],
            min,
            max,
            context ?? new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty));
        State = new ChoiceRequested(choice).Apply(State);
        return choice;
    }

    public ImmutableList<ObjectId> In(Zone zone, int owner = 0) => State.In(ZoneKey.Of(zone, new PlayerId(owner)));

    public static ImmutableList<ChoiceOption> Options(params ObjectId[] ids) =>
        [.. ids.Select(id => new ChoiceOption(id.Value.ToString(CultureInfo.InvariantCulture), "option", id))];

    public static ImmutableList<string> Answer(params ObjectId[] ids) =>
        [.. ids.Select(id => id.Value.ToString(CultureInfo.InvariantCulture))];
}

internal sealed record ChoiceResumptionMove(ObjectId What, ZoneKey To, Placement Where);

/// <summary>
/// The zone system as the choice system uses it: every move, draw and shuffle is recorded, and
/// the state really changes, so a mulligan's redraw is visible to the question that follows it.
/// </summary>
internal sealed class ChoiceResumptionFakeZones : IZoneSystem
{
    public string Name => "fake zones";

    public List<ChoiceResumptionMove> Moves { get; } = [];

    public List<(PlayerId Player, int Count)> Draws { get; } = [];

    public List<PlayerId> Shuffles { get; } = [];

    public Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where = Placement.Default, bool tapped = false, PlayerId? controller = null)
    {
        Moves.Add(new ChoiceResumptionMove(what, to, where));
        return MoveCore(state, what, to, where);
    }

    public Outcome Draw(GameState state, PlayerId player, int count)
    {
        Draws.Add((player, count));
        var outcome = Outcome.Unchanged(state);
        for (var i = 0; i < count; i++)
        {
            outcome = outcome.Then(s =>
            {
                var library = s.In(ZoneKey.Of(Zone.Library, player));
                return library.IsEmpty ? Outcome.Unchanged(s) : MoveCore(s, library[0], ZoneKey.Of(Zone.Hand, player), Placement.Default);
            });
        }

        return outcome;
    }

    public Outcome Shuffle(GameState state, PlayerId player)
    {
        Shuffles.Add(player);
        var zone = ZoneKey.Of(Zone.Library, player);
        var reordered = new ZoneReordered(zone, [.. state.In(zone).Reverse()]);
        return new Outcome(reordered.Apply(state), [reordered]);
    }

    public Outcome Create(GameSetup setup, IDefinitionStore store) => throw new NotSupportedException();

    public Outcome CreateToken(GameState state, TokenSpec token, PlayerId controller, bool tapped, bool attacking) => throw new NotSupportedException();

    private static Outcome MoveCore(GameState state, ObjectId what, ZoneKey to, Placement where)
    {
        if (!state.Objects.TryGetValue(what, out var obj))
            return Outcome.Unchanged(state);

        var moved = new ObjectMoved(
            what,
            obj with { Id = new ObjectId(state.NextObjectId), Zone = to, Timestamp = state.NextTimestamp },
            where == Placement.Top ? 0 : -1);
        return new Outcome(moved.Apply(state), [moved]);
    }
}

/// <summary>The turn system, only far enough to see that the cleanup step was finished.</summary>
internal sealed class ChoiceResumptionFakeTurn : ITurnSystem
{
    public string Name => "fake turn";

    public int Advanced { get; private set; }

    public Outcome Advance(GameState state)
    {
        Advanced++;
        var began = new StepBegan(Step.Untap);
        return new Outcome(began.Apply(state), [began]);
    }

    public Outcome Begin(GameState state) => throw new NotSupportedException();

    public Outcome Pass(GameState state, PlayerId player) => throw new NotSupportedException();
}

/// <summary>Stands in for the interpreter: records what it was handed back and changes nothing.</summary>
internal sealed class ChoiceResumptionFakeResumable : IResumable
{
    public string Name => "fake resumption";

    public List<Resumption> Resumed { get; } = [];

    public Outcome Resume(GameState state, Resumption resumption)
    {
        Resumed.Add(resumption);
        return Outcome.Unchanged(state);
    }
}
