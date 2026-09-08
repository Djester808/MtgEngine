namespace MtgEngine.Cards.Pipeline.Scenarios;

/// <summary>
/// The roads that read a step back: a card a step remembered under a key is what a later step
/// moves or measures. The board keeps the alias and the filler under the key, so the expectation
/// can name the very card rather than settle for a count.
/// </summary>
/// <remarks>
/// A reveal of the top X with a choice among the revealed cards (<see cref="Reveal"/> of a
/// <see cref="Top"/>, then a <see cref="Move"/> of a <see cref="Choose"/> among the remembered set)
/// was staged here in wave 17 and played: the engine put the chosen card into the graveyard with
/// the rest, and the road was withheld until it read a permanent card in a library
/// (seam-requests/P19.md § 1). E9p made that reading zone-aware, so the three arms are here — the
/// reveal, the choice among what it showed, and the rest of it into the graveyard — and the board's
/// <see cref="SmokeBoard.Reveals"/> and <see cref="SmokeFillers.Unfindable"/> are what they stand on.
/// </remarks>
public static partial class SmokeScenario
{
    /// <summary>The effects that read a remembered step, or nothing when the record says something the board cannot stage.</summary>
    private static bool Remembers(SmokeBoard board, Effect effect)
    {
        switch (effect)
        {
            // CR 404.1 with CR 110.1: "put target creature card from a graveyard onto the
            // battlefield under your control" — the graveyard is stocked with the creature, the
            // spell is aimed at it, and it is remembered so "that card's mana value" can be read.
            case Move { What: Target dug, To: Zone.Battlefield, Where: Placement.Default, UnderControlOf: null or You, Tapped: false, FaceDown: false, Attacking: false, Transformed: false, WithCounters: null, AttachedTo: null, RememberAs: var key }
                when dug is { Count: null or Fixed { N: 1 }, UpTo: false } && dug.Filter.OwnedBy is null or You
                    && SmokeFillers.Buried(dug.Filter, board.AnnouncedX) is { } buried:
                {
                    var alias = board.Buries(buried);
                    if (key is not null)
                        board.Remember(key, alias, buried);
                    board.Expect(new InZone(alias, Zone.Battlefield));
                    board.Caster(graveyard: -1);
                    board.Arrives(1);
                }

                break;

            // CR 404.1 with CR 701.19: "put target card from a graveyard on top of its owner's
            // library" — the graveyard is stocked with a card the filter admits, the spell is aimed
            // at it, and afterwards that card is in the library and the graveyard holds only the
            // spell. The placement is not asserted: a library's order is not a zone a scenario can
            // read, and the zone is what the effect says happens.
            case Move { What: Target lifted, To: Zone.Library, UnderControlOf: null, Tapped: false, FaceDown: false, Attacking: false, Transformed: false, WithCounters: null, AttachedTo: null }
                when lifted is { Count: null or Fixed { N: 1 }, UpTo: false } && lifted.Filter.OwnedBy is null or You
                    && SmokeFillers.Buried(lifted.Filter, board.AnnouncedX) is { } lost:
                board.Expect(new InZone(board.Buries(lost), Zone.Library, Caster));
                board.Caster(library: 1, graveyard: -1);
                break;

            // CR 400.7 with CR 110.6: "put this artifact on top of its owner's library" moves the
            // source itself off the battlefield, so what the play shows is the zone it went to —
            // and a card there is neither tapped nor untapped, which is why the tap the activation
            // paid is not an expectation (<see cref="SmokeActivation"/>).
            case Move { What: Self, To: var away, UnderControlOf: null, Tapped: false, FaceDown: false, Attacking: false, Transformed: false, WithCounters: null, AttachedTo: null }
                when board.PlaysFromBattlefield && away is Zone.Library or Zone.Hand or Zone.Graveyard or Zone.Exile:
                board.MovesItselfTo(away);
                break;

            // CR 701.15: "reveal the top X cards of your library" shows them and leaves them where
            // they are, remembered under a key for the steps that read them back. Nothing is
            // stocked here: what those cards have to be is the business of the step that chooses
            // among them, which is the only one that knows what it is looking for.
            case Reveal { What: Top { Whose: You, Zone: Zone.Library } top, Who: null or You, RememberAs: { } shown }
                when Revealing(board, top.Count) is { } revealed:
                board.Reveals(shown, revealed);
                break;

            // CR 700.2 with CR 701.15a: "you may put any number of permanent cards with mana value
            // X or less from among them onto the battlefield". The top of the library is stocked
            // with what the reveal is going to show — one card the filter admits and the rest it
            // refuses — so the choice has exactly one option and the expectation can name the card
            // it took rather than count a battlefield. Each is remembered under the key the reveal
            // used, and the one taken under the choice's own, so the step that puts everything
            // revealed and not taken into the graveyard reads back the same two sets the engine does.
            case Move { What: Choose { Among: Remembered { Key: var among }, Chooser: You, Count: null } picked, To: Zone.Battlefield, Where: Placement.Default, UnderControlOf: null or You, Tapped: false, FaceDown: false, Attacking: false, Transformed: false, WithCounters: null, AttachedTo: null }
                when board.Revealed(among) is { } many && many <= LibrarySize
                    && SmokeFillers.Findable(picked.Filter, board.AnnouncedX) is { } admitted
                    && SmokeFillers.Unfindable(picked.Filter, board.AnnouncedX) is { } refused:
                {
                    var taken = board.Stocks(admitted);
                    board.Remember(among, taken, admitted);
                    if (picked.Key is { } key)
                        board.Remember(key, taken, admitted);
                    board.Expect(new InZone(taken, Zone.Battlefield));
                    board.Arrives(1);
                    board.Caster(library: -1);
                    for (var under = 1; under < many; under++)
                        board.Remember(among, board.Stocks(refused), refused);
                }

                break;

            // CR 700.2: "then put all cards revealed this way that weren't put onto the battlefield
            // into your graveyard" — everything still in the library that one key remembered and
            // the other did not. The difference between the two sets is the cards the step is
            // about, and the board named each of them when it stocked them.
            case Move { What: Each { Filter: { In: Zone.Library } rest }, To: Zone.Graveyard, Where: Placement.Default, UnderControlOf: null, Tapped: false, FaceDown: false, Attacking: false, Transformed: false, WithCounters: null, AttachedTo: null }
                when Unchosen(rest.Also) is { } keys && Left(board, keys) is { Count: > 0 } left:
                foreach (var (alias, _) in left)
                    board.Expect(new InZone(alias, Zone.Graveyard, Caster));
                board.Caster(library: -left.Count, graveyard: left.Count);
                break;

            default:
                break;
        }

        return true;
    }

