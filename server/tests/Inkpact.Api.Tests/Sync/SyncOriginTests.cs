using System.Net.WebSockets;
using Inkpact.Api.Sync;
using Inkpact.Api.Tests.Testing;
using Microsoft.AspNetCore.Http;

namespace Inkpact.Api.Tests.Sync;

public class SyncOriginTests
{
    private static HttpRequest Request(string scheme, string host, string? origin)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        return context.Request;
    }

    [Theory]
    [InlineData("https", "notes.example.com", "https://notes.example.com")]
    [InlineData("https", "notes.example.com", "https://NOTES.example.com")]
    [InlineData("http", "192.168.1.10:8080", "http://192.168.1.10:8080")]
    public void The_apps_own_origin_is_accepted(string scheme, string host, string origin)
    {
        SyncEndpoints.IsSameOrigin(Request(scheme, host, origin)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("https", "notes.example.com", "https://evil.example.com")]
    [InlineData("https", "notes.example.com", "http://notes.example.com")] // the scheme counts
    [InlineData("https", "notes.example.com", "https://notes.example.com:8443")] // and the port
    [InlineData("https", "notes.example.com", "https://notes.example.com.evil.example")]
    [InlineData("https", "notes.example.com", "null")] // sandboxed frames and file pages
    [InlineData("https", "notes.example.com", "")]
    [InlineData("https", "notes.example.com", null)]
    public void Any_other_origin_is_refused(string scheme, string host, string? origin)
    {
        SyncEndpoints.IsSameOrigin(Request(scheme, host, origin)).ShouldBeFalse();
    }
}

/// <summary>Behind a TLS proxy the app sees plain HTTP, but the browser's origin is https: the forwarded scheme decides.</summary>
public class SyncBehindProxyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Behind_a_trusted_proxy_the_https_origin_is_the_apps_own()
    {
        await using var factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), trustedProxies: ["10.0.0.0/8"], remoteIp: "10.0.0.2");
        var jos = await SyncUser.SetUpAdministratorAsync(factory, "https://localhost");

        var socketClient = factory.Server.CreateWebSocketClient();
        socketClient.ConfigureRequest = request =>
        {
            request.Headers.Cookie = jos.CookieHeader;
            request.Headers.Origin = "https://localhost";
            request.Headers["X-Forwarded-Proto"] = "https";
        };

        using var socket = await socketClient.ConnectAsync(new Uri("ws://localhost" + SyncEndpoints.Path), CancellationToken.None);
        socket.State.ShouldBe(WebSocketState.Open);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }
}

/// <summary>
/// Traefik forwards a WebSocket upgrade with <c>X-Forwarded-Proto: wss</c> (or <c>ws</c>), not <c>https</c>: the same
/// browser on the same page, so the same session cookie and the same origin must count.
/// </summary>
public class SyncBehindTraefikTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("wss", "https://localhost")]
    [InlineData("WSS", "https://localhost")]
    [InlineData("ws", "http://localhost")]
    public async Task A_websocket_forwarded_as_ws_or_wss_is_the_http_or_https_page_it_came_from(string forwardedProto, string pageOrigin)
    {
        await using var factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), trustedProxies: ["10.0.0.0/8"], remoteIp: "10.0.0.2");
        // Signed in through the proxy, as https or http: the cookie the browser holds is the one for that scheme.
        var jos = await SyncUser.SetUpAdministratorAsync(factory, pageOrigin);

        var socketClient = factory.Server.CreateWebSocketClient();
        socketClient.ConfigureRequest = request =>
        {
            request.Headers.Cookie = jos.CookieHeader;
            request.Headers.Origin = pageOrigin;
            request.Headers["X-Forwarded-Proto"] = forwardedProto;
        };

        using var socket = await socketClient.ConnectAsync(new Uri("ws://localhost" + SyncEndpoints.Path), CancellationToken.None);
        socket.State.ShouldBe(WebSocketState.Open);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }
}
