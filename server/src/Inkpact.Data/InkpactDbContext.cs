using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Data;

public class InkpactDbContext(DbContextOptions<InkpactDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<Page> Pages => Set<Page>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<LoginThrottle> LoginThrottles => Set<LoginThrottle>();

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

        modelBuilder.Entity<Session>(session =>
        {
            session.ToTable("sessions");
            session.HasKey(s => s.Id);
            session.Property(s => s.Id).HasMaxLength(64);
            session.Property(s => s.UserAgent).HasMaxLength(256);
            session.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            session.HasIndex(s => s.UserId);
            session.HasIndex(s => s.ExpiresAt);
        });

        modelBuilder.Entity<WorkspaceMember>(member =>
        {
            member.ToTable("workspace_members");
            member.HasKey(m => new { m.WorkspaceId, m.UserId });
            // Stored as text ("owner", "editor", "viewer"), so the table reads without a key to the numbers.
            member.Property(m => m.Role)
                .HasConversion(role => role.ToString().ToLowerInvariant(), text => Enum.Parse<WorkspaceRole>(text, true))
                .HasMaxLength(16);
            member.HasOne(m => m.Workspace).WithMany().HasForeignKey(m => m.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            member.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            member.HasIndex(m => m.UserId);
        });

        modelBuilder.Entity<AuditEvent>(audit =>
        {
            audit.ToTable("audit_events");
            audit.Property(a => a.Type).HasMaxLength(64).IsRequired();
            audit.Property(a => a.Email).HasMaxLength(320);
            audit.Property(a => a.Ip).HasMaxLength(64);
            audit.Property(a => a.UserAgent).HasMaxLength(256);
            // A deleted user leaves their history behind, without the link.
            audit.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.SetNull);
            audit.HasIndex(a => a.OccurredAt);
            audit.HasIndex(a => new { a.UserId, a.OccurredAt });
        });

        modelBuilder.Entity<LoginThrottle>(throttle =>
        {
            throttle.ToTable("login_throttles");
            throttle.HasKey(t => t.Key);
            throttle.Property(t => t.Key).HasMaxLength(64);
        });

        modelBuilder.UseSnakeCaseNames();
    }
}
