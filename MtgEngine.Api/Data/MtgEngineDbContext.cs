using Microsoft.EntityFrameworkCore;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Data;

public sealed class MtgEngineDbContext : DbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<CollectionCard> CollectionCards => Set<CollectionCard>();
    public DbSet<CardSynergyScore> CardSynergyScores => Set<CardSynergyScore>();
    public DbSet<AiResponseCache> AiResponseCache => Set<AiResponseCache>();
    public DbSet<ForumPost> ForumPosts => Set<ForumPost>();
    public DbSet<ForumComment> ForumComments => Set<ForumComment>();
    public DbSet<CardPriceSnapshot> CardPriceSnapshots => Set<CardPriceSnapshot>();

    public MtgEngineDbContext(DbContextOptions<MtgEngineDbContext> options)
        : base(options)
    {
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
