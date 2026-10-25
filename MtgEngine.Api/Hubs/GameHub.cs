using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using MtgEngine.Rules.Views;

namespace MtgEngine.Api.Hubs;

/// <summary>
/// Real-time play: a client sends an intent, the server decides, every seat gets its own view.
/// </summary>
/// <remarks>
/// Three rules this hub exists to keep.
/// <para>
/// <b>Each player is sent a view built for them, never the state.</b> A previous hub broadcast
/// one state object to a group containing everyone at the table, which handed each player the
/// other's hand and both libraries. Every push here goes through
/// <see cref="GameSession.ProjectFor"/> and is addressed to one account.
/// </para>
/// <para>
/// <b>The seat comes from the token.</b> <see cref="Me"/> is the signed-in account and
/// <see cref="GameSession.SeatOf"/> turns it into a seat; no payload has anywhere to put a
/// player id. A client that could name its own player could act as its opponent.
/// </para>
/// <para>
/// <b>The server is authoritative in the strong sense.</b> A refused command changes nothing and
/// only the caller hears why, with the rule that says so. There is no client-side rules check
/// to disagree with.
/// </para>
/// </remarks>
[Authorize]
public sealed class GameHub(GameSessionService sessions, ILogger<GameHub> logger, GameArtworkService? artwork = null) : Hub
{
    /// <summary>The signed-in account. Taken from the token, never from the message.</summary>
    private string Me =>
        Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id
            ? id
            : throw new HubException("Not signed in.");

    private static string Group(Guid gameId) => $"game:{gameId:N}";

