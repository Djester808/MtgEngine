using Microsoft.EntityFrameworkCore;
using MtgEngine.Domain.Models;

namespace MtgEngine.Api.Data;

public sealed class MtgEngineDbContext : DbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<CollectionCard> CollectionCards => Set<CollectionCard>();
    public DbSet<CardSynergyScore> CardSynergyScores => Set<CardSynergyScore>();
    public DbSet<ForumPost> ForumPosts => Set<ForumPost>();
    public DbSet<ForumComment> ForumComments => Set<ForumComment>();

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
            entity.HasIndex(e => new { e.CollectionId, e.ScryfallId, e.Board }).IsUnique();
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
            entity.HasIndex(e => new { e.CommanderOracleId, e.CardOracleId }).IsUnique();
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
