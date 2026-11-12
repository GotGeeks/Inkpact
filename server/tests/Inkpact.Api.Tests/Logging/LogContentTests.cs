using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Inkpact.Api.Auth;
using Inkpact.Api.Crdt;
using Inkpact.Api.History;
using Inkpact.Api.Tests.Import;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Inkpact.Api.Tests.Logging;

/// <summary>
/// The rule of run B.7: a log line holds ids, counts, codes and durations, never what a person wrote or typed. Each flow
/// below puts a marker where content goes, and no log entry of any category or level may hold one.
/// </summary>
public class LogContentTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Marker = "ZQXMARKER";

    [Fact]
    public async Task No_log_entry_holds_what_a_person_wrote_or_typed()
    {
        await using var factory = new InkpactFactory(
            await postgres.CreateDatabaseAsync(pooled: true),
            sidecar: true,
            // Everything the app can log, not only what the shipped levels let through.
            settings: new Dictionary<string, string> { ["Logging:LogLevel:Default"] = "Debug", ["Logging:LogLevel:Inkpact"] = "Trace" });
        using var owner = await factory.SignedInClientAsync();
        await factory.Services.GetRequiredService<YjsSidecar>().WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        Guid workspaceId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            workspaceId = await scope.ServiceProvider.GetRequiredService<InkpactDbContext>().Workspaces.Select(w => w.Id).SingleAsync();
        }

        // A failed login, with something typed where the address goes and a password.
        using (var stranger = factory.CreateClient())
        {
            (await stranger.LoginAsync(email: $"{Marker}-typed@example.com", password: $"{Marker}-password")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await stranger.LoginAsync(email: $"{Marker} not an address", password: $"{Marker}-password")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // A page: made, renamed, tagged, searched for.
        var created = await owner.PostJsonAsync($"/api/workspaces/{workspaceId}/pages", new { title = $"{Marker} title" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var pageId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("page").GetProperty("id").GetGuid();
        (await owner.PatchJsonAsync($"/api/pages/{pageId}", new { title = $"{Marker} renamed" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.PostJsonAsync($"/api/pages/{pageId}/tags", new { add = new[] { $"{Marker} tag" }, remove = Array.Empty<Guid>() })).IsSuccessStatusCode.ShouldBeTrue();
        (await owner.GetAsync($"/api/workspaces/{workspaceId}/search?q={Marker}search")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A file, by name and by content.
        var upload = new HttpRequestMessage(HttpMethod.Post, $"/api/pages/{pageId}/attachments?name={Marker}-file.txt")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes($"{Marker} in a file")),
        };
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        upload.Headers.Add(CsrfProtection.HeaderName, (await owner.GetStateAsync()).CsrfToken);
        (await owner.SendAsync(upload)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // An import: notebook, note, body, tag, a file, and an item that is refused.
        var jex = new JexBuilder()
            .Folder("f", title: $"{Marker} notebook")
            .Note("n", "f", body: $"# {Marker} heading\n\n{Marker} body ![](:/{JexBuilder.Id("r")})", title: $"{Marker} note")
            .Note("secret", "f", body: $"{Marker} encrypted", title: $"{Marker} secret", extra: new() { ["encryption_applied"] = "1" })
            .Tag("t", title: $"{Marker} joplin tag")
            .NoteTag("n", "t")
            .Resource("r", $"{Marker}-resource.txt", Encoding.UTF8.GetBytes($"{Marker} resource"))
            .ToBytes();
        var import = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/imports/joplin?name={Marker}-export.jex")
        {
            Content = new ByteArrayContent(jex),
        };
        import.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        import.Headers.Add(CsrfProtection.HeaderName, (await owner.GetStateAsync()).CsrfToken);
        var accepted = await owner.SendAsync(import);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var importId = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var state = (await owner.GetFromJsonAsync<JsonElement>($"/api/imports/{importId}")).GetProperty("state").GetString();
            if (state is "completed" or "failed")
            {
                state.ShouldBe("completed");
                break;
            }

            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(90), "the import did not end");
            await Task.Delay(100);
        }

        // Versions (run B.5b): every page written and imported above is captured, with the title that waited from the
        // rename, and one is read back. Then a page whose stored content is not a document, which no version can be made of.
        var capture = factory.Services.GetRequiredService<HistoryCapture>();
        Guid brokenId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
            foreach (var id in await db.Pages.Select(p => p.Id).ToListAsync())
            {
                (await capture.CaptureAsync(id, default)).ShouldBe(HistoryCaptureOutcome.Captured);
            }

            (await db.PageVersions.Select(v => v.Title).ToListAsync()).ShouldContain($"{Marker} renamed");
            var broken = await owner.PostJsonAsync($"/api/workspaces/{workspaceId}/pages", new { title = $"{Marker} broken" });
            brokenId = (await broken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("page").GetProperty("id").GetGuid();
            db.PageUpdates.Add(new PageUpdate { PageId = brokenId, UserId = (await owner.GetStateAsync()).User!.Id, Data = Encoding.UTF8.GetBytes($"{Marker} is not an update"), CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        (await capture.CaptureAsync(brokenId, default)).ShouldBe(HistoryCaptureOutcome.Unreadable);
        var versions = await owner.GetFromJsonAsync<VersionList>($"/api/pages/{pageId}/versions");
        (await owner.GetAsync($"/api/pages/{pageId}/versions/{versions!.Versions[0].Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetFromJsonAsync<VersionList>($"/api/pages/{brokenId}/versions"))!.State.ShouldBe(HistoryStates.Failed);

        factory.Logs.Entries.ShouldNotBeEmpty();
        factory.Logs.Entries
            .Where(e => e.Message.Contains(Marker, StringComparison.OrdinalIgnoreCase))
            .Select(e => $"{e.Level} {e.Category}: {e.Message}")
            .ShouldBeEmpty();

        NpgsqlConnection.ClearAllPools();
    }

    [Fact]
    public async Task The_log_is_one_line_per_entry_in_utc_without_colour_codes_by_default()
    {
        await using var factory = new InkpactFactory(await postgres.CreateDatabaseAsync());

        factory.Services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName.ShouldBe(ConsoleFormatterNames.Simple);
        var simple = factory.Services.GetRequiredService<IOptions<SimpleConsoleFormatterOptions>>().Value;
        simple.SingleLine.ShouldBeTrue();
        simple.UseUtcTimestamp.ShouldBeTrue();
        simple.TimestampFormat.ShouldNotBeNullOrEmpty();
        simple.ColorBehavior.ShouldBe(LoggerColorBehavior.Disabled);
    }

    [Fact]
    public async Task One_setting_makes_the_log_json_with_scopes_and_utc_times()
    {
        await using var factory = new InkpactFactory(
            await postgres.CreateDatabaseAsync(),
            settings: new Dictionary<string, string> { ["Logging:Console:FormatterName"] = "json" });

        factory.Services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName.ShouldBe(ConsoleFormatterNames.Json);
        var json = factory.Services.GetRequiredService<IOptions<JsonConsoleFormatterOptions>>().Value;
        json.IncludeScopes.ShouldBeTrue();
        json.UseUtcTimestamp.ShouldBeTrue();
        json.JsonWriterOptions.Indented.ShouldBeFalse();
    }
}
