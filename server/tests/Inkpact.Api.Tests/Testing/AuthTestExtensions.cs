using System.Net;
using System.Net.Http.Json;
using Inkpact.Api.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Inkpact.Api.Tests.Testing;

public static class AuthTestExtensions
{
    public const string Email = "jos@example.com";
    public const string Password = "correct horse battery";

    public static HttpClient CreateHttpsClient(this InkpactFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string path, object? body = null) =>
        client.PostAsJsonAsync(path, body ?? new { });

    public static Task<HttpResponseMessage> SetupAsync(
        this HttpClient client,
        string email = Email,
        string password = Password,
        string displayName = "Jos",
        string workspaceName = "Private",
        string setupCode = InkpactFactory.SetupCode) =>
        client.PostJsonAsync("/api/auth/setup", new { setupCode, email, displayName, password, workspaceName });

    public static Task<HttpResponseMessage> LoginAsync(this HttpClient client, string email = Email, string password = Password) =>
        client.PostJsonAsync("/api/auth/login", new { email, password });

    public static async Task<AuthState> GetStateAsync(this HttpClient client) =>
        (await client.GetFromJsonAsync<AuthState>("/api/auth/state"))!;

    public static async Task<ApiError> ReadErrorAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiError>())!;

    /// <summary>Creates the administrator through the real endpoint and returns the client, signed in.</summary>
    public static async Task<HttpClient> SignedInClientAsync(this InkpactFactory factory)
    {
        var client = factory.CreateClient();
        (await client.SetupAsync()).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return client;
    }

    public static IEnumerable<string> SetCookies(this HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
}
