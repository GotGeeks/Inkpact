using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Inkpact.Api.Tests.History;

public class HistorySchemaTests(PostgresFixture postgres) : DatabaseTestBase(postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_new_workspace_has_the_default_policy_and_can_be_created_with_history_disabled()
    {
        await using (var db = CreateContext())
        {
            var user = new User { Email = "owner@example.com", DisplayName = "Owner", CreatedAt = Now };
            db.AddRange(
                user,
                new Workspace { Name = "Default", OwnerId = user.Id, CreatedAt = Now },
                new Workspace { Name = "Disabled", OwnerId = user.Id, CreatedAt = Now, HistoryEnabled = false });
            await db.SaveChangesAsync();
        }

        await using var read = CreateContext();
        var standard = await read.Workspaces.SingleAsync(w => w.Name == "Default");
        standard.HistoryEnabled.ShouldBeTrue();
        standard.HistoryGranularHours.ShouldBe(24);
        standard.HistoryDailyDays.ShouldBe(30);
        standard.HistoryExpiryDays.ShouldBe(365);
        standard.HistoryCapBytes.ShouldBeNull();
        (await read.Workspaces.SingleAsync(w => w.Name == "Disabled")).HistoryEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Versions_keep_their_payload_when_the_author_goes_and_are_deleted_with_the_page()
    {
        var owner = new User { Email = "owner@example.com", DisplayName = "Owner", CreatedAt = Now };
        var author = new User { Email = "author@example.com", DisplayName = "Author", CreatedAt = Now };
        var workspace = new Workspace { Name = "Workspace", OwnerId = owner.Id, CreatedAt = Now };
        var page = new Page { WorkspaceId = workspace.Id, Title = "Current", SortKey = "a0", CreatedAt = Now, UpdatedAt = Now };
        byte[] source = [0, 0];
        var version = new PageVersion(
            page.Id, source, "Earlier", "\u00e9\U00010437", Now, author.Id, PageVersionKind.BeforeRestore, Now.AddHours(24));
        source[0] = 9;

        await using (var db = CreateContext())
        {
            db.AddRange(owner, author, workspace, page, version);
            await db.SaveChangesAsync();
            db.Users.Remove(author);
            await db.SaveChangesAsync();
        }

        await using var read = CreateContext();
        var saved = await read.PageVersions.SingleAsync();
        saved.State.ShouldBe([(byte)0, 0]);
        saved.Title.ShouldBe("Earlier");
        saved.FileText.ShouldBe("\u00e9\U00010437");
        saved.CreatedAt.ShouldBe(Now);
        saved.CreatedBy.ShouldBeNull();
        saved.Kind.ShouldBe(PageVersionKind.BeforeRestore);
        saved.ProtectedUntil.ShouldBe(Now.AddHours(24));
        saved.PayloadBytes.ShouldBe(8);
        var callerCopy = saved.State;
        callerCopy[0] = 9;
        saved.State.ShouldBe([(byte)0, 0]);
        await read.SaveChangesAsync();

        await using (var verify = CreateContext())
        {
            (await verify.PageVersions.AsNoTracking().SingleAsync()).State.ShouldBe([(byte)0, 0]);
        }

        read.Pages.Remove(await read.Pages.SingleAsync(p => p.Id == page.Id));
        await read.SaveChangesAsync();
        (await read.PageVersions.CountAsync()).ShouldBe(0);
    }
}

public class HistoryUpgradeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task An_existing_workspace_gains_default_history_settings_without_inventing_versions()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var db = new InkpactDbContext(new DbContextOptionsBuilder<InkpactDbContext>().UseNpgsql(connectionString).Options);
        var before = db.Database.GetMigrations().Single(m => m.EndsWith("_AddFavoritesAndVisits", StringComparison.Ordinal));
        await db.GetService<IMigrator>().MigrateAsync(before);

        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO users (id, email, normalized_email, display_name, created_at, is_admin)
            VALUES ({userId}, 'old@example.com', 'old@example.com', 'Old', now(), true)
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO workspaces (id, name, owner_id, created_at)
            VALUES ({workspaceId}, 'Old workspace', {userId}, now())
            """);

        await db.Database.MigrateAsync();

        var workspace = await db.Workspaces.AsNoTracking().SingleAsync();
        workspace.HistoryEnabled.ShouldBeTrue();
        workspace.HistoryGranularHours.ShouldBe(24);
        workspace.HistoryDailyDays.ShouldBe(30);
        workspace.HistoryExpiryDays.ShouldBe(365);
        workspace.HistoryCapBytes.ShouldBeNull();
        (await db.PageVersions.CountAsync()).ShouldBe(0);
    }
}
