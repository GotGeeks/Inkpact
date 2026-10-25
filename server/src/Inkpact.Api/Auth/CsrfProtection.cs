using Microsoft.AspNetCore.Antiforgery;

namespace Inkpact.Api.Auth;

/// <summary>
/// Every state-changing API call must carry the token that GET /api/auth/state hands out, in a header. The token
/// is tied to the signed-in user, so one from before a login stops working after it. Together with SameSite=Lax
/// on the session cookie and the JSON-only rule, a page on another site has no way to act as the user.
/// </summary>
public static class CsrfProtection
{
    public const string HeaderName = "X-CSRF-Token";
    public const string CookieName = "inkpact-csrf";

    public static IServiceCollection AddCsrfProtection(this IServiceCollection services) =>
        services.AddAntiforgery(options =>
        {
            options.HeaderName = HeaderName;
            // Antiforgery has one fixed cookie name, so no "__Host-" variant per request as the session cookie
            // has. The request token is bound to the user, so a cookie planted by someone else gains little.
            options.Cookie.Name = CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.SuppressXFrameOptionsHeader = true;
        });

    /// <summary>Place after authentication, so the token is checked against the signed-in user.</summary>
    public static IApplicationBuilder UseCsrfProtection(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var changesState = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)
                && !HttpMethods.IsOptions(request.Method) && !HttpMethods.IsTrace(request.Method);
            if (changesState && request.Path.StartsWithSegments("/api"))
            {
                try
                {
                    await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
                }
                catch (AntiforgeryValidationException)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new ApiError("csrf_invalid"));
                    return;
                }
            }

            await next(context);
        });
}
