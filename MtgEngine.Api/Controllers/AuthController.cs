using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MtgEngine.Api.Dtos;
using MtgEngine.Api.Services;

namespace MtgEngine.Api.Controllers;

[ApiController, Route("api/auth"), EnableRateLimiting("auth")]
public sealed class AuthController(AccountManagementService accounts) : ControllerBase
{
    private const string CookieName = "compleat_refresh";
    [AllowAnonymous, HttpPost("register")]
    public async Task<ActionResult<AuthTokenResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        var result = await accounts.RegisterAsync(request, Device(), ct).ConfigureAwait(false);
        SetRefresh(Response, result);
        return Ok(result.Response);
    }
    [AllowAnonymous, HttpPost("login")]
    public async Task<ActionResult<AuthTokenResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await accounts.LoginAsync(request, Device(), ct).ConfigureAwait(false);
        SetRefresh(Response, result, request.RememberMe);
        return Ok(result.Response);
    }
    [AllowAnonymous, HttpPost("refresh")]
    public async Task<ActionResult<AuthTokenResponse>> Refresh(RefreshSessionRequest request, CancellationToken ct)
    {
        var result = await accounts.RefreshAsync(Request.Cookies[CookieName] ?? "", ct).ConfigureAwait(false);
        SetRefresh(Response, result, request.RememberMe);
        return Ok(result.Response);
    }
    [AllowAnonymous, HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        Guid? id = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var user) ? user : null;
        Guid? family = Guid.TryParse(User.FindFirstValue("session"), out var session) ? session : null;
        await accounts.SignOutAsync(Request.Cookies[CookieName] ?? "", id, family, ct).ConfigureAwait(false);
        ClearRefresh(Response);
        return NoContent();
    }
    [AllowAnonymous, HttpPost("password/forgot")]
    public async Task<IActionResult> Forgot(ForgotAccountPasswordRequest request, CancellationToken ct)
    {
        await accounts.ForgotAsync(request.Email, ct).ConfigureAwait(false);
        return NoContent();
    }
    [AllowAnonymous, HttpPost("password/reset")]
    public async Task<IActionResult> Reset(ResetAccountPasswordRequest request, CancellationToken ct)
    {
        await accounts.ResetAsync(request, ct).ConfigureAwait(false);
        ClearRefresh(Response);
        return NoContent();
    }
    private string Device() => Request.Headers.UserAgent.ToString();
    private static void SetRefresh(HttpResponse response, AccountSignIn result, bool rememberMe = true) => response.Cookies.Append(CookieName, result.RefreshToken,
        new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/api/auth", Expires = rememberMe ? result.RefreshExpiresAt : null });
    public static void ClearRefresh(HttpResponse response) => response.Cookies.Delete(CookieName,
        new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/api/auth" });
}
