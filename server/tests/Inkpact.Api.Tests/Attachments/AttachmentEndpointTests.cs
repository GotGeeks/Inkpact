using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Inkpact.Api.Attachments;
using Inkpact.Api.Auth;
using Inkpact.Api.Pages;
using Inkpact.Api.Security;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.Attachments;

[Collection(PostgresCollection.Name)]
public class AttachmentEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private InkpactFactory _factory = null!;
    private HttpClient _owner = null!;
    private Guid _workspaceId;
    private Guid _pageId;

    public async Task InitializeAsync() => await StartAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task StartAsync(IReadOnlyDictionary<string, string>? settings = null)
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), settings: settings);
        _owner = await _factory.SignedInClientAsync();
        _workspaceId = await UseDbAsync(db => db.Workspaces.Select(w => w.Id).SingleAsync());
        _pageId = await CreatePageAsync("Page");
    }

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    private async Task<Guid> CreatePageAsync(string title)
    {
        var response = await _owner.PostJsonAsync($"/api/workspaces/{_workspaceId}/pages", new { title });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<PageChange>())!.Page.Id;
    }

    /// <summary>Someone signed in on their own client: a member with this role, or with no role at all a stranger.</summary>
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

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        Guid pageId,
        HttpContent content,
        string? name = "file.bin",
        bool withToken = true)
    {
        var query = name is null ? "" : $"?name={Uri.EscapeDataString(name)}";
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/pages/{pageId}/attachments{query}") { Content = content };
        if (withToken)
        {
            request.Headers.Add(CsrfProtection.HeaderName, (await client.GetStateAsync()).CsrfToken);
        }

        return await client.SendAsync(request);
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

    private async Task<AttachmentDto> UploadOkAsync(byte[] bytes, string name, HttpClient? client = null)
    {
        var response = await UploadAsync(client ?? _owner, _pageId, Bytes(bytes), name);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<AttachmentDto>())!;
    }

    private string[] StoredFiles() =>
        Directory.Exists(_factory.AttachmentsDirectory)
            ? Directory.EnumerateFiles(_factory.AttachmentsDirectory, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(Path.GetDirectoryName(f)) != "tmp").ToArray()
            : [];

    private string[] TemporaryFiles() =>
        Directory.EnumerateFiles(Path.Combine(_factory.AttachmentsDirectory, "tmp")).ToArray();

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : "";

    // ---- the round trip ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_uploaded_image_comes_back_as_it_was_and_is_shown_inline()
    {
        var added = await UploadOkAsync(Png, "Screenshot 2026.png");

        added.FileName.ShouldBe("Screenshot 2026.png");
        added.ContentType.ShouldBe("image/png");
        added.Size.ShouldBe(Png.Length);
        added.Inline.ShouldBeTrue();

        var response = await _owner.GetAsync($"/api/attachments/{added.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(Png);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        response.Content.Headers.ContentDisposition.FileNameStar.ShouldBe("Screenshot 2026.png");
    }

    [Fact]
    public async Task A_download_has_its_own_sandboxing_policy_and_the_other_security_headers()
    {
        var added = await UploadOkAsync(Png, "a.png");

        var response = await _owner.GetAsync($"/api/attachments/{added.Id}");

        Header(response, "Content-Security-Policy").ShouldBe(SecurityHeaders.UserFileContentSecurityPolicy);
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(response, "Cross-Origin-Resource-Policy").ShouldBe("same-origin");
        Header(response, "X-Frame-Options").ShouldBe("DENY");
        response.Headers.CacheControl!.Private.ShouldBeTrue();
        response.Headers.CacheControl.NoCache.ShouldBeTrue();
        response.Headers.CacheControl.NoStore.ShouldBeFalse();
        response.Headers.ETag!.Tag.ShouldBe($"\"{Convert.ToHexStringLower(SHA256.HashData(Png))}\"");
    }

    [Fact]
    public async Task The_app_keeps_its_own_policy_and_no_store_on_its_other_answers()
    {
        var response = await _owner.GetAsync($"/api/workspaces/{_workspaceId}/pages");

        Header(response, "Content-Security-Policy").ShouldBe(SecurityHeaders.ContentSecurityPolicy);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task A_download_that_fails_is_not_cached()
    {
        var response = await _owner.GetAsync($"/api/attachments/{Guid.NewGuid()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    public static TheoryData<byte[], string, string> Downloads => new()
    {
        { Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"), "logo.svg", "image/svg+xml" },
        { Encoding.UTF8.GetBytes("<!doctype html><script>alert(1)</script>"), "page.html", "text/html" },
        { Encoding.UTF8.GetBytes("<!doctype html><script>alert(1)</script>"), "cat.png", "application/octet-stream" },
        { Encoding.UTF8.GetBytes("%PDF-1.7 ..."), "manual.pdf", "application/pdf" },
    };

    [Theory]
    [MemberData(nameof(Downloads))]
    public async Task Anything_that_is_not_a_recognised_image_is_only_ever_a_download(byte[] bytes, string name, string contentType)
    {
        var added = await UploadOkAsync(bytes, name);
        added.ContentType.ShouldBe(contentType);
        added.Inline.ShouldBeFalse();

        var response = await _owner.GetAsync($"/api/attachments/{added.Id}");

        response.Content.Headers.ContentType!.MediaType.ShouldBe(contentType);
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        Header(response, "Content-Security-Policy").ShouldBe(SecurityHeaders.UserFileContentSecurityPolicy);
    }

    [Fact]
    public async Task The_type_the_client_claims_for_the_file_is_ignored()
    {
        // The request must say octet-stream; a name that says "png" does not make HTML an image either.
        var added = await UploadOkAsync(Encoding.UTF8.GetBytes("<html>"), "photo.png");

        added.ContentType.ShouldBe("application/octet-stream");
        added.Inline.ShouldBeFalse();
    }

    [Fact]
    public async Task The_name_is_cleaned_and_a_missing_name_becomes_file()
    {
        (await UploadOkAsync(Png, "../../etc/x.png")).FileName.ShouldBe("x.png");

        var response = await UploadAsync(_owner, _pageId, Bytes(Png), name: null);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await response.Content.ReadFromJsonAsync<AttachmentDto>())!.FileName.ShouldBe("file");
    }

    [Fact]
    public async Task A_name_in_any_script_survives_the_download_header()
    {
        var added = await UploadOkAsync(Encoding.UTF8.GetBytes("notes"), "Notities – crème 日本 🎉.txt");

        var response = await _owner.GetAsync($"/api/attachments/{added.Id}");

        response.Content.Headers.ContentDisposition!.FileNameStar.ShouldBe("Notities – crème 日本 🎉.txt");
    }

    // ---- stored once --------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_file_added_twice_is_two_attachments_and_one_stored_file()
    {
        var other = await CreatePageAsync("Other");
        var first = await UploadOkAsync(Png, "first.png");
        var second = await UploadAsync(_owner, other, Bytes(Png), "second.png");
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        var secondId = (await second.Content.ReadFromJsonAsync<AttachmentDto>())!.Id;

        secondId.ShouldNotBe(first.Id);
        StoredFiles().ShouldHaveSingleItem();
        var rows = await UseDbAsync(db => db.Attachments.OrderBy(a => a.FileName).ToListAsync());
        rows.Select(r => r.FileName).ShouldBe(["first.png", "second.png"]);
        rows.Select(r => r.Sha256).Distinct().ShouldHaveSingleItem().ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Png)));
        rows.Select(r => r.PageId).ShouldBe([_pageId, other]);
        (await _owner.GetByteArrayAsync($"/api/attachments/{secondId}")).ShouldBe(Png);
    }

    [Fact]
    public async Task The_row_records_who_added_it_and_when()
    {
        var added = await UploadOkAsync(Png, "a.png");

        var row = await UseDbAsync(db => db.Attachments.SingleAsync(a => a.Id == added.Id));
        var owner = await UseDbAsync(db => db.Users.Select(u => u.Id).SingleAsync());
        row.CreatedBy.ShouldBe(owner);
        row.CreatedAt.ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    // ---- who may do what ----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_editor_may_add_a_file_and_a_viewer_may_not()
    {
        var editor = await SignedInAsAsync(WorkspaceRole.Editor);
        var viewer = await SignedInAsAsync(WorkspaceRole.Viewer);

        (await UploadAsync(editor, _pageId, Bytes(Png))).StatusCode.ShouldBe(HttpStatusCode.Created);

        var refused = await UploadAsync(viewer, _pageId, Bytes(Png));
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.ReadErrorAsync()).ShouldBe(new ApiError("forbidden"));
        (await UseDbAsync(db => db.Attachments.CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task A_viewer_may_download()
    {
        var added = await UploadOkAsync(Png, "a.png");
        var viewer = await SignedInAsAsync(WorkspaceRole.Viewer);

        (await viewer.GetByteArrayAsync($"/api/attachments/{added.Id}")).ShouldBe(Png);
    }

    [Fact]
    public async Task A_stranger_gets_not_found_for_the_page_and_its_files_and_stores_nothing()
    {
        var added = await UploadOkAsync(Png, "a.png");
        var stranger = await SignedInAsAsync(role: null);

        var download = await stranger.GetAsync($"/api/attachments/{added.Id}");
        download.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await download.ReadErrorAsync()).ShouldBe(new ApiError("not_found"));

        var upload = await UploadAsync(stranger, _pageId, Bytes([1, 2, 3]));
        upload.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await upload.ReadErrorAsync()).ShouldBe(new ApiError("not_found"));
        StoredFiles().ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-guid")]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    public async Task An_attachment_that_does_not_exist_is_not_found(string id)
    {
        var response = await _owner.GetAsync($"/api/attachments/{id}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Nobody_who_is_not_signed_in_gets_anything()
    {
        var added = await UploadOkAsync(Png, "a.png");
        var anonymous = _factory.CreateClient();

        (await anonymous.GetAsync($"/api/attachments/{added.Id}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await UploadAsync(anonymous, _pageId, Bytes(Png))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_page_in_the_trash_keeps_its_files_and_takes_new_ones()
    {
        // Content edits to a page in the trash are kept (the conflict table); a file is content.
        var added = await UploadOkAsync(Png, "a.png");
        (await _owner.PostJsonAsync($"/api/pages/{_pageId}/trash")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await _owner.GetAsync($"/api/attachments/{added.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await UploadAsync(_owner, _pageId, Bytes([1, 2, 3]))).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    // ---- the rules every state-changing call follows ------------------------------------------------------------

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("image/png")]
    public async Task An_upload_is_raw_bytes_and_nothing_else(string contentType)
    {
        var response = await UploadAsync(_owner, _pageId, Bytes(Png, contentType));

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await response.ReadErrorAsync()).ShouldBe(new ApiError("unsupported_media_type"));
        StoredFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task Raw_bytes_are_refused_everywhere_else()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{_workspaceId}/pages") { Content = Bytes("{}"u8.ToArray()) };
        request.Headers.Add(CsrfProtection.HeaderName, (await _owner.GetStateAsync()).CsrfToken);

        (await _owner.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task An_upload_without_the_csrf_token_is_refused()
    {
        var response = await UploadAsync(_owner, _pageId, Bytes(Png), withToken: false);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadErrorAsync()).ShouldBe(new ApiError("csrf_invalid"));
        StoredFiles().ShouldBeEmpty();
    }

    // ---- limits -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_file_over_the_limit_is_refused_with_or_without_a_length_and_leaves_nothing()
    {
        await _factory.DisposeAsync();
        await StartAsync(new Dictionary<string, string> { ["Attachments:MaxFileSize"] = "1000" });

        var atLimit = await UploadAsync(_owner, _pageId, Bytes(new byte[1000]));
        var declared = await UploadAsync(_owner, _pageId, Bytes(new byte[1001]));
        var streamed = await UploadAsync(_owner, _pageId, Streamed(new byte[5000]));

        atLimit.StatusCode.ShouldBe(HttpStatusCode.Created);
        foreach (var refused in new[] { declared, streamed })
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
            (await refused.ReadErrorAsync()).ShouldBe(new ApiError("attachment_too_large"));
        }

        StoredFiles().ShouldHaveSingleItem();
        TemporaryFiles().ShouldBeEmpty();
        (await UseDbAsync(db => db.Attachments.CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task The_default_limit_is_50_mb_and_the_client_can_ask_for_it()
    {
        new AttachmentOptions().MaxFileSize.ShouldBe(50L * 1024 * 1024);

        var limits = await _owner.GetFromJsonAsync<AttachmentLimits>("/api/attachments/limits");

        limits.ShouldBe(new AttachmentLimits(50L * 1024 * 1024));
        (await _factory.CreateClient().GetAsync("/api/attachments/limits")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_limit_the_client_is_told_is_the_one_that_is_set()
    {
        await _factory.DisposeAsync();
        await StartAsync(new Dictionary<string, string> { ["Attachments:MaxFileSize"] = "1000" });

        (await _owner.GetFromJsonAsync<AttachmentLimits>("/api/attachments/limits")).ShouldBe(new AttachmentLimits(1000));
    }

    [Fact]
    public async Task An_empty_file_is_refused_with_or_without_a_length()
    {
        var declared = await UploadAsync(_owner, _pageId, Bytes([]));
        var streamed = await UploadAsync(_owner, _pageId, Streamed([]));

        foreach (var refused in new[] { declared, streamed })
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await refused.ReadErrorAsync()).ShouldBe(new ApiError("attachment_empty"));
        }

        (await UseDbAsync(db => db.Attachments.CountAsync())).ShouldBe(0);
        StoredFiles().ShouldBeEmpty();
    }

    // ---- caching and ranges -------------------------------------------------------------------------------------

    [Fact]
    public async Task Asking_again_with_the_tag_gets_not_modified()
    {
        var added = await UploadOkAsync(Png, "a.png");
        var first = await _owner.GetAsync($"/api/attachments/{added.Id}");

        var again = new HttpRequestMessage(HttpMethod.Get, $"/api/attachments/{added.Id}");
        again.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        var response = await _owner.SendAsync(again);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Part_of_a_file_can_be_asked_for()
    {
        var bytes = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        var added = await UploadOkAsync(bytes, "data.bin");

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/attachments/{added.Id}");
        request.Headers.Range = new RangeHeaderValue(100, 199);
        var response = await _owner.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(bytes[100..200]);
        response.Content.Headers.ContentRange!.ToString().ShouldBe("bytes 100-199/1000");
    }

    [Fact]
    public async Task A_file_missing_from_the_store_is_not_found_and_not_a_crash()
    {
        var added = await UploadOkAsync(Png, "a.png");
        File.Delete(StoredFiles().ShouldHaveSingleItem());

        var response = await _owner.GetAsync($"/api/attachments/{added.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadErrorAsync()).ShouldBe(new ApiError("not_found"));
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
