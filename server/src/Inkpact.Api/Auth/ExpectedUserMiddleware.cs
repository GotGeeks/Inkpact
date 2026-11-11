using System.Security.Claims;

namespace Inkpact.Api.Auth;

/// <summary>Prevents an offline operation from being replayed under a cookie changed by another tab.</summary>
public sealed class ExpectedUserMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Inkpact-User";

    public async Task InvokeAsync(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated == true && http.Request.Headers.TryGetValue(HeaderName, out var expected))
        {
            var actual = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (expected.Count != 1 || !Guid.TryParse(expected[0], out var expectedId)
                || !Guid.TryParse(actual, out var actualId) || expectedId != actualId)
            {
                http.Response.StatusCode = StatusCodes.Status409Conflict;
                await http.Response.WriteAsJsonAsync(new ApiError("session_changed"), http.RequestAborted);
                return;
            }
        }

        await next(http);
    }
}
