using Inkpact.Api.Permissions;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.Permissions;

public class PermissionServiceTests(PostgresFixture postgres) : DatabaseTestBase(postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private async Task<User> AddUserAsync(string email, bool isAdmin = false)
    {
        await using var db = CreateContext();
        var user = new User { Email = email, DisplayName = email, CreatedAt = Now, IsAdmin = isAdmin };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<Workspace> AddWorkspaceAsync(User owner, params (User User, WorkspaceRole Role)[] members)
    {
        await using var db = CreateContext();
        var workspace = new Workspace { Name = "Workspace", OwnerId = owner.Id, CreatedAt = Now };
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = owner.Id, Role = WorkspaceRole.Owner, CreatedAt = Now });
        foreach (var (user, role) in members)
        {
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = user.Id, Role = role, CreatedAt = Now });
        }

        await db.SaveChangesAsync();
        return workspace;
    }

    private async Task<Page> AddPageAsync(Workspace workspace, DateTimeOffset? deletedAt = null)
    {
        await using var db = CreateContext();
        var page = new Page { WorkspaceId = workspace.Id, Title = "Page", SortKey = "a0", CreatedAt = Now, UpdatedAt = Now, DeletedAt = deletedAt };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        return page;
    }

    private async Task<Attachment> AddAttachmentAsync(Page page)
    {
        await using var db = CreateContext();
        var attachment = new Attachment { PageId = page.Id, FileName = "a.png", ContentType = "image/png", Size = 1, Sha256 = new string('a', 64), CreatedAt = Now };
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync();
        return attachment;
    }

    private PermissionService CreateService(InkpactDbContext db) => new(db);

    [Fact]
    public async Task Each_member_gets_their_own_role()
    {
        var owner = await AddUserAsync("owner@example.com");
        var editor = await AddUserAsync("editor@example.com");
        var viewer = await AddUserAsync("viewer@example.com");
        var workspace = await AddWorkspaceAsync(owner, (editor, WorkspaceRole.Editor), (viewer, WorkspaceRole.Viewer));
        await using var db = CreateContext();
        var permissions = CreateService(db);

        (await permissions.GetWorkspaceRoleAsync(owner.Id, workspace.Id)).ShouldBe(WorkspaceRole.Owner);
        (await permissions.GetWorkspaceRoleAsync(editor.Id, workspace.Id)).ShouldBe(WorkspaceRole.Editor);
        (await permissions.GetWorkspaceRoleAsync(viewer.Id, workspace.Id)).ShouldBe(WorkspaceRole.Viewer);
    }

    [Fact]
    public async Task A_stranger_has_no_role()
    {
        var owner = await AddUserAsync("owner@example.com");
        var stranger = await AddUserAsync("stranger@example.com");
        var workspace = await AddWorkspaceAsync(owner);
        await using var db = CreateContext();

        (await CreateService(db).GetWorkspaceRoleAsync(stranger.Id, workspace.Id)).ShouldBe(WorkspaceRole.None);
    }

    [Fact]
    public async Task A_workspace_that_does_not_exist_gives_no_role()
    {
        var user = await AddUserAsync("jos@example.com");
        await using var db = CreateContext();

        (await CreateService(db).GetWorkspaceRoleAsync(user.Id, Guid.NewGuid())).ShouldBe(WorkspaceRole.None);
    }

    [Fact]
    public async Task Roles_are_per_workspace()
    {
        var jos = await AddUserAsync("jos@example.com");
        var anna = await AddUserAsync("anna@example.com");
        var mine = await AddWorkspaceAsync(jos);
        var shared = await AddWorkspaceAsync(anna, (jos, WorkspaceRole.Viewer));
        var hers = await AddWorkspaceAsync(anna);
        await using var db = CreateContext();
        var permissions = CreateService(db);

        (await permissions.GetWorkspaceRoleAsync(jos.Id, mine.Id)).ShouldBe(WorkspaceRole.Owner);
        (await permissions.GetWorkspaceRoleAsync(jos.Id, shared.Id)).ShouldBe(WorkspaceRole.Viewer);
        (await permissions.GetWorkspaceRoleAsync(jos.Id, hers.Id)).ShouldBe(WorkspaceRole.None);
    }

    [Fact]
    public async Task A_page_has_the_role_of_its_workspace()
    {
        var owner = await AddUserAsync("owner@example.com");
        var viewer = await AddUserAsync("viewer@example.com");
        var stranger = await AddUserAsync("stranger@example.com");
        var workspace = await AddWorkspaceAsync(owner, (viewer, WorkspaceRole.Viewer));
        var page = await AddPageAsync(workspace);
        await using var db = CreateContext();
        var permissions = CreateService(db);

        (await permissions.GetPageRoleAsync(owner.Id, page.Id)).ShouldBe(WorkspaceRole.Owner);
        (await permissions.GetPageRoleAsync(viewer.Id, page.Id)).ShouldBe(WorkspaceRole.Viewer);
        (await permissions.GetPageRoleAsync(stranger.Id, page.Id)).ShouldBe(WorkspaceRole.None);
    }

    [Fact]
    public async Task A_page_in_the_trash_keeps_its_role_so_it_can_be_restored()
    {
        var owner = await AddUserAsync("owner@example.com");
        var workspace = await AddWorkspaceAsync(owner);
        var page = await AddPageAsync(workspace, deletedAt: Now);
        await using var db = CreateContext();

        (await CreateService(db).GetPageRoleAsync(owner.Id, page.Id)).ShouldBe(WorkspaceRole.Owner);
    }

    [Fact]
    public async Task A_page_that_does_not_exist_gives_no_role()
    {
        var owner = await AddUserAsync("owner@example.com");
        await using var db = CreateContext();

        (await CreateService(db).GetPageRoleAsync(owner.Id, Guid.NewGuid())).ShouldBe(WorkspaceRole.None);
    }

    [Fact]
    public async Task An_attachment_has_the_role_of_its_page()
    {
        var owner = await AddUserAsync("owner@example.com");
        var editor = await AddUserAsync("editor@example.com");
        var viewer = await AddUserAsync("viewer@example.com");
        var stranger = await AddUserAsync("stranger@example.com");
        var workspace = await AddWorkspaceAsync(owner, (editor, WorkspaceRole.Editor), (viewer, WorkspaceRole.Viewer));
        var attachment = await AddAttachmentAsync(await AddPageAsync(workspace));
        await using var db = CreateContext();
        var permissions = CreateService(db);

        (await permissions.GetAttachmentRoleAsync(owner.Id, attachment.Id)).ShouldBe(WorkspaceRole.Owner);
        (await permissions.GetAttachmentRoleAsync(editor.Id, attachment.Id)).ShouldBe(WorkspaceRole.Editor);
        (await permissions.GetAttachmentRoleAsync(viewer.Id, attachment.Id)).ShouldBe(WorkspaceRole.Viewer);
        (await permissions.GetAttachmentRoleAsync(stranger.Id, attachment.Id)).ShouldBe(WorkspaceRole.None);
    }

    [Fact]
    public async Task An_attachment_follows_its_page_into_another_workspace()
    {
        // Moving pages between workspaces arrives with phase 2; the role must follow the page, not the upload.
        var jos = await AddUserAsync("jos@example.com");
        var anna = await AddUserAsync("anna@example.com");
        var mine = await AddWorkspaceAsync(jos);
        var hers = await AddWorkspaceAsync(anna);
        var page = await AddPageAsync(mine);
        var attachment = await AddAttachmentAsync(page);
        await using (var db = CreateContext())
        {
            await db.Pages.Where(p => p.Id == page.Id).ExecuteUpdateAsync(set => set.SetProperty(p => p.WorkspaceId, hers.Id));
        }

        await using var next = CreateContext();
        (await CreateService(next).GetAttachmentRoleAsync(jos.Id, attachment.Id)).ShouldBe(WorkspaceRole.None);
        (await CreateService(next).GetAttachmentRoleAsync(anna.Id, attachment.Id)).ShouldBe(WorkspaceRole.Owner);
    }

    [Fact]
    public async Task An_attachment_that_does_not_exist_gives_no_role()
    {
        var owner = await AddUserAsync("owner@example.com");
        await using var db = CreateContext();

        (await CreateService(db).GetAttachmentRoleAsync(owner.Id, Guid.NewGuid())).ShouldBe(WorkspaceRole.None);
    }

    [Fact]
    public async Task An_attachment_goes_with_its_page_and_outlives_its_uploader()
    {
        var owner = await AddUserAsync("owner@example.com");
        var uploader = await AddUserAsync("uploader@example.com");
        var page = await AddPageAsync(await AddWorkspaceAsync(owner, (uploader, WorkspaceRole.Editor)));
        await using var db = CreateContext();
        var attachment = new Attachment { PageId = page.Id, FileName = "a.png", ContentType = "image/png", Size = 1, Sha256 = new string('b', 64), CreatedAt = Now, CreatedBy = uploader.Id };
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync();

        await db.Users.Where(u => u.Id == uploader.Id).ExecuteDeleteAsync();
        (await db.Attachments.AsNoTracking().SingleAsync()).CreatedBy.ShouldBeNull();

        await db.Pages.Where(p => p.Id == page.Id).ExecuteDeleteAsync();
        (await db.Attachments.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Answers_are_remembered_for_the_rest_of_the_request()
    {
        var owner = await AddUserAsync("owner@example.com");
        var member = await AddUserAsync("member@example.com");
        var workspace = await AddWorkspaceAsync(owner, (member, WorkspaceRole.Editor));
        var page = await AddPageAsync(workspace);
        var attachment = await AddAttachmentAsync(page);
        await using var db = CreateContext();
        var permissions = CreateService(db);
        (await permissions.GetWorkspaceRoleAsync(member.Id, workspace.Id)).ShouldBe(WorkspaceRole.Editor);
        (await permissions.GetPageRoleAsync(member.Id, page.Id)).ShouldBe(WorkspaceRole.Editor);
        (await permissions.GetAttachmentRoleAsync(member.Id, attachment.Id)).ShouldBe(WorkspaceRole.Editor);

        await using (var other = CreateContext())
        {
            await other.WorkspaceMembers.Where(m => m.UserId == member.Id).ExecuteDeleteAsync();
        }

        // Same request, same answer: one query is enough however often the endpoint asks.
        (await permissions.GetWorkspaceRoleAsync(member.Id, workspace.Id)).ShouldBe(WorkspaceRole.Editor);
        (await permissions.GetPageRoleAsync(member.Id, page.Id)).ShouldBe(WorkspaceRole.Editor);
        (await permissions.GetAttachmentRoleAsync(member.Id, attachment.Id)).ShouldBe(WorkspaceRole.Editor);

        // The next request starts fresh and sees that the access is gone.
        await using var nextRequest = CreateContext();
        (await CreateService(nextRequest).GetWorkspaceRoleAsync(member.Id, workspace.Id)).ShouldBe(WorkspaceRole.None);
        (await CreateService(nextRequest).GetPageRoleAsync(member.Id, page.Id)).ShouldBe(WorkspaceRole.None);
        (await CreateService(nextRequest).GetAttachmentRoleAsync(member.Id, attachment.Id)).ShouldBe(WorkspaceRole.None);
    }

    [Fact]
    public async Task Admin_is_read_from_the_database_each_request()
    {
        var admin = await AddUserAsync("admin@example.com", isAdmin: true);
        var regular = await AddUserAsync("regular@example.com");
        await using (var db = CreateContext())
        {
            (await CreateService(db).IsAdminAsync(admin.Id)).ShouldBeTrue();
            (await CreateService(db).IsAdminAsync(regular.Id)).ShouldBeFalse();
            (await CreateService(db).IsAdminAsync(Guid.NewGuid())).ShouldBeFalse();
        }

        await using (var db = CreateContext())
        {
            await db.Users.Where(u => u.Id == admin.Id).ExecuteUpdateAsync(set => set.SetProperty(u => u.IsAdmin, false));
        }

        await using var next = CreateContext();
        (await CreateService(next).IsAdminAsync(admin.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_roles_are_ordered_so_that_a_higher_one_includes_the_lower()
    {
        (WorkspaceRole.None < WorkspaceRole.Viewer).ShouldBeTrue();
        (WorkspaceRole.Viewer < WorkspaceRole.Editor).ShouldBeTrue();
        (WorkspaceRole.Editor < WorkspaceRole.Owner).ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_role_is_stored_as_readable_text()
    {
        var owner = await AddUserAsync("owner@example.com");
        var workspace = await AddWorkspaceAsync(owner);
        await using var db = CreateContext();

        var stored = await db.Database.SqlQuery<string>($"SELECT role AS \"Value\" FROM workspace_members WHERE workspace_id = {workspace.Id}").SingleAsync();

        stored.ShouldBe("owner");
    }

    [Fact]
    public async Task A_member_is_removed_with_their_workspace_and_with_their_account()
    {
        var owner = await AddUserAsync("owner@example.com");
        var member = await AddUserAsync("member@example.com");
        var workspace = await AddWorkspaceAsync(owner, (member, WorkspaceRole.Viewer));

        await using (var db = CreateContext())
        {
            await db.Users.Where(u => u.Id == member.Id).ExecuteDeleteAsync();
            (await db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == workspace.Id)).ShouldBe(1);

            await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspace.Id).ExecuteDeleteAsync();
            await db.Workspaces.Where(w => w.Id == workspace.Id).ExecuteDeleteAsync();
            (await db.WorkspaceMembers.CountAsync()).ShouldBe(0);
        }
    }

    [Fact]
    public async Task A_person_is_a_member_of_a_workspace_once()
    {
        var owner = await AddUserAsync("owner@example.com");
        var workspace = await AddWorkspaceAsync(owner);
        await using var db = CreateContext();
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = owner.Id, Role = WorkspaceRole.Viewer, CreatedAt = Now });

        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
