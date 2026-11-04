using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Inkpact.Api.Attachments;
using Inkpact.Api.Auth;
using Inkpact.Api.Crdt;
using Inkpact.Api.Export;
using Inkpact.Api.Pages;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Inkpact.Api.Tests.Export;

/// <summary>
/// The export over HTTP, as the app runs it: the zip's entries and what is in them, who may take it, and what happens when
/// something fails half way. A page's content in these tests is its document as JSON in the one stored update, which the stand-in
/// codec reads as the sidecar's <c>read</c> would give it; the real sidecar has a test of its own at the end.
/// </summary>
public sealed partial class ExportEndpointTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly byte[] Photo = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] Manual = Encoding.UTF8.GetBytes("%PDF-1.4 the manual");

    private InkpactFactory _factory = null!;
    private HttpClient _owner = null!;
    private Guid _workspaceId;

    public async Task InitializeAsync() => await StartAsync();

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
    }

    private async Task StartAsync(IYjsCodec? codec = null, bool sidecar = false)
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), codec: sidecar ? null : codec ?? new JsonCodec(), sidecar: sidecar);
        _owner = await _factory.SignedInClientAsync();
        if (sidecar)
        {
            await _factory.Services.GetRequiredService<YjsSidecar>().WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        }

        _workspaceId = await UseDbAsync(db => db.Workspaces.Select(w => w.Id).SingleAsync());
    }

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    /// <summary>A page made through the API, with its content (a document as JSON) stored as its one update.</summary>
    private async Task<Guid> PageAsync(string title, Guid? parent = null, JsonNode? document = null, byte[]? update = null)
    {
        var response = await _owner.PostJsonAsync($"/api/workspaces/{_workspaceId}/pages", new { title, parentId = parent });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await response.Content.ReadFromJsonAsync<PageChange>())!.Page.Id;
        if (document is not null || update is not null)
        {
            await UseDbAsync(async db =>
            {
                db.PageUpdates.Add(new PageUpdate { PageId = id, Data = update ?? Encoding.UTF8.GetBytes(document!.ToJsonString()), CreatedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
                return 0;
            });
        }

        return id;
    }

    /// <summary>A file on a page: its bytes in the store, its row in the table.</summary>
    private async Task<Guid> FileAsync(Guid pageId, string name, byte[] bytes, bool store = true)
    {
        string hash;
        if (store)
        {
            hash = (await _factory.Services.GetRequiredService<IAttachmentStore>().PutAsync(new MemoryStream(bytes), long.MaxValue)).Sha256;
        }
        else
        {
            hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        }

        return await UseDbAsync(async db =>
        {
            var attachment = new Attachment { PageId = pageId, FileName = name, ContentType = "application/octet-stream", Size = bytes.Length, Sha256 = hash, CreatedAt = DateTimeOffset.UtcNow };
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync();
            return attachment.Id;
        });
    }

    private async Task TrashAsync(Guid pageId) =>
        (await _owner.PostJsonAsync($"/api/pages/{pageId}/trash", new { })).StatusCode.ShouldBe(HttpStatusCode.OK);

    private static async Task<Dictionary<string, byte[]>> ZipOfAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var archive = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        });
    }

    private async Task<Dictionary<string, byte[]>> ExportAsync(HttpClient? client = null, string query = "") =>
        await ZipOfAsync(await (client ?? _owner).GetAsync($"/api/workspaces/{_workspaceId}/export{query}"));

    private static string Text(Dictionary<string, byte[]> zip, string path) => Encoding.UTF8.GetString(zip[path]);

    private async Task<HttpClient> SignedInAsAsync(WorkspaceRole? role)
    {
        var email = $"{role?.ToString() ?? "stranger"}-{Guid.NewGuid():N}@example.com".ToLowerInvariant();
        await UseDbAsync(async db =>
        {
            var user = new User { Email = email, DisplayName = "Someone", PasswordHash = new Argon2PasswordHasher().Hash(AuthTestExtensions.Password), CreatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
            if (role is { } member)
            {
                db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = _workspaceId, UserId = user.Id, Role = member, CreatedAt = DateTimeOffset.UtcNow });
            }

            await db.SaveChangesAsync();
            return 0;
        });
        var client = _factory.CreateClient();
        (await client.LoginAsync(email)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return client;
    }

    // ---- what is in the zip ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_library_is_exported_as_folders_of_Markdown_with_one_copy_of_each_file_and_links_that_resolve()
    {
        var notes = await PageAsync("Notes");
        var photo = await FileAsync(notes, "photo.png", Photo);
        var manual = await FileAsync(notes, "manual.pdf", Manual);
        var ideas = await PageAsync("Ideas", notes);
        // The same picture on a second page is a second row with the same content: one file in the zip.
        var photoAgain = await FileAsync(ideas, "photo.png", Photo);
        await UseDbAsync(async db =>
        {
            db.PageUpdates.AddRange(
                Update(notes, Pm.Doc(Pm.P(Pm.T("See "), Pm.Link("the ideas", $"/p/{ideas}")), Pm.Image(photo, "A photo"), Pm.P(Pm.File(manual, "manual.pdf")))),
                Update(ideas, Pm.Doc(Pm.H(2, Pm.T("Back")), Pm.P(Pm.Link("up", $"/p/{notes}")), Pm.Image(photoAgain, "Again"))));
            await db.SaveChangesAsync();
            return 0;
        });
        var emptyTitle = await PageAsync("");
        var sameTitle = await PageAsync("Notes");
        var gone = await PageAsync("Gone");
        var goneChild = await PageAsync("Gone child", gone);
        await TrashAsync(gone);

        var zip = await ExportAsync();

        zip.Keys.Order(StringComparer.Ordinal).ShouldBe(
            ["Notes (2).md", "Notes.md", "Notes/Ideas.md", "README.md", "Untitled.md", "_attachments/manual.pdf", "_attachments/photo.png"]);
        zip["_attachments/photo.png"].ShouldBe(Photo);
        zip["_attachments/manual.pdf"].ShouldBe(Manual);

        var notesFile = Text(zip, "Notes.md");
        notesFile.ShouldStartWith($"---\ntitle: \"Notes\"\nid: \"{notes:D}\"\ncreated: ");
        notesFile.ShouldContain("[the ideas](Notes/Ideas.md)");
        notesFile.ShouldContain("![A photo](_attachments/photo.png)");
        notesFile.ShouldContain("[manual.pdf](_attachments/manual.pdf)");
        var ideasFile = Text(zip, "Notes/Ideas.md");
        ideasFile.ShouldContain("[up](../Notes.md)");
        ideasFile.ShouldContain("![Again](../_attachments/photo.png)");
        Text(zip, "Untitled.md").ShouldContain("title: \"\"");
        Text(zip, "Notes (2).md").ShouldContain($"id: \"{sameTitle:D}\"");
        zip.Values.Select(Encoding.UTF8.GetString).ShouldNotContain(t => t.Contains(gone.ToString("D")) || t.Contains(goneChild.ToString("D")));

        EveryLinkResolves(zip);
        _ = emptyTitle;
    }

    private static PageUpdate Update(Guid pageId, JsonNode document) =>
        new() { PageId = pageId, Data = Encoding.UTF8.GetBytes(document.ToJsonString()), CreatedAt = DateTimeOffset.UtcNow };

    [GeneratedRegex(@"\]\((?<path>[^)\s]+)\)")]
    private static partial Regex MarkdownLink();

    /// <summary>Every relative link in every page points at an entry of the zip, the way a reader follows it.</summary>
    private static void EveryLinkResolves(Dictionary<string, byte[]> zip)
    {
        var checkedLinks = 0;
        foreach (var (path, bytes) in zip.Where(e => e.Key.EndsWith(".md", StringComparison.Ordinal) && e.Key != "README.md"))
        {
            var directory = path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : "";
            foreach (Match link in MarkdownLink().Matches(Encoding.UTF8.GetString(bytes)))
            {
                var target = Uri.UnescapeDataString(link.Groups["path"].Value);
                if (target.Contains("://", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = new List<string>(directory.Length == 0 ? [] : directory.Split('/'));
                foreach (var part in target.Split('/'))
                {
                    if (part == "..")
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }
                    else if (part != ".")
                    {
                        parts.Add(part);
                    }
                }

                zip.ContainsKey(string.Join('/', parts)).ShouldBeTrue($"{path} links to {target}, which is not in the zip");
                checkedLinks++;
            }
        }

        checkedLinks.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Titles_that_are_not_valid_file_names_still_make_names_that_are_valid_everywhere_and_links_that_still_resolve()
    {
        var odd = await PageAsync("Café: «日本語» / what?");
        var reserved = await PageAsync("CON");
        var spaced = await PageAsync("Plan (draft) [2026] #1", odd);
        await UseDbAsync(async db =>
        {
            db.PageUpdates.Add(Update(reserved, Pm.Doc(Pm.P(Pm.Link("odd", $"/p/{odd}"), Pm.T(" "), Pm.Link("spaced", $"/p/{spaced}")))));
            await db.SaveChangesAsync();
            return 0;
        });

        var zip = await ExportAsync();

        zip.Keys.Order(StringComparer.Ordinal).ShouldBe(
            ["Café- «日本語» - what-.md", "Café- «日本語» - what-/Plan (draft) [2026] #1.md", "README.md", "_CON.md"]);
        Text(zip, "_CON.md").ShouldContain("[odd](Café-%20%C2%AB日本語%C2%BB%20-%20what-.md)");
        EveryLinkResolves(zip);
    }

    [Fact]
    public async Task A_page_has_its_tags_and_to_do_state_in_its_front_matter()
    {
        var todo = await PageAsync("Pay the bill", document: Pm.Doc(Pm.P(Pm.T("Today"))));
        await UseDbAsync(async db =>
        {
            var page = await db.Pages.SingleAsync(p => p.Id == todo);
            page.IsTodo = true;
            page.DueAt = new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);
            var tag = new Tag { WorkspaceId = _workspaceId, Name = "Money", NormalizedName = "money", CreatedAt = DateTimeOffset.UtcNow };
            db.Tags.Add(tag);
            db.PageTags.Add(new PageTag { PageId = todo, TagId = tag.Id });
            await db.SaveChangesAsync();
            return 0;
        });

        var text = Text(await ExportAsync(), "Pay the bill.md");

        text.ShouldContain("tags: [\"Money\"]\n");
        text.ShouldContain("todo: true\n");
        text.ShouldContain("due: 2026-12-01T09:00:00Z\n");
        text.ShouldEndWith("---\n\nToday\n");
    }

    [Fact]
    public async Task A_page_without_content_is_only_its_front_matter_and_an_empty_workspace_is_only_the_README()
    {
        var empty = await ExportAsync();
        empty.Keys.ShouldBe(["README.md"]);
        Text(empty, "README.md").ShouldContain("Notes: 0. Files: 0.");

        await PageAsync("Blank");
        var zip = await ExportAsync();

        Text(zip, "Blank.md").ShouldEndWith("---\n");
    }

    [Fact]
    public async Task The_README_is_in_the_language_asked_for_and_English_otherwise()
    {
        await PageAsync("One");

        Text(await ExportAsync(query: "?lang=nl"), "README.md").ShouldStartWith("# Inkpact-export");
        Text(await ExportAsync(query: "?lang=en"), "README.md").ShouldStartWith("# Inkpact export");
        Text(await ExportAsync(query: "?lang=xx"), "README.md").ShouldStartWith("# Inkpact export");
        Text(await ExportAsync(), "README.md").ShouldStartWith("# Inkpact export");
    }

    [Fact]
    public async Task The_zip_has_a_name_with_the_workspace_and_the_day_and_is_never_cached()
    {
        var response = await _owner.GetAsync($"/api/workspaces/{_workspaceId}/export");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        response.Content.Headers.ContentDisposition.FileNameStar!.ShouldMatch(@"^inkpact-Private-\d{4}-\d{2}-\d{2}\.zip$");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.Contains("Content-Security-Policy").ShouldBeTrue();
    }

    [Fact]
    public async Task The_entries_are_dated_with_the_pages_so_the_same_library_gives_the_same_zip()
    {
        await PageAsync("Dated", document: Pm.Doc(Pm.P(Pm.T("x"))));
        var updated = await UseDbAsync(db => db.Pages.Select(p => p.UpdatedAt).SingleAsync());

        using var archive = new ZipArchive(new MemoryStream(await (await _owner.GetAsync($"/api/workspaces/{_workspaceId}/export")).Content.ReadAsByteArrayAsync()));

        // A zip keeps the time to two seconds.
        // A zip has no time zone: the clock of the page's UTC time is what is written, so the same library gives the same zip anywhere.
        Math.Abs((archive.GetEntry("Dated.md")!.LastWriteTime.DateTime - updated.UtcDateTime).TotalSeconds).ShouldBeLessThanOrEqualTo(2);
    }

    // ---- who may take it, and what it leaves behind ----------------------------------------------------------------

    [Fact]
    public async Task A_viewer_can_export_a_stranger_gets_404_and_someone_not_signed_in_gets_401()
    {
        await PageAsync("Readable");

        var viewer = await SignedInAsAsync(WorkspaceRole.Viewer);
        (await ExportAsync(viewer)).ShouldContainKey("Readable.md");

        var stranger = await SignedInAsAsync(null);
        var refused = await stranger.GetAsync($"/api/workspaces/{_workspaceId}/export");
        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        refused.Content.Headers.ContentType!.MediaType.ShouldNotBe("application/zip");

        var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync($"/api/workspaces/{_workspaceId}/export")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_workspace_that_does_not_exist_is_404()
    {
        (await _owner.GetAsync($"/api/workspaces/{Guid.NewGuid()}/export")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Taking_the_export_is_a_line_in_the_security_log_and_a_refused_one_is_not()
    {
        var stranger = await SignedInAsAsync(null);
        (await stranger.GetAsync($"/api/workspaces/{_workspaceId}/export")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await UseDbAsync(db => db.AuditEvents.CountAsync(e => e.Type == AuditEventTypes.WorkspaceExported))).ShouldBe(0);

        await ExportAsync();

        var events = await UseDbAsync(db => db.AuditEvents.Where(e => e.Type == AuditEventTypes.WorkspaceExported).ToListAsync());
        events.Count.ShouldBe(1);
        events[0].UserId.ShouldNotBeNull();
        events[0].WorkspaceId.ShouldBe(_workspaceId);
    }

    [Fact]
    public async Task An_export_that_sends_nothing_is_no_line_in_the_security_log()
    {
        await StartAsync(new JsonCodec { Down = true });
        await PageAsync("One", document: Pm.Doc(Pm.P(Pm.T("x"))));

        (await _owner.GetAsync($"/api/workspaces/{_workspaceId}/export")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        (await UseDbAsync(db => db.AuditEvents.CountAsync(e => e.Type == AuditEventTypes.WorkspaceExported))).ShouldBe(0);
    }

    // ---- the check the client makes before the download -----------------------------------------------------------

    [Fact]
    public async Task The_check_says_an_export_can_be_made_to_whoever_may_read_the_workspace()
    {
        (await _owner.GetAsync($"/api/workspaces/{_workspaceId}/export/check")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var viewer = await SignedInAsAsync(WorkspaceRole.Viewer);
        (await viewer.GetAsync($"/api/workspaces/{_workspaceId}/export/check")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var stranger = await SignedInAsAsync(null);
        (await stranger.GetAsync($"/api/workspaces/{_workspaceId}/export/check")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync($"/api/workspaces/{_workspaceId}/export/check")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_check_says_when_the_export_cannot_be_made_with_a_code_the_client_translates()
    {
        await StartAsync(new JsonCodec { Down = true });

        var response = await _owner.GetAsync($"/api/workspaces/{_workspaceId}/export/check");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.ReadErrorAsync()).Code.ShouldBe("export_unavailable");
        (await UseDbAsync(db => db.AuditEvents.CountAsync(e => e.Type == AuditEventTypes.WorkspaceExported))).ShouldBe(0);
    }

    [Fact]
    public async Task A_file_of_another_workspace_is_not_carried_into_the_zip_by_naming_its_id()
    {
        var secret = Encoding.UTF8.GetBytes("secret of another workspace");
        var foreignId = await UseDbAsync(async db =>
        {
            var owner = await db.Users.SingleAsync();
            var other = new Workspace { Name = "Other", OwnerId = owner.Id, CreatedAt = DateTimeOffset.UtcNow };
            var page = new Page { WorkspaceId = other.Id, Title = "Theirs", SortKey = "a0", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.Workspaces.Add(other);
            db.Pages.Add(page);
            await db.SaveChangesAsync();
            return page.Id;
        });
        var foreignFile = await FileAsync(foreignId, "secret.txt", secret);
        await PageAsync("Mine", document: Pm.Doc(Pm.P(Pm.File(foreignFile, "secret.txt")), Pm.Image(foreignFile, "also")));

        var zip = await ExportAsync();

        zip.Keys.ShouldNotContain(k => k.StartsWith("_attachments", StringComparison.Ordinal));
        zip.Values.Any(bytes => bytes.AsSpan().IndexOf(secret) >= 0).ShouldBeFalse();
        Text(zip, "Mine.md").ShouldContain($"attachment:{foreignFile}");
    }

    [Fact]
    public async Task A_file_on_a_page_in_the_trash_may_still_be_used_by_a_page_that_is_exported()
    {
        // Whoever reads the workspace may read all of its files; a page that points at one in the trash gets it.
        var gone = await PageAsync("Gone");
        var file = await FileAsync(gone, "kept.bin", Photo);
        await PageAsync("Uses it", document: Pm.Doc(Pm.P(Pm.File(file, "kept.bin"))));
        await TrashAsync(gone);

        var zip = await ExportAsync();

        zip["_attachments/kept.bin"].ShouldBe(Photo);
        zip.ShouldNotContainKey("Gone.md");
    }

    // ---- what goes wrong -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_file_the_store_does_not_have_is_left_out_and_listed_and_the_export_goes_on()
    {
        var page = await PageAsync("Notes");
        var lost = await FileAsync(page, "lost.pdf", Manual, store: false);
        var there = await FileAsync(page, "there.png", Photo);
        await UseDbAsync(async db =>
        {
            db.PageUpdates.Add(Update(page, Pm.Doc(Pm.P(Pm.File(lost, "lost.pdf")), Pm.Image(there, "there"))));
            await db.SaveChangesAsync();
            return 0;
        });

        var zip = await ExportAsync();

        zip.ShouldNotContainKey("_attachments/lost.pdf");
        zip["_attachments/there.png"].ShouldBe(Photo);
        Text(zip, "README.md").ShouldContain("`_attachments/lost.pdf`");
        Text(zip, "README.md").ShouldContain("Files: 1.");
        Text(zip, "Notes.md").ShouldContain("(_attachments/lost.pdf)");
    }

    [Fact]
    public async Task A_stored_file_that_cannot_be_read_as_stored_is_left_out_and_listed_and_does_not_stop_the_export()
    {
        var page = await PageAsync("Notes");
        var damaged = await FileAsync(page, "damaged.pdf", Manual);
        var there = await FileAsync(page, "there.png", Photo);
        // The bytes on disk are no longer the ones their hash names: a bad disk, or a file changed by hand.
        var hash = Convert.ToHexStringLower(SHA256.HashData(Manual));
        var onDisk = Path.Combine(_factory.AttachmentsDirectory, hash[..2], hash[2..4], hash);
        File.SetAttributes(onDisk, FileAttributes.Normal);
        await File.WriteAllBytesAsync(onDisk, Encoding.UTF8.GetBytes("%PDF-1.4 something else"));
        await UseDbAsync(async db =>
        {
            db.PageUpdates.Add(Update(page, Pm.Doc(Pm.P(Pm.File(damaged, "damaged.pdf")), Pm.Image(there, "there"))));
            await db.SaveChangesAsync();
            return 0;
        });

        var zip = await ExportAsync();

        zip.ShouldNotContainKey("_attachments/damaged.pdf");
        zip["_attachments/there.png"].ShouldBe(Photo);
        Text(zip, "README.md").ShouldContain("`_attachments/damaged.pdf`");
    }

    [Fact]
    public async Task A_page_whose_stored_content_is_not_a_page_is_there_with_its_title_and_listed_while_the_rest_goes_on()
    {
        await PageAsync("Broken", update: [0xFF, 0xFE, 0x00]);
        await PageAsync("Fine", document: Pm.Doc(Pm.P(Pm.T("Still here"))));

        var zip = await ExportAsync();

        Text(zip, "Broken.md").ShouldEndWith("---\n");
        Text(zip, "Broken.md").ShouldContain("title: \"Broken\"");
        Text(zip, "Fine.md").ShouldContain("Still here");
        Text(zip, "README.md").ShouldContain("`Broken.md`");
        Text(zip, "README.md").ShouldNotContain("`Fine.md`");
    }

    [Fact]
    public async Task When_the_sidecar_is_down_the_first_page_finds_out_and_the_answer_is_503_without_a_zip()
    {
        await StartAsync(new JsonCodec { Down = true });
        await PageAsync("One", document: Pm.Doc(Pm.P(Pm.T("x"))));

        var response = await _owner.GetAsync($"/api/workspaces/{_workspaceId}/export");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.ReadErrorAsync()).Code.ShouldBe("export_unavailable");
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
    }

    [Fact]
    public async Task When_something_fails_before_anything_was_sent_the_answer_is_still_503_and_no_line_in_the_log()
    {
        // The first pages are small: they are gathered and never leave the server before the second page fails.
        await StartAsync(new JsonCodec { DownAfter = 1 });
        await PageAsync("First", document: Pm.Doc(Pm.P(Pm.T("one"))));
        await PageAsync("Second", document: Pm.Doc(Pm.P(Pm.T("two"))));

        var response = await _owner.GetAsync($"/api/workspaces/{_workspaceId}/export");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.ReadErrorAsync()).Code.ShouldBe("export_unavailable");
        response.Content.Headers.ContentDisposition.ShouldBeNull();
        (await UseDbAsync(db => db.AuditEvents.CountAsync(e => e.Type == AuditEventTypes.WorkspaceExported))).ShouldBe(0);
    }

    [Fact]
    public async Task When_something_fails_after_bytes_were_sent_the_download_is_cut_and_what_arrived_is_not_a_zip()
    {
        var codec = new JsonCodec { DownAfter = 1 };
        await StartAsync(codec);
        // A file larger than what is gathered before sending, so the first page's entries have left when the second fails.
        var first = await PageAsync("First");
        var large = await FileAsync(first, "large.bin", RandomNumberGenerator.GetBytes(4 * ResponseZipStream.ChunkSize));
        await UseDbAsync(async db =>
        {
            db.PageUpdates.Add(Update(first, Pm.Doc(Pm.P(Pm.File(large, "large.bin")))));
            await db.SaveChangesAsync();
            return 0;
        });
        await PageAsync("Second", document: Pm.Doc(Pm.P(Pm.T("two"))));
        await PageAsync("Third", document: Pm.Doc(Pm.P(Pm.T("three"))));

        using var response = await _owner.GetAsync($"/api/workspaces/{_workspaceId}/export", HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var received = new MemoryStream();
        var failure = await Should.ThrowAsync<Exception>(async () =>
        {
            await using var body = await response.Content.ReadAsStreamAsync();
            await body.CopyToAsync(received);
        });

        failure.ShouldNotBeNull();
        received.Length.ShouldBeGreaterThan(0);
        // The first entry was written, but the index at the end of a zip never was: no tool takes this for a whole zip.
        received.Position = 0;
        Should.Throw<InvalidDataException>(() => new ZipArchive(received, ZipArchiveMode.Read));
        // Bytes left, so the export is in the log, with its workspace.
        (await UseDbAsync(db => db.AuditEvents.SingleAsync(e => e.Type == AuditEventTypes.WorkspaceExported))).WorkspaceId.ShouldBe(_workspaceId);
    }

    // ---- the real sidecar ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_the_editor_can_make_is_exported_through_the_real_sidecar()
    {
        await StartAsync(sidecar: true);
        var picture = await PageAsync("Pictures");
        var photo = await FileAsync(picture, "photo.png", Photo);
        var codec = _factory.Services.GetRequiredService<IYjsCodec>();
        var document = Pm.Doc(
            Pm.H(1, Pm.T("Heading")),
            Pm.P(Pm.T("Some "), Pm.T("bold", "bold"), Pm.T(" text with a "), Pm.Link("link", "https://example.com/")),
            Pm.Ul(Pm.Li(Pm.P(Pm.T("item")))),
            Pm.Image(photo, "alt"));
        var update = await codec.BuildAsync(Encoding.UTF8.GetBytes(document.ToJsonString()), CancellationToken.None);
        await UseDbAsync(async db =>
        {
            db.PageUpdates.Add(new PageUpdate { PageId = picture, Data = update, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });

        var zip = await ExportAsync();

        var text = Text(zip, "Pictures.md");
        text.ShouldContain("# Heading\n");
        text.ShouldContain("Some **bold** text with a [link](https://example.com/)");
        text.ShouldContain("- item\n");
        text.ShouldContain("![alt](_attachments/photo.png)");
        zip["_attachments/photo.png"].ShouldBe(Photo);
    }

    /// <summary>
    /// Reads a page's stored update as the document itself (UTF-8 JSON), as the sidecar's <c>read</c> would give it. Anything
    /// that is not JSON is refused the way the sidecar refuses bytes that are no update.
    /// </summary>
    private sealed class JsonCodec : IYjsCodec
    {
        private int _calls;

        /// <summary>Every read fails as it does when the sidecar is not running.</summary>
        public bool Down { get; init; }

        /// <summary>Reads after this many fail the same way.</summary>
        public int? DownAfter { get; init; }

        public Task<YjsRead> ReadAsync(IReadOnlyList<byte[]> inputs, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            if (Down || (DownAfter is { } limit && call > limit))
            {
                throw new YjsUnavailableException("The sidecar is down.");
            }

            if (inputs.Count == 0)
            {
                return Task.FromResult(new YjsRead("""{"type":"doc","content":[]}"""u8.ToArray(), 0));
            }

            if (inputs[0].Length == 0 || inputs[0][0] != (byte)'{')
            {
                throw new YjsRejectedException("Not an update.", invalidContent: true);
            }

            return Task.FromResult(new YjsRead(inputs[0], 0));
        }

        public Task<YjsDiff> DiffAsync(ReadOnlyMemory<byte> stateVector, IReadOnlyList<byte[]> updates, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<YjsCompaction> CompactAsync(IReadOnlyList<byte[]> inputs, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<byte[]> FingerprintAsync(IReadOnlyList<byte[]> updates, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<byte[]> BuildAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<YjsReplacement> ReplaceAsync(IReadOnlyList<byte[]> inputs, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
