using System.Collections.Concurrent;

namespace MtgEngine.Api.Services;

/// <summary>
/// One player asking another for a game, with the deck they intend to bring.
/// </summary>
/// <remarks>
/// Each player names their own deck: the inviter when they invite, the opponent when they
/// accept. That is not a courtesy — a deck list is not public, and a lobby that let you pick
/// somebody else's deck would need an endpoint that hands out everyone's, which is a privacy
/// hole opened to save a click.
/// </remarks>
public sealed record GameInvite
{
    public required Guid Id { get; init; }

    public required Guid FromUserId { get; init; }

    public required string FromUserName { get; init; }

    public required Guid FromDeckId { get; init; }

    public required Guid ToUserId { get; init; }

    public required int StartingLife { get; init; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Outstanding invitations, in memory alongside the games they become.
/// </summary>
/// <remarks>
/// In memory for the same reason sessions are: an invitation that outlives the process it was
/// made in is an invitation to a game the server no longer has. They expire on their own so a
/// forgotten invite does not sit in someone's list forever.
/// </remarks>
public sealed class GameInviteService
{
    private readonly ConcurrentDictionary<Guid, GameInvite> _invites = new();

    /// <summary>How long an unanswered invitation stands.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromHours(1);

    public GameInvite Create(
        Guid fromUserId, string fromUserName, Guid fromDeckId, Guid toUserId, int startingLife)
    {
        if (fromUserId == toUserId)
            throw new InvalidResourceStateException("You cannot invite yourself to a game.");

        var invite = new GameInvite
        {
            Id = Guid.NewGuid(),
            FromUserId = fromUserId,
            FromUserName = fromUserName,
            FromDeckId = fromDeckId,
            ToUserId = toUserId,
            StartingLife = startingLife,
        };

        _invites[invite.Id] = invite;
        return invite;
    }

    /// <summary>Invitations waiting for this player to answer.</summary>
    public IReadOnlyList<GameInvite> For(Guid userId) =>
        [.. Live().Where(i => i.ToUserId == userId).OrderByDescending(i => i.CreatedUtc)];

    /// <summary>Invitations this player has sent and nobody has answered.</summary>
    public IReadOnlyList<GameInvite> SentBy(Guid userId) =>
        [.. Live().Where(i => i.FromUserId == userId).OrderByDescending(i => i.CreatedUtc)];

    /// <summary>
    /// Takes an invitation, so it can only be accepted once.
    /// </summary>
    /// <remarks>
    /// Removing on read is what makes a double-tap safe: two accepts race, one wins the
    /// dictionary, and the loser gets null rather than a second game against the same deck.
    /// </remarks>
    public GameInvite? Take(Guid inviteId, Guid byUserId)
    {
        if (!_invites.TryGetValue(inviteId, out var invite) || invite.ToUserId != byUserId)
            return null;

        return _invites.TryRemove(inviteId, out var taken) ? taken : null;
    }

    /// <summary>Withdraws an invitation the caller sent.</summary>
    public bool Withdraw(Guid inviteId, Guid byUserId) =>
        _invites.TryGetValue(inviteId, out var invite)
        && invite.FromUserId == byUserId
        && _invites.TryRemove(inviteId, out _);

    private IEnumerable<GameInvite> Live()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var invite in _invites.Values)
        {
            if (now - invite.CreatedUtc > Expiry)
            {
                _invites.TryRemove(invite.Id, out _);
                continue;
            }

            yield return invite;
        }
    }
}
