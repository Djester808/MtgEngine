using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Zones;

public sealed partial class ZoneSystem
{
    public Outcome MoveInBatch(GameState state, MoveRequest move, string snapshot, IEnumerable<string>? usedReplacements = null)
    {
        // Search resumes each member through its interpreter continuation. Keep the
        // original evaluation board while forwarding that continuation's history.
        var evaluated = MoveAll(ZoneMoveSnapshot.Evaluate(snapshot, state), [move], usedReplacements);
        return evaluated with { State = GameReducer.Apply(state, evaluated.Events) };
    }

    public Outcome ResumeMoveBatch(GameState state, ImmutableList<ObjectMoved> moves) => ResumeMoveBatch(state, moves, null);

    public Outcome ResumeMoveBatch(GameState state, ImmutableList<ObjectMoved> moves, string? snapshot,
        ImmutableDictionary<int, ImmutableHashSet<string>>? inherited = null)
    {
        var tape = new Tape(state);
        for (var index = 0; index < moves.Count; index++)
        {
            var proposed = tape.Resolve(moves[index]);
            if (!tape.State.Objects.ContainsKey(proposed.From))
                continue;
            if (!IsLibraryReposition(proposed))
                proposed = proposed with
                {
                    To = proposed.To with { Id = new ObjectId(tape.State.NextObjectId), Timestamp = tape.State.NextTimestamp },
                };
            var evaluation = ZoneMoveSnapshot.Evaluate(snapshot, tape.State);
            tape.AddAll(ApplyMove(evaluation, tape.State, proposed, inherited?.GetValueOrDefault(moves[index].From.Value)));
            if (tape.State.Pending is not { } pending)
                continue;
            tape.Add(new ChoiceRequested(pending with
            {
                Context = pending.Context with
                {
                    DeferredZoneMoves = moves.Skip(index + 1).Select(tape.Resolve).ToImmutableList(),
                    DeferredMoveHistory = moves.Skip(index + 1).Where(move => inherited?.ContainsKey(move.From.Value) == true)
                        .ToImmutableDictionary(move => tape.Resolve(move).From.Value, move => inherited![move.From.Value]),
                    ZoneMoveSnapshot = snapshot,
                },
            }));
            break;
        }

        return tape.Done();
    }
}