    /// <summary>
    /// CR 601.2b: the X a reveal of the top X announces. Two rather than the usual one, because a
    /// choice among a single card cannot show both halves of "choose among them, then the rest go
    /// to the graveyard" — there is no rest, and the step that moves it would assert nothing.
    /// </summary>
    [RuleRef("601.2b")]
    private const int RevealedX = 2;

    /// <summary>
    /// How many cards the reveal shows, or null when the count is one the board cannot put a number
    /// to or is deeper than the library the setup stocks. A count of X raises what the cast is going
    /// to announce first, so every filter bounded by X is read against the number that was named.
    /// </summary>
    [RuleRef("601.2b")]
    private static int? Revealing(SmokeBoard board, Amount? count)
    {
        if (count is X && board.AnnouncedX is not null)
            board.AnnouncedX = RevealedX;
        return Number(board, count ?? new Fixed(1)) is { } revealed && revealed > 0 && revealed <= LibrarySize
            ? revealed
            : null;
    }

    /// <summary>
    /// CR 608.2h: the two keys "revealed this way but not put onto the battlefield" reads — the set
    /// a step remembered, and the one a later step took out of it. Null for any other predicate,
    /// because which cards a filter the board cannot read describes is not a generated scenario's
    /// to guess.
    /// </summary>
    [RuleRef("608.2h")]
    private static (string Shown, string Taken)? Unchosen(Predicate? predicate) =>
        predicate is AllOf { Of: [IsRemembered shown, NotPredicate { Of: IsRemembered taken }] }
            ? (shown.Key, taken.Key)
            : null;

    /// <summary>The cards one key remembered that the other did not, in the order the board stocked them.</summary>
    [RuleRef("608.2h")]
    private static IReadOnlyList<(string Alias, string Card)> Left(SmokeBoard board, (string Shown, string Taken) keys) =>
        [.. board.Recalled(keys.Shown).Where(card => !board.Recalled(keys.Taken).Contains(card))];
}
