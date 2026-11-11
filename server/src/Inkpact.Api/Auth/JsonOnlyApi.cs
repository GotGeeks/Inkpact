using Inkpact.Api.Security;

namespace Inkpact.Api.Auth;

/// <summary>
/// API responses are never cached. Every state-changing API call must be JSON: a cross-site form or fetch cannot
/// send that without a CORS preflight, which the app never allows. The one exception is an attachment upload
/// (<see cref="AcceptsFileUpload"/>), which takes the raw file, and only as <c>application/octet-stream</c>.
/// </summary>
public static class JsonOnlyApi
{
    public static IApplicationBuilder UseJsonOnlyApi(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            var request = context.Request;
            if (request.Path.StartsWithSegments("/api"))
            {
                // Answers about who is signed in must never come from a cache. A file download sets its own
                // header (revalidated on every use); its error answers still get this one.
                context.Response.OnStarting(() =>
                {
                    var headers = context.Response.Headers;
                    if (!(context.ServesUserFile() && headers.CacheControl.Count > 0))
                    {
                        headers.CacheControl = "no-store";
                    }

                    return Task.CompletedTask;
                });
            }

            var expected = context.AcceptsFileUpload() ? AcceptsFileUpload.ContentType : "application/json";
            var changesState = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);
            if (changesState
                && request.Path.StartsWithSegments("/api")
                && !(request.ContentType?.StartsWith(expected, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return context.Response.WriteAsJsonAsync(new ApiError("unsupported_media_type"));
            }

            return next(context);
        });
}
