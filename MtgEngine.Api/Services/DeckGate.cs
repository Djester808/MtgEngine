using System.Collections.Immutable;
using MtgEngine.Cards;

namespace MtgEngine.Api.Services;

/// <summary>One card the gate let through, and on what terms.</summary>
/// <remarks>
/// <paramref name="Enforced"/> is what the table does with it: an enforced object is played by
/// the engine and refuses a manual action, an unenforced one is badged and handled by the
/// players. Nothing is half-enforced.
/// </remarks>
public sealed record AdmittedCard(string OracleId, string Name, ImplementationStatus Status, bool Enforced);

/// <summary>One card the gate turned away, and the reason a player can read.</summary>
public sealed record RefusedCard(string OracleId, string Name, string Why);

/// <summary>What a deck may do at this table.</summary>
public sealed record DeckAdmission(ImmutableList<AdmittedCard> Cards)
{
    /// <summary>The oracle ids the engine will enforce. The hub refuses a manual action on any of these.</summary>
    public ImmutableHashSet<string> EnforcedOracleIds =>
        ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, Cards.Where(c => c.Enforced).Select(c => c.OracleId));

    public bool AllEnforced => Cards.All(c => c.Enforced);
}

/// <summary>
/// Whether a deck may sit down at a table, card by card.
/// </summary>
/// <remarks>
/// The fourth rule of the card plan, and the only thing standing between a half-translated card
/// and a game that silently plays it wrong: <b>the gate reads one status field</b>. A card is
/// enforced (<see cref="ImplementationStatus.Verified"/>), badged honour-system
/// (<see cref="ImplementationStatus.Translated"/>, <see cref="ImplementationStatus.Disputed"/>,
/// <see cref="ImplementationStatus.Untranslated"/>, and only when the table said so when it was
/// created), or refused. <see cref="ImplementationStatus.Excluded"/> is refused at every table:
/// those cards are on the published list because the engine cannot represent them at all —
/// subgames, dexterity, ante — and no table setting makes that untrue.
/// <para>
/// A refusal names every card it turned away and why, because a deck of a hundred cards that is
/// told only "not allowed" cannot be fixed. The message is capped so it still reads inside a
/// sheet on a phone; the full list is what the client asks for next.
/// </para>
/// </remarks>
public sealed class DeckGate(ICardFilePool pool)
{
    /// <summary>How many refused cards a refusal names before it stops and counts the rest.</summary>
    internal const int NamedInRefusal = 5;

    private readonly ICardFilePool _pool = pool;

    /// <summary>
    /// Admits a deck, or throws naming every card it refused.
    /// </summary>
    /// <param name="oracleIds">Every card in the deck, including the command zone. Duplicates are fine.</param>
    /// <param name="allowHonourSystem">The table setting, chosen when the table was created and never after.</param>
    /// <exception cref="InvalidRequestException">At least one card cannot sit at this table.</exception>
    public DeckAdmission Admit(IReadOnlyList<string> oracleIds, bool allowHonourSystem)
    {
        ArgumentNullException.ThrowIfNull(oracleIds);

        var admitted = ImmutableList.CreateBuilder<AdmittedCard>();
        var refused = new List<RefusedCard>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var oracleId in oracleIds)
        {
            if (string.IsNullOrWhiteSpace(oracleId) || !seen.Add(oracleId))
                continue;

            var status = _pool.StatusOf(oracleId);
            if (status.Status == ImplementationStatus.Verified)
            {
                admitted.Add(new AdmittedCard(status.OracleId, status.Name, status.Status, Enforced: true));
            }
            else if (status.Status == ImplementationStatus.Excluded)
            {
                refused.Add(new RefusedCard(status.OracleId, status.Name, status.Reason is { Length: > 0 } why
                    ? "cannot be represented: " + why
                    : "cannot be represented"));
            }
            else if (allowHonourSystem)
            {
                admitted.Add(new AdmittedCard(status.OracleId, status.Name, status.Status, Enforced: false));
            }
            else
            {
                refused.Add(new RefusedCard(status.OracleId, status.Name, Unverified(status.Status)));
            }
        }

        if (refused.Count > 0)
            throw new InvalidRequestException(Explain(refused, allowHonourSystem));

        return new DeckAdmission(admitted.ToImmutable());
    }

    private static string Unverified(ImplementationStatus status) => status switch
    {
        ImplementationStatus.Translated => "not verified yet",
        ImplementationStatus.Disputed => "two readings disagree",
        _ => "not translated yet",
    };

    private static string Explain(IReadOnlyList<RefusedCard> refused, bool allowHonourSystem)
    {
        var named = string.Join("; ", refused.Take(NamedInRefusal).Select(c => $"{c.Name} ({c.Why})"));
        var rest = refused.Count > NamedInRefusal ? $"; and {refused.Count - NamedInRefusal} more" : string.Empty;
        var advice = allowHonourSystem
            ? string.Empty
            : " This table enforces every card; a table that allows honour-system cards would admit the unverified ones.";
        return $"{refused.Count} card(s) cannot be played here: {named}{rest}.{advice}";
    }
}