    /// <summary>Joins a table the caller is seated at and receives their view.</summary>
    public async Task Join(Guid gameId)
    {
        var (session, seat) = await SeatAsync(gameId).ConfigureAwait(false);
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(gameId)).ConfigureAwait(false);
        await PushAsync(session, Me, seat).ConfigureAwait(false);
    }

    /// <summary>Leaves the group. The seat is kept, so the player can come back to it.</summary>
    public Task Leave(Guid gameId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(gameId));

    /// <summary>
    /// One command, one method.
    /// </summary>
    /// <remarks>
    /// Every action a player can take arrives here as a <see cref="GameCommandDto"/>
    /// discriminated by its <c>kind</c>. A method per action with optional parameters is how the
    /// previous hub ended up unable to express half the engine's costs; this cannot drift
    /// without failing to compile.
    /// </remarks>
    public async Task Command(Guid gameId, GameCommandDto command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (session, seat) = await SeatAsync(gameId).ConfigureAwait(false);

        if (!Validate(command, out var invalid))
        {
            await RefuseAsync(invalid, null).ConfigureAwait(false);
            return;
        }

        if (command.IsManual && await RefuseManualAsync(session, command).ConfigureAwait(false))
            return;

        GameResult result;
        try
        {
            result = await sessions.ApplyAsync(session, command.ToCommand(seat), Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (InvalidRequestException ex)
        {
            // A malformed payload the DTO could not reject on its own — an unknown zone name.
            // Its message is written by us and names no internals.
            await RefuseAsync(ex.Message, null).ConfigureAwait(false);
            return;
        }

        if (result is Refused refused)
        {
            await RefuseAsync(refused.Reason, refused.Rule).ConfigureAwait(false);
            return;
        }

        // Prepare sequentially: artwork may use a scoped database context. Delivery
        // is independent per account so one slow connection cannot hold later seats.
        var deliveries = new List<Delivery>();
        foreach (var (userId, player) in session.SeatedPlayers())
            deliveries.Add(await PrepareAsync(session, userId, player).ConfigureAwait(false));
        await Task.WhenAll(deliveries.Select(SendAsync)).ConfigureAwait(false);
    }

    /// <summary>
    /// Honour-system actions are accepted only where the table left something to the players.
    /// </summary>
    /// <remarks>
    /// The fourth rule of the card plan reaching the table: a card the engine enforces is played
    /// by the engine, and a manual action on it is a refusal rather than an override. Otherwise
    /// a player could quietly hand-wave a card the server was already playing correctly, and
    /// the log would show two authorities for one object.
    /// </remarks>
    private async Task<bool> RefuseManualAsync(GameSession session, GameCommandDto command)
    {
        if (!session.AllowHonourSystem)
        {
            await RefuseAsync("This table enforces every card; manual actions are not accepted here.", null).ConfigureAwait(false);
            return true;
        }

        if (command.Subject is { } subject && GameSessionService.IsEnforced(session, subject))
        {
            await RefuseAsync("The engine plays that card; it cannot be handled manually.", null).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>Sends one account its view, its legal actions and any question that is waiting for it.</summary>
    private async Task PushAsync(GameSession session, string userId, PlayerId player)
    {
        await SendAsync(await PrepareAsync(session, userId, player).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private sealed record Delivery(string UserId, PlayerId Player, GameView? View, LegalActions? Legal,
        IReadOnlyDictionary<string, GameArtwork>? Images);

    private async Task<Delivery> PrepareAsync(GameSession session, string userId, PlayerId player)
    {
        var (view, legal) = session.ProjectFor(player);
        var images = artwork is null ? null : await artwork.ForAsync(view, Context.ConnectionAborted).ConfigureAwait(false);
        return new Delivery(userId, player, view, legal, images);
    }

    private async Task SendAsync(Delivery delivery)
    {
        var recipient = Clients.User(delivery.UserId);
        await recipient.SendAsync("ViewUpdated", delivery.View, delivery.Legal, delivery.Images).ConfigureAwait(false);
        if (delivery.View?.Pending is { } pending && pending.Player == delivery.Player)
            await recipient.SendAsync("ChoiceRequested", pending).ConfigureAwait(false);
    }

    private Task RefuseAsync(string reason, string? rule)
    {
        logger.LogDebug("Refused: {Reason} ({Rule})", reason, rule);
        return Clients.Caller.SendAsync("Refused", reason, rule);
    }

    /// <summary>The table and the seat the caller holds, or a hub error naming neither.</summary>
    private async Task<(GameSession Session, PlayerId Seat)> SeatAsync(Guid gameId)
    {
        GameSession session;
        try
        {
            session = await sessions.FindAsync(gameId, Context.ConnectionAborted).ConfigureAwait(false)
                ?? throw new HubException("No such game.");
        }
        catch (UnsupportedGameLogException)
        {
            throw new HubException(UnsupportedGameLogException.Detail);
        }
        return session.SeatOf(Me) is { } seat
            ? (session, seat)
            : throw new HubException("You are not seated at that table.");
    }

    /// <summary>
    /// Runs the payload's DataAnnotations, which MVC would have run for a controller and does
    /// not run for a hub. Without this the caps on the DTOs are decoration.
    /// </summary>
    /// <remarks>
    /// The caps live on the constructor parameters, because <c>[property: ...]</c> on a
    /// positional record throws in MVC's model binder and this repo learned that the expensive
    /// way. <see cref="Validator"/> only reads property attributes, so each parameter is matched
    /// back to its property by name — without this the caps on the DTOs would be decoration.
    /// </remarks>
    private static bool Validate(GameCommandDto command, out string reason)
    {
        var results = new List<ValidationResult>();
        var type = command.GetType();
        Validator.TryValidateObject(command, new ValidationContext(command), results, validateAllProperties: true);

        foreach (var parameter in type.GetConstructors().FirstOrDefault()?.GetParameters() ?? [])
        {
            var attributes = parameter.GetCustomAttributes(typeof(ValidationAttribute), inherit: true)
                .Cast<ValidationAttribute>()
                .ToArray();
            if (attributes.Length == 0 || parameter.Name is null)
                continue;

            var property = type.GetProperty(parameter.Name);
            if (property is null)
                continue;

            Validator.TryValidateValue(
                property.GetValue(command)!,
                new ValidationContext(command) { MemberName = property.Name },
                results,
                attributes);
        }

        reason = results.Count == 0
            ? string.Empty
            : results[0].ErrorMessage ?? "That command is not well formed.";
        return results.Count == 0;
    }
}
