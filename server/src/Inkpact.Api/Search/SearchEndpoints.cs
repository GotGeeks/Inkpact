using Inkpact.Api.Permissions;
using Inkpact.Data.Entities;

namespace Inkpact.Api.Search;

/// <summary>
/// Search over REST. Reading the workspace's tree needs the viewer role, and so does finding what is in it: a person who
/// may not read a workspace gets a 404 as for its tree, and never a title or a snippet.
/// </summary>
public static class SearchEndpoints
{
    public static IServiceCollection AddSearch(this IServiceCollection services) => services.AddScoped<SearchService>();

    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/workspaces/{workspaceId}/search", Search).RequireWorkspaceRole(WorkspaceRole.Viewer);
        return app;
    }

    private static async Task<IResult> Search(Guid workspaceId, string? q, SearchService search, CancellationToken cancellationToken) =>
        Results.Ok(await search.SearchAsync(workspaceId, q, cancellationToken));
}
