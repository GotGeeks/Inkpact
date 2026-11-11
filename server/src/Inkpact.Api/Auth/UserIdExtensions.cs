using System.Security.Claims;

namespace Inkpact.Api.Auth;

public static class UserIdExtensions
{
    /// <summary>
    /// The signed-in user's id. Throws when there is none, so "nobody" can never end up in a row: call it only
    /// behind an authorisation policy, which every endpoint has.
    /// </summary>
    public static Guid UserId(this HttpContext http) => http.User.UserId();

    /// <inheritdoc cref="UserId(HttpContext)"/>
    public static Guid UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new InvalidOperationException("The request has no signed-in user.");
}
