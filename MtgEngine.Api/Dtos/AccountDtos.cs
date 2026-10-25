using System.ComponentModel.DataAnnotations;

namespace MtgEngine.Api.Dtos;

public sealed record AccountDetails(Guid Id, string Username, string Email, bool EmailVerified, DateTime CreatedAt);
public sealed record AccountSessionDetails(Guid Id, string DeviceLabel, DateTime CreatedAt, DateTime LastUsedAt, DateTime ExpiresAt, bool Current);
public sealed record AccountCodeRequest([Required, RegularExpression("^[0-9]{6}$")] string Code);
public sealed record ForgotAccountPasswordRequest([Required, EmailAddress, StringLength(254)] string Email);
public sealed record ResetAccountPasswordRequest(
    [Required, EmailAddress, StringLength(254)] string Email,
    [Required, RegularExpression("^[0-9]{6}$")] string Code,
    [Required, StringLength(128, MinimumLength = 8)] string NewPassword);
public sealed record ChangeAccountPasswordRequest(
    [Required, StringLength(128)] string CurrentPassword,
    [Required, StringLength(128, MinimumLength = 8)] string NewPassword);
public sealed record ChangeAccountEmailRequest(
    [Required, StringLength(128)] string CurrentPassword,
    [Required, EmailAddress, StringLength(254)] string Email);
public sealed record DeleteAccountRequest([Required, StringLength(128)] string Password);

public sealed record AccountSignIn(AuthTokenResponse Response, string RefreshToken, DateTime RefreshExpiresAt);

public sealed record ChangeUsernameRequest([Required, StringLength(32, MinimumLength = 3)] string Username, [Required, StringLength(128)] string CurrentPassword);
public sealed record DeckPrivacy(bool HideProfileDecks);
public sealed record UsernameAvailability(bool Available);
