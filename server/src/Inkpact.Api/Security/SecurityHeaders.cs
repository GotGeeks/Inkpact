namespace Inkpact.Api.Security;

/// <summary>
/// The security headers from architecture "Security from day one". Set when the response starts,
/// so every response has them: the SPA, the API, static files, 404s and errors.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// No unsafe-inline, unsafe-eval, nonces or hashes: the built index.html only has external scripts and
    /// stylesheets. connect-src 'self' also covers the app's own WebSocket. upgrade-insecure-requests is left
    /// out on purpose, because it blanks every page of an install served over plain HTTP on a LAN.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; font-src 'self'; "
        + "connect-src 'self'; worker-src 'self'; manifest-src 'self'; object-src 'none'; base-uri 'none'; "
        + "form-action 'self'; frame-ancestors 'none'";

    /// <summary>
    /// For a file someone uploaded, opened on its own: a sandboxed document that may load nothing and run nothing,
    /// whatever the file turns out to be. The inline style is what the browser's own image viewer uses.
    /// </summary>
    public const string UserFileContentSecurityPolicy =
        "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; frame-ancestors 'none'; sandbox";

    public const string PermissionsPolicy =
        "camera=(), microphone=(), geolocation=(), payment=(), usb=(), serial=(), bluetooth=(), "
        + "magnetometer=(), gyroscope=(), accelerometer=(), browsing-topics=()";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["Content-Security-Policy"] = context.ServesUserFile() ? UserFileContentSecurityPolicy : ContentSecurityPolicy;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Permissions-Policy"] = PermissionsPolicy;
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers["Cross-Origin-Resource-Policy"] = "same-origin";
                headers["X-Frame-Options"] = "DENY";
                return Task.CompletedTask;
            });
            return next(context);
        });
}
