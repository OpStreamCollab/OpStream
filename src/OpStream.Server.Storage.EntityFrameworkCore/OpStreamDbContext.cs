using Microsoft.EntityFrameworkCore;

namespace OpStream.Server.Storage.EntityFrameworkCore;

/// <summary>
/// Base DbContext for OpStream storage.
/// </summary>
public abstract class OpStreamDbContext : DbContext
{
    protected OpStreamDbContext(DbContextOptions options) : base(options)
    {
    }

    public DbSet<DocumentSnapshotEntity> DocumentSnapshots => Set<DocumentSnapshotEntity>();
    public DbSet<DocumentOpEntity> DocumentOps => Set<DocumentOpEntity>();
    public DbSet<HistoryOpEntity> HistoryOps => Set<HistoryOpEntity>();
    public DbSet<HistorySnapshotEntity> HistorySnapshots => Set<HistorySnapshotEntity>();
    public DbSet<CommentEntity> Comments => Set<CommentEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        if (Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            modelBuilder.HasDefaultSchema("opstream");
        }

        modelBuilder.Entity<DocumentOpEntity>(entity =>
        {
            entity.HasKey(e => new { e.DocumentId, e.Revision });
            entity.HasIndex(e => new { e.DocumentId, e.Revision });
        });

        modelBuilder.Entity<HistoryOpEntity>(entity =>
        {
            entity.HasKey(e => new { e.DocumentId, e.Revision });
            entity.HasIndex(e => new { e.DocumentId, e.Revision });
        });

        modelBuilder.Entity<HistorySnapshotEntity>(entity =>
        {
            entity.HasKey(e => new { e.DocumentId, e.Revision });
            entity.HasIndex(e => new { e.DocumentId, e.Revision });
        });

        modelBuilder.Entity<CommentEntity>(entity =>
        {
            // Covering index for the most common query: open comments per document.
            entity.HasIndex(e => new { e.DocumentId, e.ParentCommentId, e.ResolvedAt });
            // Single-row lookup by comment id is the PK (string, covered above).
        });
    }
}
