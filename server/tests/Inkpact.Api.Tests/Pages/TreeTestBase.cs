using Inkpact.Api.Pages;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inkpact.Api.Tests.Pages;

/// <summary>Helpers for tests of the page tree: a workspace to work in, and a tree service for every call, as a request gets one.</summary>
public abstract class TreeTestBase(PostgresFixture postgres) : DatabaseTestBase(postgres)
{
    protected static readonly DateTimeOffset Start = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    protected MutableTimeProvider Clock { get; } = new(Start);

    protected Guid WorkspaceId { get; private set; }

    protected Guid UserId { get; private set; }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        (UserId, WorkspaceId) = await AddWorkspaceAsync("owner@example.com");
    }

    protected async Task<(Guid UserId, Guid WorkspaceId)> AddWorkspaceAsync(string email)
    {
        await using var db = CreateContext();
        var user = new User { Email = email, DisplayName = email, CreatedAt = Start };
        var workspace = new Workspace { Name = "Workspace", OwnerId = user.Id, CreatedAt = Start };
        db.AddRange(user, workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = user.Id, Role = WorkspaceRole.Owner, CreatedAt = Start });
        await db.SaveChangesAsync();
        return (user.Id, workspace.Id);
    }

    /// <summary>The service of one request: its own database context and nothing remembered from the last call.</summary>
    protected async Task<T> WithTree<T>(Func<TreeService, Task<T>> action, ILogger<TreeService>? logger = null)
    {
        await using var db = CreateContext();
        return await action(new TreeService(db, Clock, logger ?? NullLogger<TreeService>.Instance));
    }

    /// <summary>Creates a page and returns its id. Fails the test if the page could not be created.</summary>
    protected async Task<Guid> CreateAsync(string title, Guid? parentId = null, Guid? afterId = null, Guid? beforeId = null, Guid? workspaceId = null)
    {
        var result = await WithTree(tree => tree.CreateAsync(workspaceId ?? WorkspaceId, title, new Position(parentId, afterId, beforeId)));
        result.Error.ShouldBeNull($"creating {title}");
        return result.Value!.Page.Id;
    }

    protected static async Task<PageChange> Succeeded(Task<TreeResult<PageChange>> call)
    {
        var result = await call;
        result.Error.ShouldBeNull();
        return result.Value!;
    }

    protected static async Task<TreeError> Failed(Task<TreeResult<PageChange>> call)
    {
        var result = await call;
        result.Error.ShouldNotBeNull($"expected an error, got {result.Value}");
        return result.Error;
    }

    protected Task<TreeDto> TreeAsync(Guid? workspaceId = null) => WithTree(tree => tree.GetTreeAsync(workspaceId ?? WorkspaceId));

    /// <summary>"Title" of every live page in tree order, with two spaces of indent per level.</summary>
    protected async Task<List<string>> OutlineAsync()
    {
        var pages = (await TreeAsync()).Pages;
        var depth = new Dictionary<Guid, int>();
        var lines = new List<string>();
        foreach (var page in pages)
        {
            var level = page.ParentId is { } parent ? depth[parent] + 1 : 0;
            depth[page.Id] = level;
            lines.Add(new string(' ', level * 2) + page.Title);
        }

        return lines;
    }

    protected async Task<Page> RowAsync(Guid pageId)
    {
        await using var db = CreateContext();
        return await db.Pages.AsNoTracking().SingleAsync(p => p.Id == pageId);
    }

    protected async Task<List<Page>> AllRowsAsync()
    {
        await using var db = CreateContext();
        return await db.Pages.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
    }
}
