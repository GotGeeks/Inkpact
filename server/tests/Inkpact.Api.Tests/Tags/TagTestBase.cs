using Inkpact.Api.Pages;
using Inkpact.Api.Tags;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inkpact.Api.Tests.Tags;

/// <summary>Helpers for tests of tags: a workspace with a page to tag, and a service for every call, as a request gets one.</summary>
public abstract class TagTestBase(PostgresFixture postgres) : DatabaseTestBase(postgres)
{
    protected static readonly DateTimeOffset Start = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    protected MutableTimeProvider Clock { get; } = new(Start);

    protected Guid WorkspaceId { get; private set; }

    protected Guid UserId { get; private set; }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await using var db = CreateContext();
        var user = new User { Email = "owner@example.com", DisplayName = "Owner", CreatedAt = Start };
        var workspace = new Workspace { Name = "Workspace", OwnerId = user.Id, CreatedAt = Start };
        db.AddRange(user, workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = user.Id, Role = WorkspaceRole.Owner, CreatedAt = Start });
        await db.SaveChangesAsync();
        UserId = user.Id;
        WorkspaceId = workspace.Id;
    }

    /// <summary>The service of one request: its own database context and nothing remembered from the last call.</summary>
    protected async Task<T> WithTagsAsync<T>(Func<TagService, Task<T>> action)
    {
        await using var db = CreateContext();
        return await action(new TagService(db, Clock));
    }

    /// <summary>Creates a page and returns its id, through the tree service, as a client would.</summary>
    protected async Task<Guid> CreatePageAsync(string title = "Page", Guid? parentId = null)
    {
        await using var db = CreateContext();
        var tree = new TreeService(db, Clock, NullLogger<TreeService>.Instance);
        var result = await tree.CreateAsync(WorkspaceId, title, new Position(parentId, null, null));
        result.Error.ShouldBeNull();
        return result.Value!.Page.Id;
    }

    protected async Task TrashPageAsync(Guid pageId)
    {
        await using var db = CreateContext();
        var tree = new TreeService(db, Clock, NullLogger<TreeService>.Instance);
        (await tree.TrashAsync(pageId, null)).Succeeded.ShouldBeTrue();
    }

    protected static async Task<IReadOnlyList<TagDto>> SucceededAsync(Task<TagResult<IReadOnlyList<TagDto>>> call)
    {
        var result = await call;
        result.Error.ShouldBeNull($"expected success, got {result.Error?.Code}");
        return result.Value!;
    }

    protected static async Task<TagError> FailedAsync<T>(Task<TagResult<T>> call)
    {
        var result = await call;
        result.Error.ShouldNotBeNull($"expected an error, got {result.Value}");
        return result.Error;
    }

    /// <summary>A tag no page has, as the import leaves one, or one that was taken off its last page.</summary>
    protected async Task<Guid> AddTagAsync(string name)
    {
        await using var db = CreateContext();
        var tag = new Tag { WorkspaceId = WorkspaceId, Name = name, NormalizedName = name.ToLowerInvariant(), CreatedAt = Start };
        db.Tags.Add(tag);
        await db.SaveChangesAsync();
        return tag.Id;
    }

    protected async Task<List<Tag>> AllTagsAsync()
    {
        await using var db = CreateContext();
        return await db.Tags.AsNoTracking().Where(t => t.WorkspaceId == WorkspaceId).OrderBy(t => t.NormalizedName).ToListAsync();
    }

    protected async Task<List<Guid>> PageTagIdsAsync(Guid pageId)
    {
        await using var db = CreateContext();
        return await db.PageTags.AsNoTracking().Where(pt => pt.PageId == pageId).Select(pt => pt.TagId).OrderBy(id => id).ToListAsync();
    }
}
