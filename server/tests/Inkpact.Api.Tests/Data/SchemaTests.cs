using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Inkpact.Api.Tests.Data;

public class SchemaTests(PostgresFixture postgres) : DatabaseTestBase(postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static User NewUser(string email = "jos@example.com", string? oidcSubject = null) =>
        new() { Email = email, DisplayName = "Jos", CreatedAt = Now, OidcSubject = oidcSubject };

    private async Task<(User User, Workspace Workspace)> SeedWorkspaceAsync()
    {
        await using var db = CreateContext();
        var user = NewUser();
        var workspace = new Workspace { Name = "Private", OwnerId = user.Id, CreatedAt = Now };
        db.AddRange(user, workspace);
        await db.SaveChangesAsync();
        return (user, workspace);
    }

    private static Page NewPage(Guid workspaceId, string sortKey, string title = "Page", Guid? parentId = null) =>
        new() { WorkspaceId = workspaceId, ParentId = parentId, Title = title, SortKey = sortKey, CreatedAt = Now, UpdatedAt = Now };

    [Fact]
    public async Task Email_is_unique_regardless_of_case_and_surrounding_spaces()
    {
        await using var db = CreateContext();
        db.Users.Add(NewUser("Jos@Example.com"));
        await db.SaveChangesAsync();

        await using var other = CreateContext();
        other.Users.Add(NewUser("  jos@example.COM "));

        var exception = await Should.ThrowAsync<DbUpdateException>(() => other.SaveChangesAsync());
        ((PostgresException)exception.InnerException!).ConstraintName.ShouldBe("ix_users_normalized_email");
    }

    [Fact]
    public async Task Email_keeps_the_address_as_typed_and_stores_a_normalized_copy()
    {
        await using (var db = CreateContext())
        {
            db.Users.Add(NewUser("Jos@Example.com"));
            await db.SaveChangesAsync();
        }

        await using var read = CreateContext();
        var user = await read.Users.SingleAsync();
        user.Email.ShouldBe("Jos@Example.com");
        user.NormalizedEmail.ShouldBe("jos@example.com");
    }

    [Fact]
    public async Task Several_users_can_have_no_oidc_subject_but_a_subject_is_unique()
    {
        await using var db = CreateContext();
        db.Users.AddRange(NewUser("a@example.com"), NewUser("b@example.com"), NewUser("c@example.com", "sub-1"));
        await db.SaveChangesAsync();

        await using var other = CreateContext();
        other.Users.Add(NewUser("d@example.com", "sub-1"));

        var exception = await Should.ThrowAsync<DbUpdateException>(() => other.SaveChangesAsync());
        ((PostgresException)exception.InnerException!).ConstraintName.ShouldBe("ix_users_oidc_subject");
    }

    [Fact]
    public async Task Sort_keys_compare_byte_by_byte_so_fractional_indexes_keep_their_order()
    {
        var (_, workspace) = await SeedWorkspaceAsync();
        await using (var db = CreateContext())
        {
            // In a language-aware collation "a" sorts before "B"; fractional keys need uppercase first.
            db.Pages.AddRange(
                NewPage(workspace.Id, "a0", "lower"), NewPage(workspace.Id, "B0", "upper"), NewPage(workspace.Id, "Zz", "last upper"));
            await db.SaveChangesAsync();
        }

        await using var read = CreateContext();
        var titles = await read.Pages.OrderBy(p => p.SortKey).Select(p => p.Title).ToListAsync();
        titles.ShouldBe(["upper", "last upper", "lower"]);
    }

    [Fact]
    public async Task A_page_can_hold_children_and_to_do_and_import_fields()
    {
        var (_, workspace) = await SeedWorkspaceAsync();
        var parent = NewPage(workspace.Id, "a0", "Notebook");
        var child = NewPage(workspace.Id, "a0", "Task", parent.Id);
        child.IsTodo = true;
        child.DueAt = Now.AddDays(1);
        child.CompletedAt = Now;
        child.ExternalSourceType = "joplin";
        child.ExternalId = "abc123";
        child.DeletedAt = Now;
        await using (var db = CreateContext())
        {
            db.Pages.AddRange(parent, child);
            await db.SaveChangesAsync();
        }

        await using var read = CreateContext();
        var loaded = await read.Pages.SingleAsync(p => p.Id == child.Id);
        loaded.ParentId.ShouldBe(parent.Id);
        loaded.IsTodo.ShouldBeTrue();
        loaded.DueAt.ShouldBe(Now.AddDays(1));
        loaded.CompletedAt.ShouldBe(Now);
        loaded.ExternalSourceType.ShouldBe("joplin");
        loaded.ExternalId.ShouldBe("abc123");
        loaded.DeletedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task An_external_id_is_unique_within_a_workspace_and_source()
    {
        var (_, workspace) = await SeedWorkspaceAsync();
        await using (var db = CreateContext())
        {
            var first = NewPage(workspace.Id, "a0");
            first.ExternalSourceType = "joplin";
            first.ExternalId = "abc";
            var otherSource = NewPage(workspace.Id, "a1");
            otherSource.ExternalSourceType = "markdown";
            otherSource.ExternalId = "abc";
            db.Pages.AddRange(first, otherSource, NewPage(workspace.Id, "a2"), NewPage(workspace.Id, "a3"));
            await db.SaveChangesAsync();
        }

        await using var other = CreateContext();
        var duplicate = NewPage(workspace.Id, "a4");
        duplicate.ExternalSourceType = "joplin";
        duplicate.ExternalId = "abc";
        other.Pages.Add(duplicate);

        var exception = await Should.ThrowAsync<DbUpdateException>(() => other.SaveChangesAsync());
        ((PostgresException)exception.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task A_page_with_children_cannot_be_deleted_for_real()
    {
        var (_, workspace) = await SeedWorkspaceAsync();
        var parent = NewPage(workspace.Id, "a0");
        await using (var db = CreateContext())
        {
            db.Pages.AddRange(parent, NewPage(workspace.Id, "a0", "child", parent.Id));
            await db.SaveChangesAsync();
        }

        await using var other = CreateContext();
        other.Pages.Remove(await other.Pages.SingleAsync(p => p.Id == parent.Id));

        var exception = await Should.ThrowAsync<DbUpdateException>(() => other.SaveChangesAsync());
        ((PostgresException)exception.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
    }

    [Fact]
    public async Task A_page_needs_an_existing_workspace()
    {
        await using var db = CreateContext();
        db.Pages.Add(NewPage(Guid.NewGuid(), "a0"));

        var exception = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        ((PostgresException)exception.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task A_user_who_owns_a_workspace_cannot_be_deleted()
    {
        var (user, _) = await SeedWorkspaceAsync();
        await using var db = CreateContext();
        db.Users.Remove(await db.Users.SingleAsync(u => u.Id == user.Id));

        var exception = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        ((PostgresException)exception.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
    }

    [Fact]
    public async Task Ids_are_time_ordered()
    {
        var first = new User().Id;
        await Task.Delay(5);
        var second = new User().Id;

        string.CompareOrdinal(first.ToString(), second.ToString()).ShouldBeLessThan(0);
        first.Version.ShouldBe(7);
    }
}
