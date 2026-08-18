namespace MtgEngine.Domain.Models;

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? PreferencesJson { get; set; }

    // ---- Public profile ------------------------------------------------
    // Everything below is optional and self-authored. It is served to anyone
    // browsing /api/users/{username}, so nothing private belongs here — the
    // email above is deliberately not part of the public profile projection.

    /// <summary>Name shown instead of <see cref="Username"/>. The username stays the identity.</summary>
    public string? DisplayName { get; set; }

    /// <summary>One-line "who I am" under the name.</summary>
    public string? Tagline { get; set; }

    /// <summary>Long-form self description.</summary>
    public string? Bio { get; set; }

    /// <summary>Free text (Commander, Modern, …), not an enum — new formats arrive constantly.</summary>
    public string? FavoriteFormat { get; set; }

    /// <summary>A commander the user pins to their profile. Oracle id, resolved through the card lookup.</summary>
    public string? FavoriteCommanderOracleId { get; set; }

    public DateTime? ProfileUpdatedAt { get; set; }

    /// <summary>
    /// When the avatar last changed, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Lives on the user row rather than being read from <see cref="UserAvatar"/> so that
    /// building a profile — or a whole page of them — never has to touch the blob table
    /// just to decide whether to emit an avatar URL. It doubles as the cache-buster in
    /// that URL.
    /// </remarks>
    public DateTime? AvatarUpdatedAt { get; set; }
}

/// <summary>
/// One user's avatar bytes, kept in its own table.
/// </summary>
/// <remarks>
/// Deliberately not columns on <see cref="User"/>: EF materialises every mapped property
/// of an entity it loads, so a blob there would ride along on the login lookup, the
/// preferences read and every profile projection that only wanted a username. A separate
/// table means the bytes are read exactly once, by the endpoint that streams them.
/// <para>
/// Stored in the database rather than on disk so a backup of the .db is a complete
/// backup, and so no request path ever turns caller-supplied text into a file path.
/// </para>
/// </remarks>
public sealed class UserAvatar
{
    /// <summary>Primary key and foreign key both: one avatar per user.</summary>
    public Guid UserId { get; set; }

    /// <summary>Sniffed from the bytes, never taken from the upload's declared type.</summary>
    public string ContentType { get; set; } = string.Empty;

    public byte[] Data { get; set; } = [];

    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Strong validator for conditional GETs, derived from the stored bytes.</summary>
    public string ETag { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
