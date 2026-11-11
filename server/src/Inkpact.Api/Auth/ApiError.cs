using System.Text.Json.Serialization;

namespace Inkpact.Api.Auth;

/// <summary>An error the client translates: a stable code, and the field it belongs to when there is one.</summary>
public sealed record ApiError(string Code, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field = null);

/// <summary>The one way an endpoint answers with an error: the status and an <see cref="ApiError"/> as JSON.</summary>
public static class ApiResults
{
    public static IResult Error(int status, string code, string? field = null) =>
        Results.Json(new ApiError(code, field), statusCode: status);

    public static IResult NotFound() => Error(StatusCodes.Status404NotFound, "not_found");
}
