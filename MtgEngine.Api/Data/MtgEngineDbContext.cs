using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Data;

public sealed class MtgEngineDbContext : DbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserAvatar> UserAvatars => Set<UserAvatar>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<CollectionCard> CollectionCards => Set<CollectionCard>();
    public DbSet<CardSynergyScore> CardSynergyScores => Set<CardSynergyScore>();
    public DbSet<AiResponseCache> AiResponseCache => Set<AiResponseCache>();
    public DbSet<ForumPost> ForumPosts => Set<ForumPost>();
    public DbSet<ForumComment> ForumComments => Set<ForumComment>();
    public DbSet<CardPriceSnapshot> CardPriceSnapshots => Set<CardPriceSnapshot>();
    public DbSet<CollectionCardEvent> CollectionCardEvents => Set<CollectionCardEvent>();

    public MtgEngineDbContext(DbContextOptions<MtgEngineDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Every <see cref="DateTime"/> in this database is UTC, and is read back saying so.
    /// </summary>
    /// <remarks>
    /// SQLite has no date type: values round-trip through TEXT and materialize as
    /// <see cref="DateTimeKind.Unspecified"/>. System.Text.Json then serializes them with no
    /// trailing <c>Z</c>, and JavaScript reads a bare date-time as *local* — so for a browser at
    /// UTC-5 every timestamp arrived five hours in the future. That is what made the card
    /// modal's History tab read "just now" for five hours, and it applied equally to
    /// <c>addedAt</c>, forum post times and everything else on the wire.
    /// <para>
    /// Applied as a convention rather than per property so a new entity cannot quietly opt out
    /// of it. Writing normalizes a Local value instead of trusting it, because storing local
    /// wall-clock and then labelling it UTC on read would bake in the very error this removes.
    /// </para>
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        base.ConfigureConventions(builder);
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcDateTimeConverter()
            : base(
                v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
                v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
        {
        }
    }

    private sealed class NullableUtcDateTimeConverter : ValueConverter<DateTime?, DateTime?>
    {
        public NullableUtcDateTimeConverter()
            : base(
                v => v.HasValue && v.Value.Kind == DateTimeKind.Local ? v.Value.ToUniversalTime() : v,
                v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v)
        {
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // User
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Username).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(256);
            entity.Property(e => e.PasswordHash).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.PreferencesJson).HasColumnType("TEXT");
            entity.HasIndex(e => e.Username).IsUnique();
            entity.HasIndex(e => e.Email).IsUnique();

            // Profile text. These lengths are the same numbers the request DTO validates
            // with -- SQLite does not enforce HasMaxLength, so the DataAnnotations on
            // UpdateProfileRequest are what actually stops an oversized value; keeping the
            // two in step means a future move off SQLite does not start rejecting rows the
            // API accepts.
            entity.Property(e => e.DisplayName).HasMaxLength(64);
            entity.Property(e => e.Tagline).HasMaxLength(120);
            entity.Property(e => e.Bio).HasMaxLength(2000);
            entity.Property(e => e.FavoriteFormat).HasMaxLength(32);
            entity.Property(e => e.FavoriteCommanderOracleId).HasMaxLength(64);
        });

        // UserAvatar -- one row per user, holding the image bytes.
        modelBuilder.Entity<UserAvatar>(entity =>
        {
            entity.HasKey(e => e.UserId);
            entity.Property(e => e.ContentType).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Data).IsRequired();
            entity.Property(e => e.ETag).IsRequired().HasMaxLength(64);
            entity.Property(e => e.UpdatedAt).IsRequired();

            // Cascade: an avatar is meaningless without its user, and leaving orphaned
            // blobs behind would keep a deleted account's picture reachable by id.
            entity.HasOne<User>()
                .WithOne()
                .HasForeignKey<UserAvatar>(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Collection
        modelBuilder.Entity<Collection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(256);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(256);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.IsDeck).IsRequired().HasDefaultValue(false);
            entity.Property(e => e.Tags)
                .HasColumnType("TEXT")
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => string.IsNullOrEmpty(v)
                        ? new List<string>()
                        : System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<string>()
                );
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();

            // Relationships
            entity.HasMany(e => e.Cards)
                .WithOne(c => c.Collection)
                .HasForeignKey(c => c.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.UserId, e.Name });
        });

        // CollectionCard
        modelBuilder.Entity<CollectionCard>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CollectionId).IsRequired();
            entity.Property(e => e.OracleId).IsRequired().HasMaxLength(256);
            entity.Property(e => e.ScryfallId).HasMaxLength(256);
            entity.Property(e => e.Quantity).IsRequired();
            entity.Property(e => e.QuantityFoil).IsRequired();
            entity.Property(e => e.Notes).HasMaxLength(1000);
            entity.Property(e => e.Board).IsRequired().HasDefaultValue("main");
            entity.Property(e => e.AddedAt).IsRequired();

            // Relationships
            entity.HasOne(e => e.Collection)
                .WithMany(c => c.Cards)
                .HasForeignKey(e => e.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes — one entry per (collection, printing, board); same card can appear in main+side+maybe
            entity.HasIndex(e => e.CollectionId);
            entity.HasIndex(e => new { e.CollectionId, e.OracleId });
            // Only constrains rows that pin a printing. SQLite treats NULLs as distinct,
            // so this index silently permitted any number of duplicate *unpinned* rows
            // for one card — and unpinned rows are the majority (decks rarely pin a
            // printing). The filtered companion below covers them.
            entity.HasIndex(e => new { e.CollectionId, e.ScryfallId, e.Board }).IsUnique();
            // One unpinned row per (collection, card, board). Filtered so it applies only
            // where ScryfallId IS NULL, leaving the index above to police pinned rows.
            entity.HasIndex(e => new { e.CollectionId, e.OracleId, e.Board })
                  .IsUnique()
                  .HasFilter("\"ScryfallId\" IS NULL")
                  .HasDatabaseName("IX_CollectionCards_Unpinned_Unique");
        });

        // CardPriceSnapshot — daily price history for printings owned in collections
        modelBuilder.Entity<CardPriceSnapshot>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ScryfallId).IsRequired().HasMaxLength(256);
            entity.Property(e => e.CapturedAt).IsRequired();

            // One row per printing per day; the history query reads by printing ordered
            // by date, which this index also serves.
            entity.HasIndex(e => new { e.ScryfallId, e.CapturedAt }).IsUnique();
            entity.HasIndex(e => e.CapturedAt); // supports the retention sweep
        });

        // CollectionCardEvent — append-only audit trail behind the card modal's History tab
        modelBuilder.Entity<CollectionCardEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(256);
            entity.Property(e => e.CollectionId).IsRequired();
            entity.Property(e => e.CollectionName).IsRequired().HasMaxLength(256);
            entity.Property(e => e.IsDeck).IsRequired().HasDefaultValue(false);
            entity.Property(e => e.OracleId).IsRequired().HasMaxLength(256);
            entity.Property(e => e.ScryfallId).HasMaxLength(256);
            entity.Property(e => e.SetCode).HasMaxLength(16);
            entity.Property(e => e.Board).IsRequired().HasDefaultValue("main");
            entity.Property(e => e.EventType).IsRequired();
            entity.Property(e => e.CounterpartCollectionName).HasMaxLength(256);
            entity.Property(e => e.CreatedAt).IsRequired();

            // Deliberately NO foreign key to Collections. A cascade would delete the
            // history when the collection goes, and "what happened to this card" is most
            // worth asking precisely about a collection that no longer exists. The
            // denormalised UserId/CollectionName make the row stand on its own.

            // The only read: one user's events for one card, newest first.
            entity.HasIndex(e => new { e.UserId, e.OracleId, e.CreatedAt });
        });

        // CardSynergyScore
        modelBuilder.Entity<CardSynergyScore>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CommanderOracleId).IsRequired().HasMaxLength(256);
            entity.Property(e => e.CardOracleId).IsRequired().HasMaxLength(256);
            entity.Property(e => e.Score).IsRequired();
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ModelVersion).IsRequired().HasMaxLength(64);
            entity.Property(e => e.CreatedAt).IsRequired();
            // Model version is part of the identity, not just a stamp. A card now has a
            // score per scoring mode -- ideal, and one per distinct deck shape -- and
            // keying without the version made writing a deck-aware score clobber the
            // ideal one for the same pair.
            entity.HasIndex(e => new { e.CommanderOracleId, e.CardOracleId, e.ModelVersion }).IsUnique();
        });

        // AiResponseCache
        modelBuilder.Entity<AiResponseCache>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).IsRequired().HasMaxLength(32);
            entity.Property(e => e.CacheKey).IsRequired().HasMaxLength(64);
            entity.Property(e => e.PayloadJson).IsRequired().HasColumnType("TEXT");
            entity.Property(e => e.ModelVersion).IsRequired().HasMaxLength(64);
            entity.Property(e => e.CreatedAt).IsRequired();

            // Lookup is always (kind, key, model version); unique so a hit is unambiguous.
            entity.HasIndex(e => new { e.Kind, e.CacheKey, e.ModelVersion }).IsUnique();
            entity.HasIndex(e => e.CreatedAt); // supports TTL sweeps
        });

        // ForumPost
        modelBuilder.Entity<ForumPost>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DeckId).IsRequired();
            entity.Property(e => e.AuthorId).IsRequired().HasMaxLength(256);
            entity.Property(e => e.AuthorUsername).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.ColorIdentityJson).IsRequired().HasColumnType("TEXT").HasDefaultValue("[]");
            entity.Property(e => e.PublishedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();

            entity.HasMany(e => e.Comments)
                .WithOne(c => c.ForumPost)
                .HasForeignKey(c => c.ForumPostId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.DeckId).IsUnique();
            entity.HasIndex(e => e.AuthorId);
            entity.HasIndex(e => e.PublishedAt);
        });

        // ForumComment
        modelBuilder.Entity<ForumComment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ForumPostId).IsRequired();
            entity.Property(e => e.AuthorId).IsRequired().HasMaxLength(256);
            entity.Property(e => e.AuthorUsername).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Content).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();

            entity.HasOne(e => e.ForumPost)
                .WithMany(p => p.Comments)
                .HasForeignKey(e => e.ForumPostId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.ForumPostId);
        });
    }
}
