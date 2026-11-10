using System.Security.Claims;
using Inkpact.Api.Auth;
using Inkpact.Api.Permissions;
using Inkpact.Data.Entities;

namespace Inkpact.Api.History;

public static class HistoryPolicyEndpoints
{
    public static IEndpointRouteBuilder MapHistoryPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/workspaces/{workspaceId}/history-policy", Get).RequireWorkspaceRole(WorkspaceRole.Viewer);
        app.MapPut("/api/workspaces/{workspaceId}/history-policy", Update).RequireWorkspaceRole(WorkspaceRole.Owner);
        return app;
    }

    private static async Task<IResult> Get(Guid workspaceId, HistoryPolicyService service, CancellationToken cancellationToken) =>
        await service.ReadAsync(workspaceId, cancellationToken) is { } policy
            ? Results.Ok(policy)
            : Error(StatusCodes.Status404NotFound, "not_found");

    private static async Task<IResult> Update(
        Guid workspaceId, HistoryPolicyDto request, HttpContext http, HistoryPolicyService service, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await service.UpdateAsync(workspaceId, userId, request, cancellationToken);
        return result.Status switch
        {
            HistoryPolicyUpdateStatus.Saved => Results.Ok(result.Policy),
            HistoryPolicyUpdateStatus.Invalid => Error(StatusCodes.Status400BadRequest, "history_policy_invalid"),
            HistoryPolicyUpdateStatus.NotFound => Error(StatusCodes.Status404NotFound, "not_found"),
            HistoryPolicyUpdateStatus.Forbidden => Error(StatusCodes.Status403Forbidden, "forbidden"),
            HistoryPolicyUpdateStatus.ProtectedExceedsCap => Error(StatusCodes.Status400BadRequest, "history_cap_below_protected"),
            _ => throw new InvalidOperationException("Unknown history policy update status."),
        };
    }

    private static IResult Error(int status, string code) => Results.Json(new ApiError(code), statusCode: status);
}
