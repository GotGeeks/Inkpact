using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace Inkpact.Api.Auth;

public static class UserIdExtensions
{
    /// <summary>
    /// The key of the session this request came with, as the session store put it on the ticket it read
    /// (<see cref="PostgresTicketStore.SessionKeyItem"/>); null without one. The request is already authenticated, so this
    /// reads the cached result. The sync hub and the account settings (run B.9) both ask it here.
    /// </summary>
    public static async Task<string?> SessionKeyAsync(this HttpContext http)
    {
        var authentication = await http.AuthenticateAsync();
        return authentication.Properties?.Items.TryGetValue(PostgresTicketStore.SessionKeyItem, out var key) == true
            && !string.IsNullOrEmpty(key)
            ? key
            : null;
    }

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
