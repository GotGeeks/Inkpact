namespace Inkpact.Api.Security;

/// <summary>
/// Marks the one kind of endpoint that takes a raw file as its body instead of JSON. The JSON-only rule reads it and
/// accepts <c>application/octet-stream</c> there, and nothing else. A cross-site form still cannot reach it: it
/// cannot send that type without a preflight, nor the CSRF header every state-changing call needs.
/// </summary>
public sealed class AcceptsFileUpload
{
    public const string ContentType = "application/octet-stream";
}

/// <summary>
/// Marks an endpoint that answers with a file someone uploaded. It gets <see cref="SecurityHeaders.UserFileContentSecurityPolicy"/>
/// instead of the app's policy, and keeps the cache header it sets itself.
/// </summary>
public sealed class ServesUserFile;

public static class UserFileEndpointExtensions
{
    public static RouteHandlerBuilder AcceptsFileUpload(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new AcceptsFileUpload());

    public static RouteHandlerBuilder ServesUserFile(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new ServesUserFile());

    public static bool AcceptsFileUpload(this HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<AcceptsFileUpload>() is not null;

    public static bool ServesUserFile(this HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<ServesUserFile>() is not null;
}
