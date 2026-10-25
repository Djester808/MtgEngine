using MtgEngine.Cards;
using MtgEngine.Rules.Choices;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Zones;

namespace MtgEngine.Rules.Library;

/// <summary>Coordinates one search instruction across all participants and resumptions.</summary>
internal sealed class LibrarySearchCoordinator(ILibrarySearchRuntime runtime)
{
    /// <summary>CR 701.23i: everyone inspects first, then chooses privately in APNAP order.</summary>
    public void Execute(Search search)
    {
        var saved = runtime.SavedParticipants();
        var participants = saved ?? runtime.Searchers(search)
            .Select(searcher =>
            {
                var owner = runtime.LibraryOwner(search, searcher);
                return new LibrarySearchParticipant(searcher, owner,
                    runtime.Plan(search, searcher, owner));
            }).ToImmutableList();
        runtime.SaveParticipants(participants);
        // A choice saved before batch plans existed already emitted its inspection/search events.
        var legacy = saved is null && (runtime.Answer() is not null
            || runtime.HasLegacySelection());
        if (saved is null && !legacy)
        {
            foreach (var participant in participants)
            {
                runtime.Inspect(participant);
            }
        }
        foreach (var participant in participants)
        {
            var memory = new LibrarySearchScope(participants.Count, participant.Searcher);
            if (runtime.Cards(memory, LibrarySearchMemory.Choice) is not null)
                continue;
            if (legacy && runtime.Cards(memory, LibrarySearchMemory.Selected) is { } selected)
            {
                runtime.RememberCards(memory, LibrarySearchMemory.Choice, selected);
                continue;
            }
            var answer = runtime.AnswerPlayer == participant.Searcher && runtime.AnswerKind == ChoiceKinds.Search
                ? runtime.Answer() : null;
            var plan = participant.Plan;
            if (!plan.Zones.IsEmpty && answer is null)
            {
                runtime.Ask(participant.Searcher, plan);
                return;
            }
            runtime.RememberCards(memory, LibrarySearchMemory.Choice, answer ?? []);
        }
        var moves = runtime.SavedMoves(participants) ?? participants.ToImmutableDictionary(
            participant => participant.Searcher,
            participant => runtime.PlanMoves(search,
                runtime.Cards(new LibrarySearchScope(participants.Count, participant.Searcher), LibrarySearchMemory.Choice)!));
        runtime.SaveMoves(moves);
        var board = runtime.SavedBoard() ?? ZoneMoveSnapshot.Capture(runtime.State);
        runtime.SaveBoard(board);
        // Keep every participant's progress until the entire instruction finishes. Proposal
        // ordinals can then replay earlier participants without moving/shuffling them twice.
        foreach (var participant in participants)
        {
            var memory = new LibrarySearchScope(participants.Count, participant.Searcher);
            Found(search, participant.Searcher, participant.LibraryOwner,
                runtime.Cards(memory, LibrarySearchMemory.Choice)!, moves[participant.Searcher], memory, board);
            if (runtime.Halted)
                return;
        }
        foreach (var participant in participants)
        {
            var memory = new LibrarySearchScope(participants.Count, participant.Searcher);
            FinishSearch(search, participant.LibraryOwner, participant.Searcher, memory);
            if (runtime.Halted)
                return;
        }
        var arrived = participants.SelectMany(participant => runtime.Cards(new LibrarySearchScope(participants.Count, participant.Searcher), LibrarySearchMemory.Arrived)!).Distinct().ToImmutableList();
        foreach (var participant in participants)
        {
            runtime.EndInspection(participant.Searcher);
        }
        runtime.ClearContinuation();
        runtime.RememberResult(search.RememberAs, arrived);
    }

    /// <summary>Finish one search across replacement pauses; shuffle only after all selected moves.</summary>
    [RuleRef("701.23")]
    private void Found(Search search, PlayerId searcher, PlayerId libraryOwner,
        ImmutableList<ObjectId> found, ImmutableList<LibrarySearchMove> moves, LibrarySearchScope memory, string board)
    {
        if (runtime.Cards(memory, LibrarySearchMemory.Selected) is null)
        {
            var library = ZoneSystem.KeyFor(Zone.Library, libraryOwner);
            runtime.RememberCards(memory, LibrarySearchMemory.Selected, found);
            runtime.RememberCards(memory, LibrarySearchMemory.Library, found.Where(id => runtime.State.Objects.TryGetValue(id, out var card) && card.Zone == library));
            runtime.RememberCards(memory, LibrarySearchMemory.Arrived, []);
            runtime.RememberCards(memory, LibrarySearchMemory.Top, []);
            runtime.RememberCards(memory, LibrarySearchMemory.Bottom, []);
            if (search.Reveal)
                runtime.Reveal(found, searcher);
        }

        var arrived = runtime.Cards(memory, LibrarySearchMemory.Arrived)!;
        foreach (var move in moves)
        {
            var (id, to, where, _, withinLibrary) = move;
            if (withinLibrary && runtime.Positioned(memory, id))
                continue;
            ObjectId? landed;
            if (withinLibrary)
            {
                landed = id; // Repositioning within a library is not a zone change.
                runtime.MarkPositioned(memory, id);
            }
            else
                landed = runtime.Move(move, board);
            if (landed is { } next && !arrived.Contains(next))
            {
                arrived = arrived.Add(next);
                runtime.RememberCards(memory, LibrarySearchMemory.Arrived, arrived);
                if (to == Zone.Library && where is Placement.Top or Placement.Bottom)
                {
                    var key = where == Placement.Top ? LibrarySearchMemory.Top : LibrarySearchMemory.Bottom;
                    runtime.RememberCards(memory, key, (runtime.Cards(memory, key) ?? []).Add(next));
                }
            }
            if (runtime.Halted)
                return;
        }

        return;
    }

    private void FinishSearch(Search search, PlayerId libraryOwner, PlayerId searcher, LibrarySearchScope memory)
    {
        var libraryZone = ZoneSystem.KeyFor(Zone.Library, libraryOwner);
        if (search.ShuffleAfter && LibrarySearchPlanner.RequestedZones(search).Contains(Zone.Library)
            && (!search.ShuffleOnlyIfFound || !runtime.Cards(memory, LibrarySearchMemory.Library)!.IsEmpty))
        {
            runtime.Shuffle(libraryOwner, searcher);
            if (runtime.Halted)
                return;
        }

        var current = runtime.State.In(libraryZone);
        var ordered = LibraryOrder.Arrange(current, runtime.Cards(memory, LibrarySearchMemory.Top) ?? [],
            runtime.Cards(memory, LibrarySearchMemory.Bottom) ?? []);
        if (!current.SequenceEqual(ordered))
        {
            runtime.Order(libraryZone, ordered, searcher);
        }

        return;
    }

}
