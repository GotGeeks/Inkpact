using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Inkpact.Api.Auth;
using Inkpact.Api.Crdt;
using Inkpact.Api.Import;
using Inkpact.Api.Security;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Inkpact.Api.Tests.Import;

[Collection(PostgresCollection.Name)]
public sealed class ImportEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private InkpactFactory _factory = null!;
    private HttpClient _owner = null!;
    private Guid _workspaceId;
    private Guid _ownerId;

    public Task InitializeAsync() => StartAsync();

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();

        // The pool of this test's database would otherwise stay open on the shared server.
        NpgsqlConnection.ClearAllPools();
    }

    private async Task StartAsync(Dictionary<string, string>? settings = null)
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(pooled: true), sidecar: true, settings: settings);
        _owner = await _factory.SignedInClientAsync();
        await _factory.Services.GetRequiredService<YjsSidecar>().WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        (_workspaceId, _ownerId) = await UseDbAsync(async db => (await db.Workspaces.Select(w => w.Id).SingleAsync(), await db.Users.Select(u => u.Id).SingleAsync()));
    }

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    /// <summary>Someone signed in on their own client: a member with this role, or with no role at all a stranger.</summary>
    private async Task<(HttpClient Client, Guid UserId)> SignedInAsAsync(WorkspaceRole? role)
    {
        var email = $"{role?.ToString() ?? "stranger"}-{Guid.NewGuid():N}@example.com".ToLowerInvariant();
        var userId = await UseDbAsync(async db =>
        {
            var user = new User { Email = email, DisplayName = "Someone", PasswordHash = new Argon2PasswordHasher().Hash(AuthTestExtensions.Password), CreatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
            if (role is { } member)
            {
                db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = _workspaceId, UserId = user.Id, Role = member, CreatedAt = DateTimeOffset.UtcNow });
            }

            await db.SaveChangesAsync();
            return user.Id;
        });
        var client = _factory.CreateClient();
        (await client.LoginAsync(email)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return (client, userId);
    }

    private static ByteArrayContent Bytes(byte[] bytes, string contentType = AcceptsFileUpload.ContentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return content;
    }

    /// <summary>A body without a Content-Length, sent in chunks, the way a client streams a file of unknown size.</summary>
    private static StreamContent Streamed(byte[] bytes)
    {
        var content = new StreamContent(new UnseekableStream(bytes));
        content.Headers.ContentType = new MediaTypeHeaderValue(AcceptsFileUpload.ContentType);
        return content;
    }

    private Task<HttpResponseMessage> UploadAsync(HttpClient client, HttpContent content, string? name = "export.jex", bool withToken = true, Guid? workspace = null) =>
        SendAsync(client, $"/api/workspaces/{workspace ?? _workspaceId}/imports/joplin{(name is null ? "" : $"?name={Uri.EscapeDataString(name)}")}", content, withToken);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, HttpContent content, bool withToken = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (withToken)
        {
            request.Headers.Add(CsrfProtection.HeaderName, (await client.GetStateAsync()).CsrfToken);
        }

        return await client.SendAsync(request);
    }

    private static byte[] SmallExport() => new JexBuilder()
        .Folder("f")
        .Note("n", "f", body: "# Hello\n\nA note", todo: true, due: "1793358000000")
        .Note("secret", "f", body: "x", extra: new() { ["encryption_applied"] = "1" })
        .ToBytes();

    private async Task<JsonElement> GetImportAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/imports/{id}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Asks for the import until the job has ended it, as the page will.</summary>
    private async Task<JsonElement> WaitForEndAsync(HttpClient client, Guid id)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var import = await GetImportAsync(client, id);
            if (import.GetProperty("state").GetString() is "completed" or "failed")
            {
                return import;
            }

            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(90), "the import did not end");
            await Task.Delay(100);
        }
    }

    private string[] UploadedFiles() =>
        Directory.Exists(_factory.ImportsDirectory) ? Directory.EnumerateFiles(_factory.ImportsDirectory).ToArray() : [];

    // ---- the round trip ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_title_of_a_conflict_copy_comes_with_the_upload_in_the_uploaders_words()
    {
        var response = await SendAsync(
            _owner, $"/api/workspaces/{_workspaceId}/imports/joplin?name=a.jex&copyTitle={Uri.EscapeDataString("{title} (uit Joplin)")}", Bytes(SmallExport()));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var id = (await response.Content.ReadFromJsonAsync<ImportAccepted>())!.Id;
        await WaitForEndAsync(_owner, id);
        (await UseDbAsync(db => db.Imports.Where(i => i.Id == id).Select(i => i.CopyTitle).SingleAsync())).ShouldBe("{title} (uit Joplin)");
    }

    [Theory]
    [InlineData("(Joplin)")]
    [InlineData("{title} {title}")]
    [InlineData("{title}\n(Joplin)")]
    [InlineData("{title} (a copy with far too many words in it, more than any language needs to say that this is a copy of a note)")]
    public async Task A_conflict_copys_title_without_the_note_or_that_is_not_one_line_is_refused(string copyTitle)
    {
        var response = await SendAsync(
            _owner, $"/api/workspaces/{_workspaceId}/imports/joplin?name=a.jex&copyTitle={Uri.EscapeDataString(copyTitle)}", Bytes(SmallExport()));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadErrorAsync()).Code.ShouldBe("import_copy_title_invalid");
        (await UseDbAsync(db => db.Imports.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task An_uploaded_export_is_accepted_imported_in_the_background_and_reported()
    {
        var response = await UploadAsync(_owner, Bytes(SmallExport()), "My library.jex");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var id = (await response.Content.ReadFromJsonAsync<ImportAccepted>())!.Id;
        var import = await WaitForEndAsync(_owner, id);

        import.GetProperty("state").GetString().ShouldBe("completed");
        import.GetProperty("errorCode").ValueKind.ShouldBe(JsonValueKind.Null);
        import.GetProperty("fileName").GetString().ShouldBe("My library.jex");
        import.GetProperty("workspaceId").GetGuid().ShouldBe(_workspaceId);
        import.GetProperty("processedItems").GetInt32().ShouldBe(import.GetProperty("totalItems").GetInt32());
        var counts = import.GetProperty("counts");
        (counts.GetProperty("notebooks").GetInt32(), counts.GetProperty("notes").GetInt32(), counts.GetProperty("todos").GetInt32(), counts.GetProperty("skipped").GetInt32())
            .ShouldBe((1, 1, 1, 1));
        var report = import.GetProperty("report").EnumerateArray().Single();
        (report.GetProperty("status").GetString(), report.GetProperty("reasons")[0].GetProperty("code").GetString()).ShouldBe(("skipped", "encrypted"));
        UploadedFiles().ShouldBeEmpty();
        (await UseDbAsync(db => db.Pages.CountAsync(p => p.ExternalId != null))).ShouldBe(2);
        (await UseDbAsync(db => db.Reminders.CountAsync(r => r.UserId == _ownerId))).ShouldBe(1);
    }

    [Fact]
    public async Task While_it_runs_the_import_shows_progress_and_no_report_yet()
    {
        var id = await UseDbAsync(async db =>
        {
            var run = new ImportRun
            {
                WorkspaceId = _workspaceId,
                UserId = _ownerId,
                SourceType = "joplin",
                FileName = "x.jex",
                State = ImportStates.Running,
                TotalItems = 10,
                ProcessedItems = 4,
                Counts = """{"notes":3}""",
                Report = """[{"kind":"note","title":"half way"}]""",
                CreatedAt = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow,
            };
            db.Imports.Add(run);
            await db.SaveChangesAsync();
            return run.Id;
        });

        var import = await GetImportAsync(_owner, id);

        import.GetProperty("state").GetString().ShouldBe("running");
        (import.GetProperty("processedItems").GetInt32(), import.GetProperty("totalItems").GetInt32()).ShouldBe((4, 10));
        import.GetProperty("counts").GetProperty("notes").GetInt32().ShouldBe(3);
        import.GetProperty("report").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_limits_are_given_to_whoever_is_signed_in()
    {
        var limits = await _owner.GetFromJsonAsync<ImportLimits>("/api/imports/limits");

        limits!.MaxFileSize.ShouldBe(2L * 1024 * 1024 * 1024);
        (await _factory.CreateClient().GetAsync("/api/imports/limits")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---- who may ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_editor_may_upload_but_a_viewer_may_not_and_a_stranger_does_not_know_the_workspace()
    {
        var (editor, _) = await SignedInAsAsync(WorkspaceRole.Editor);
        var (viewer, _) = await SignedInAsAsync(WorkspaceRole.Viewer);
        var (stranger, _) = await SignedInAsAsync(null);

        (await UploadAsync(viewer, Bytes(SmallExport()))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await UploadAsync(stranger, Bytes(SmallExport()))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await UploadAsync(_factory.CreateClient(), Bytes(SmallExport()))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await UploadAsync(editor, Bytes(SmallExport()))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        UploadedFiles().Length.ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task Only_the_person_who_started_an_import_can_read_it()
    {
        var accepted = await (await UploadAsync(_owner, Bytes(SmallExport()))).Content.ReadFromJsonAsync<ImportAccepted>();
        var (editor, _) = await SignedInAsAsync(WorkspaceRole.Editor);
        var (stranger, _) = await SignedInAsAsync(null);

        (await editor.GetAsync($"/api/imports/{accepted!.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"/api/imports/{accepted.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _factory.CreateClient().GetAsync($"/api/imports/{accepted.Id}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _owner.GetAsync($"/api/imports/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await WaitForEndAsync(_owner, accepted.Id);
    }

    [Fact]
    public async Task An_upload_needs_the_csrf_token_and_the_file_content_type()
    {
        var noToken = await UploadAsync(_owner, Bytes(SmallExport()), withToken: false);
        var asJson = await UploadAsync(_owner, Bytes(SmallExport(), "application/json"));
        var asForm = await UploadAsync(_owner, Bytes(SmallExport(), "multipart/form-data; boundary=x"));

        noToken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await noToken.ReadErrorAsync()).Code.ShouldBe("csrf_invalid");
        asJson.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        asForm.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await UseDbAsync(db => db.Imports.CountAsync())).ShouldBe(0);
        UploadedFiles().ShouldBeEmpty();
    }

    // ---- what is refused ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_file_that_is_not_a_tar_is_refused_at_once_and_nothing_is_kept()
    {
        var garbage = await UploadAsync(_owner, Bytes(new byte[4000]));
        var text = await UploadAsync(_owner, Bytes("not a tar at all, but a file with some text in it"u8.ToArray()));

        garbage.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await garbage.ReadErrorAsync()).Code.ShouldBe("import_invalid");
        text.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await UseDbAsync(db => db.Imports.CountAsync())).ShouldBe(0);
        UploadedFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_upload_is_refused_with_and_without_a_length()
    {
        var withLength = await UploadAsync(_owner, Bytes([]));
        var streamed = await UploadAsync(_owner, Streamed([]));

        foreach (var response in new[] { withLength, streamed })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.ReadErrorAsync()).Code.ShouldBe("import_empty");
        }

        UploadedFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_over_the_limit_is_refused_with_and_without_a_length_and_nothing_is_kept()
    {
        await _factory.DisposeAsync();
        await StartAsync(new() { ["Import:MaxFileSize"] = "3000" });
        var big = new JexBuilder().Folder("f").Note("n", "f", body: new string('x', 5000)).ToBytes();

        var withLength = await UploadAsync(_owner, Bytes(big));
        var streamed = await UploadAsync(_owner, Streamed(big));

        foreach (var response in new[] { withLength, streamed })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
            (await response.ReadErrorAsync()).Code.ShouldBe("import_too_large");
        }

        (await UseDbAsync(db => db.Imports.CountAsync())).ShouldBe(0);
        UploadedFiles().ShouldBeEmpty();
    }

    private Task<Guid> AddImportRowAsync(string state) => UseDbAsync(async db =>
    {
        var run = new ImportRun { WorkspaceId = _workspaceId, UserId = _ownerId, SourceType = "joplin", FileName = "x.jex", State = state, CreatedAt = DateTimeOffset.UtcNow };
        db.Imports.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    });

    [Fact]
    public async Task A_second_export_waits_until_the_first_has_ended()
    {
        // An import this app is working on: its row, and its claim in the queue, as an upload makes them.
        var running = await AddImportRowAsync(ImportStates.Running);
        var queue = _factory.Services.GetRequiredService<ImportQueue>();
        queue.Claim(running);

        var refused = await UploadAsync(_owner, Bytes(SmallExport()));

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.ReadErrorAsync()).Code.ShouldBe("import_in_progress");
        UploadedFiles().ShouldBeEmpty();
        (await UseDbAsync(db => db.Imports.CountAsync())).ShouldBe(1);

        await UseDbAsync(db => db.Imports.Where(i => i.Id == running).ExecuteUpdateAsync(set => set.SetProperty(i => i.State, ImportStates.Completed)));
        queue.Release(running);
        (await UploadAsync(_owner, Bytes(SmallExport()))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    private async Task<OpenImport> GetOpenAsync(HttpClient client)
    {
        var response = await client.GetAsync($"/api/workspaces/{_workspaceId}/imports/open");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<OpenImport>())!;
    }

    [Fact]
    public async Task Whether_an_import_is_open_can_be_asked_before_an_upload_with_the_id_only_for_its_starter()
    {
        var (editor, _) = await SignedInAsAsync(WorkspaceRole.Editor);
        (await GetOpenAsync(_owner)).ShouldBe(new OpenImport(false, null));

        var running = await AddImportRowAsync(ImportStates.Running);
        var queue = _factory.Services.GetRequiredService<ImportQueue>();
        queue.Claim(running);

        (await GetOpenAsync(_owner)).ShouldBe(new OpenImport(true, running));
        (await GetOpenAsync(editor)).ShouldBe(new OpenImport(true, null));

        queue.Release(running);
        (await GetOpenAsync(_owner)).ShouldBe(new OpenImport(false, null), "a row no job works on does not block");
    }

    [Fact]
    public async Task Only_an_editor_may_ask_whether_an_import_is_open()
    {
        var (viewer, _) = await SignedInAsAsync(WorkspaceRole.Viewer);
        var (stranger, _) = await SignedInAsAsync(null);
        var path = $"/api/workspaces/{_workspaceId}/imports/open";

        (await viewer.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await stranger.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _factory.CreateClient().GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_import_left_behind_by_a_job_that_ended_without_a_word_does_not_block_the_workspace()
    {
        // A row that says it runs, but that no job of this app works on: the database went away as its job ended.
        var leftBehind = await AddImportRowAsync(ImportStates.Running);

        var accepted = await UploadAsync(_owner, Bytes(SmallExport()));

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var old = await UseDbAsync(db => db.Imports.AsNoTracking().SingleAsync(i => i.Id == leftBehind));
        (old.State, old.ErrorCode).ShouldBe((ImportStates.Failed, "interrupted"));
        await WaitForEndAsync(_owner, (await accepted.Content.ReadFromJsonAsync<ImportAccepted>())!.Id);
    }

    [Fact]
    public async Task The_database_takes_one_waiting_or_running_import_per_workspace()
    {
        await AddImportRowAsync(ImportStates.Queued);

        await Should.ThrowAsync<DbUpdateException>(() => AddImportRowAsync(ImportStates.Running));
        await AddImportRowAsync(ImportStates.Completed);
        await AddImportRowAsync(ImportStates.Failed);
    }

    [Fact]
    public async Task Someone_who_has_left_the_workspace_cannot_read_the_import_they_started()
    {
        var (editor, editorId) = await SignedInAsAsync(WorkspaceRole.Editor);
        var accepted = await (await UploadAsync(editor, Bytes(SmallExport()))).Content.ReadFromJsonAsync<ImportAccepted>();
        await WaitForEndAsync(editor, accepted!.Id);

        await UseDbAsync(db => db.WorkspaceMembers.Where(m => m.UserId == editorId).ExecuteDeleteAsync());

        (await editor.GetAsync($"/api/imports/{accepted.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_name_of_the_file_is_cleaned_and_a_missing_one_gets_a_default()
    {
        var one = await (await UploadAsync(_owner, Bytes(SmallExport()), "../../etc/passwd\r\n.jex")).Content.ReadFromJsonAsync<ImportAccepted>();
        await WaitForEndAsync(_owner, one!.Id);
        var two = await (await UploadAsync(_owner, Bytes(SmallExport()), name: null)).Content.ReadFromJsonAsync<ImportAccepted>();
        await WaitForEndAsync(_owner, two!.Id);

        var names = await UseDbAsync(db => db.Imports.AsNoTracking().OrderBy(i => i.CreatedAt).Select(i => i.FileName).ToListAsync());
        names[0].ShouldNotContain("/");
        names[0].ShouldNotContain("\n");
        names[1].ShouldNotBeNullOrWhiteSpace();
    }

    // ---- start-up ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task At_start_up_an_import_that_was_waiting_or_running_is_marked_and_old_files_are_removed()
    {
        var options = _factory.Services.GetRequiredService<ImportOptions>();
        Directory.CreateDirectory(options.Directory);
        var old = Path.Combine(options.Directory, "old.jex");
        var fresh = Path.Combine(options.Directory, "fresh.jex");
        await File.WriteAllBytesAsync(old, [1]);
        await File.WriteAllBytesAsync(fresh, [1]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));
        var ids = await UseDbAsync(async db =>
        {
            // A workspace has one waiting or running import at most, so the second open one is in another workspace.
            var other = new Workspace { Name = "Other", OwnerId = _ownerId, CreatedAt = DateTimeOffset.UtcNow };
            db.Workspaces.Add(other);
            ImportRun Make(string state, Guid? workspace = null) =>
                new() { WorkspaceId = workspace ?? _workspaceId, UserId = _ownerId, SourceType = "joplin", FileName = "x.jex", State = state, CreatedAt = DateTimeOffset.UtcNow };
            var runs = new[] { Make(ImportStates.Queued), Make(ImportStates.Running, other.Id), Make(ImportStates.Completed), Make(ImportStates.Failed) };
            db.Imports.AddRange(runs);
            await db.SaveChangesAsync();
            return runs.Select(r => r.Id).ToArray();
        });

        await _factory.Services.GetServices<IHostedService>().OfType<ImportWorker>().Single().RecoverAsync(default);

        var states = await UseDbAsync(db => db.Imports.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => (i.State, i.ErrorCode)));
        states[ids[0]].ShouldBe((ImportStates.Failed, "interrupted"));
        states[ids[1]].ShouldBe((ImportStates.Failed, "interrupted"));
        states[ids[2]].ShouldBe((ImportStates.Completed, null));
        states[ids[3]].ShouldBe((ImportStates.Failed, null));
        File.Exists(old).ShouldBeFalse();
        File.Exists(fresh).ShouldBeTrue();
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();
    }
}
