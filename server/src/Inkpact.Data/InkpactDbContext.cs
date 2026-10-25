using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Data;

public class InkpactDbContext(DbContextOptions<InkpactDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<Page> Pages => Set<Page>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("users");
            user.Property(u => u.Email).HasMaxLength(320).IsRequired();
            user.Property(u => u.NormalizedEmail).HasMaxLength(320).IsRequired();
            user.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
            user.Property(u => u.OidcSubject).HasMaxLength(512);
            user.HasIndex(u => u.NormalizedEmail).IsUnique();
            user.HasIndex(u => u.OidcSubject).IsUnique().HasFilter("oidc_subject IS NOT NULL");
        });

        modelBuilder.Entity<Workspace>(workspace =>
        {
            workspace.ToTable("workspaces");
            workspace.Property(w => w.Name).HasMaxLength(200).IsRequired();
            workspace.HasOne(w => w.Owner).WithMany().HasForeignKey(w => w.OwnerId).OnDelete(DeleteBehavior.Restrict);
            workspace.HasIndex(w => w.OwnerId);
        });

        modelBuilder.Entity<Page>(page =>
        {
            page.ToTable("pages");
            page.Property(p => p.Title).HasMaxLength(500).IsRequired();
            // Fractional index keys must sort byte by byte, whatever the database locale is.
            page.Property(p => p.SortKey).HasMaxLength(100).UseCollation("C").IsRequired();
            page.Property(p => p.ExternalSourceType).HasMaxLength(50);
            page.Property(p => p.ExternalId).HasMaxLength(200);
            page.HasOne(p => p.Workspace).WithMany().HasForeignKey(p => p.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            page.HasOne(p => p.Parent).WithMany().HasForeignKey(p => p.ParentId).OnDelete(DeleteBehavior.Restrict);
            page.HasIndex(p => new { p.WorkspaceId, p.ParentId, p.SortKey });
            page.HasIndex(p => new { p.WorkspaceId, p.ExternalSourceType, p.ExternalId })
                .IsUnique()
                .HasFilter("external_id IS NOT NULL");
        });

        modelBuilder.UseSnakeCaseNames();
    }
}
