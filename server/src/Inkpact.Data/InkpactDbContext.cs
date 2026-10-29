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

    public DbSet<PageUpdate> PageUpdates => Set<PageUpdate>();

    public DbSet<PageSnapshot> PageSnapshots => Set<PageSnapshot>();

    public DbSet<ArchivedPageUpdate> ArchivedPageUpdates => Set<ArchivedPageUpdate>();

    public DbSet<Attachment> Attachments => Set<Attachment>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<PageTag> PageTags => Set<PageTag>();

    public DbSet<Reminder> Reminders => Set<Reminder>();

    public DbSet<ImportRun> Imports => Set<ImportRun>();

    public DbSet<ImportItem> ImportItems => Set<ImportItem>();

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
            page.Property(p => p.ContentText).HasDefaultValue("").IsRequired();
            page.HasOne(p => p.Workspace).WithMany().HasForeignKey(p => p.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            page.HasOne(p => p.Parent).WithMany().HasForeignKey(p => p.ParentId).OnDelete(DeleteBehavior.Restrict);
            // Two live siblings never share a key, also at the top level (where the parent is null).
            page.HasIndex(p => new { p.WorkspaceId, p.ParentId, p.SortKey })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasFilter("deleted_at IS NULL");
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

        modelBuilder.Entity<PageUpdate>(update =>
        {
            update.ToTable("page_updates");
            update.HasKey(u => u.Seq);
            update.Property(u => u.Seq).UseIdentityAlwaysColumn();
            update.Property(u => u.Data).IsRequired();
            update.HasOne<Page>().WithMany().HasForeignKey(u => u.PageId).OnDelete(DeleteBehavior.Cascade);
            update.HasOne<User>().WithMany().HasForeignKey(u => u.UserId).OnDelete(DeleteBehavior.SetNull);
            // A page is loaded as its updates in order.
            update.HasIndex(u => new { u.PageId, u.Seq });
        });

        modelBuilder.Entity<PageSnapshot>(snapshot =>
        {
            snapshot.ToTable("page_snapshots");
            snapshot.HasKey(s => s.PageId);
            snapshot.Property(s => s.State).IsRequired();
            snapshot.Property(s => s.StateVector).IsRequired();
            snapshot.Property(s => s.HistoryComplete).HasDefaultValue(true);
            snapshot.HasOne<Page>().WithOne().HasForeignKey<PageSnapshot>(s => s.PageId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ArchivedPageUpdate>(archived =>
        {
            archived.ToTable("page_updates_archive");
            archived.HasKey(a => a.Seq);
            archived.Property(a => a.Seq).ValueGeneratedNever();
            archived.Property(a => a.Data).IsRequired();
            archived.HasOne<Page>().WithMany().HasForeignKey(a => a.PageId).OnDelete(DeleteBehavior.Cascade);
            archived.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.SetNull);
            // The full-history check reads one page's archive in order; the purge finds the old rows.
            archived.HasIndex(a => new { a.PageId, a.Seq });
            archived.HasIndex(a => a.ArchivedAt);
        });

        modelBuilder.Entity<Attachment>(attachment =>
        {
            attachment.ToTable("attachments");
            attachment.Property(a => a.FileName).HasMaxLength(255).IsRequired();
            attachment.Property(a => a.ContentType).HasMaxLength(127).IsRequired();
            attachment.Property(a => a.Sha256).HasMaxLength(64).IsRequired();
            // The stored file stays when its rows go; the cleanup of unreferenced files deals with it.
            attachment.HasOne<Page>().WithMany().HasForeignKey(a => a.PageId).OnDelete(DeleteBehavior.Cascade);
            attachment.HasOne<User>().WithMany().HasForeignKey(a => a.CreatedBy).OnDelete(DeleteBehavior.SetNull);
            attachment.HasIndex(a => a.PageId);
            // Which rows share a stored file, for the cleanup and the export.
            attachment.HasIndex(a => a.Sha256);
            attachment.Property(a => a.ExternalSourceType).HasMaxLength(50);
            attachment.Property(a => a.ExternalId).HasMaxLength(200);
            // An imported file is on a page once; a page that uses it again finds this row instead of adding another.
            attachment.HasIndex(a => new { a.PageId, a.ExternalSourceType, a.ExternalId })
                .IsUnique()
                .HasFilter("external_id IS NOT NULL");
        });

        modelBuilder.Entity<Tag>(tag =>
        {
            tag.ToTable("tags");
            tag.Property(t => t.Name).HasMaxLength(200).IsRequired();
            tag.Property(t => t.NormalizedName).HasMaxLength(200).IsRequired();
            tag.Property(t => t.ExternalSourceType).HasMaxLength(50);
            tag.Property(t => t.ExternalId).HasMaxLength(200);
            tag.HasOne<Workspace>().WithMany().HasForeignKey(t => t.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            tag.HasIndex(t => new { t.WorkspaceId, t.NormalizedName }).IsUnique();
            tag.HasIndex(t => new { t.WorkspaceId, t.ExternalSourceType, t.ExternalId })
                .IsUnique()
                .HasFilter("external_id IS NOT NULL");
        });

        modelBuilder.Entity<PageTag>(pageTag =>
        {
            pageTag.ToTable("page_tags");
            pageTag.HasKey(t => new { t.PageId, t.TagId });
            pageTag.HasOne<Page>().WithMany().HasForeignKey(t => t.PageId).OnDelete(DeleteBehavior.Cascade);
            pageTag.HasOne<Tag>().WithMany().HasForeignKey(t => t.TagId).OnDelete(DeleteBehavior.Cascade);
            // The pages that have a tag.
            pageTag.HasIndex(t => t.TagId);
        });

        modelBuilder.Entity<Reminder>(reminder =>
        {
            reminder.ToTable("reminders");
            reminder.HasOne<Page>().WithMany().HasForeignKey(r => r.PageId).OnDelete(DeleteBehavior.Cascade);
            reminder.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
            reminder.HasIndex(r => r.PageId);
            // What the delivery job (phase 2) looks for: undelivered reminders by time.
            reminder.HasIndex(r => r.RemindAt).HasFilter("delivered_at IS NULL");
        });

        modelBuilder.Entity<ImportRun>(import =>
        {
            import.ToTable("imports");
            import.Property(i => i.SourceType).HasMaxLength(50).IsRequired();
            import.Property(i => i.FileName).HasMaxLength(255).IsRequired();
            import.Property(i => i.State).HasMaxLength(16).IsRequired();
            import.Property(i => i.ErrorCode).HasMaxLength(64);
            import.Property(i => i.Counts).HasColumnType("jsonb").IsRequired();
            import.Property(i => i.Report).HasColumnType("jsonb").IsRequired();
            import.HasOne<Workspace>().WithMany().HasForeignKey(i => i.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            // A person's imports keep their report when they are removed, without the link.
            import.HasOne<User>().WithMany().HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.SetNull);
            import.HasIndex(i => new { i.WorkspaceId, i.CreatedAt });
            import.HasIndex(i => i.UserId);
            // One waiting or running import per workspace; the upload claims its place by inserting its row.
            import.HasIndex(i => i.WorkspaceId)
                .IsUnique()
                .HasFilter("state IN ('queued', 'running')");
        });

        modelBuilder.Entity<ImportItem>(item =>
        {
            item.ToTable("import_items");
            item.Property(i => i.SourceType).HasMaxLength(50).IsRequired();
            item.Property(i => i.ExternalId).HasMaxLength(200).IsRequired();
            item.Property(i => i.Kind).HasMaxLength(16).IsRequired();
            item.Property(i => i.ContentHash).HasMaxLength(64).IsRequired();
            item.Property(i => i.PageFingerprint).HasMaxLength(32);
            item.Property(i => i.Base).HasColumnType("jsonb");
            item.Property(i => i.ConverterVersion).HasDefaultValue(1);
            item.HasOne<Workspace>().WithMany().HasForeignKey(i => i.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            // What later imports compare against must outlive the report of the import that made it.
            item.HasOne<ImportRun>().WithMany().HasForeignKey(i => i.ImportId).OnDelete(DeleteBehavior.Restrict);
            // An item of the other app is imported once into a workspace: this is how it is found again, and the database
            // refuses a second one.
            item.HasIndex(i => new { i.WorkspaceId, i.SourceType, i.ExternalId }).IsUnique();
            item.HasIndex(i => i.ImportId);
            item.HasIndex(i => i.InkpactId);
        });

        modelBuilder.UseSnakeCaseNames();
    }
}
