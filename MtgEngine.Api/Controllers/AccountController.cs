using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

[ApiController, Authorize, Route("api/account")]
public sealed class AccountController(AccountManagementService accounts) : ControllerBase
{
    private Guid Me => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private Guid? Family => Guid.TryParse(User.FindFirstValue("session"), out var id) ? id : null;

    [HttpGet("username-available"), EnableRateLimiting("auth")]
    public Task<UsernameAvailability> UsernameAvailable([FromQuery] string username, CancellationToken ct) => accounts.UsernameAvailableAsync(Me, username, ct);
    [HttpPut("username"), EnableRateLimiting("auth")]
    public async Task<IActionResult> Username(ChangeUsernameRequest request, CancellationToken ct)
    {
        await accounts.ChangeUsernameAsync(Me, request, ct);
        AuthController.ClearRefresh(Response);
        return NoContent();
    }
    [HttpGet("deck-privacy")]
    public Task<DeckPrivacy> GetDeckPrivacy(CancellationToken ct) => accounts.GetDeckPrivacyAsync(Me, ct);
    [HttpPut("deck-privacy")]
    public Task<DeckPrivacy> SetDeckPrivacy(DeckPrivacy request, CancellationToken ct) => accounts.SetDeckPrivacyAsync(Me, request, ct);

    [HttpGet]
    public Task<AccountDetails> Get(CancellationToken ct) => accounts.GetAsync(Me, ct);
    [HttpGet("sessions")]
    public Task<AccountSessionDetails[]> Sessions(CancellationToken ct) => accounts.SessionsAsync(Me, Family, ct);
    [HttpDelete("sessions/{family:guid}"), EnableRateLimiting("auth")]
    public async Task<IActionResult> Revoke(Guid family, CancellationToken ct)
    {
        await accounts.RevokeAsync(Me, family, ct).ConfigureAwait(false);
        return NoContent();
    }
    [HttpPost("sign-out-all"), EnableRateLimiting("auth")]
    public async Task<IActionResult> SignOutAll(CancellationToken ct)
    {
        await accounts.RevokeAllAsync(Me, ct).ConfigureAwait(false);
        AuthController.ClearRefresh(Response);
        return NoContent();
    }
    [HttpPost("verify-email"), EnableRateLimiting("auth")]
    public async Task<IActionResult> Verify(AccountCodeRequest request, CancellationToken ct)
    {
        await accounts.VerifyAsync(Me, request.Code, ct).ConfigureAwait(false);
        return NoContent();
    }
    [HttpPost("verify-email/resend"), EnableRateLimiting("auth")]
    public async Task<IActionResult> Resend(CancellationToken ct)
    {
        await accounts.ResendAsync(Me, ct).ConfigureAwait(false);
        return NoContent();
    }
    [HttpPut("password"), EnableRateLimiting("auth")]
    public async Task<IActionResult> Password(ChangeAccountPasswordRequest request, CancellationToken ct)
    {
        await accounts.ChangePasswordAsync(Me, request, ct).ConfigureAwait(false);
        AuthController.ClearRefresh(Response);
        return NoContent();
    }
    [HttpPut("email"), EnableRateLimiting("auth")]
    public async Task<IActionResult> Email(ChangeAccountEmailRequest request, CancellationToken ct)
    {
        await accounts.ChangeEmailAsync(Me, request, ct).ConfigureAwait(false);
        AuthController.ClearRefresh(Response);
        return NoContent();
    }
    [HttpDelete, EnableRateLimiting("auth")]
    public async Task<IActionResult> Delete([FromBody] DeleteAccountRequest request, CancellationToken ct)
    {
        await accounts.DeleteAsync(Me, request.Password, ct).ConfigureAwait(false);
        AuthController.ClearRefresh(Response);
        return NoContent();
    }
}
