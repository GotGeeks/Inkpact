using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Inkpact.Api.Attachments;
using Inkpact.Api.Crdt;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace Inkpact.Api.Tests.Export;

/// <summary>
/// The export of 5,000 pages, read by the real sidecar, with a hundred megabytes of attachments among them: done in under a
/// minute, and the memory of the app stays flat, since pages are read one at a time and files pass through in chunks. Slow on
/// purpose, so <c>dotnet test server --filter "Category!=Slow"</c> leaves it out; <c>pnpm check</c> runs it.
/// </summary>
[Collection(SlowCollection.Name)]
[Trait("Category", "Slow")]
public sealed class ExportTimingTests(PostgresFixture postgres, ITestOutputHelper output) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const int Notebooks = 50;
    private const int NotesPerNotebook = 99;
    private const int Files = 10;
    private const int FileSize = 10 * 1024 * 1024;

    private InkpactFactory _factory = null!;
    private HttpClient _client = null!;
    private Guid _workspaceId;

    public async Task InitializeAsync()
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(pooled: true), sidecar: true);
        _client = await _factory.SignedInClientAsync();
        await _factory.Services.GetRequiredService<YjsSidecar>().WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        _workspaceId = await UseDbAsync(db => db.Workspaces.Select(w => w.Id).SingleAsync());
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
    }

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    [Fact]
    public async Task Five_thousand_pages_and_a_hundred_megabytes_of_files_are_exported_in_a_minute_with_the_memory_flat()
    {
        var now = DateTimeOffset.UtcNow;
        var codec = _factory.Services.GetRequiredService<IYjsCodec>();
        var store = _factory.Services.GetRequiredService<IAttachmentStore>();

        // Ten files of ten megabytes, each used by every tenth page; their rows are on the first notebook.
        var notebooks = Enumerable.Range(0, Notebooks).Select(i => new Page
        {
            WorkspaceId = _workspaceId,
            Title = $"Notebook {i}",
            SortKey = $"a{i:D6}",
            CreatedAt = now,
            UpdatedAt = now,
        }).ToList();
        var attachments = new List<Attachment>();
        var updates = new List<byte[]>();
        for (var f = 0; f < Files; f++)
        {
            var bytes = RandomNumberGenerator.GetBytes(FileSize);
            var stored = await store.PutAsync(new MemoryStream(bytes), long.MaxValue);
            var attachment = new Attachment
            {
                PageId = notebooks[0].Id,
                FileName = $"scan {f}.bin",
                ContentType = "application/octet-stream",
                Size = FileSize,
                Sha256 = stored.Sha256,
                CreatedAt = now,
            };
            attachments.Add(attachment);
            var document = Pm.Doc(
                Pm.H(2, Pm.T("Meeting notes")),
                Pm.P(Pm.T("Some "), Pm.T("bold", "bold"), Pm.T(" words and a link to "), Pm.Link("the site", "https://example.com/")),
                Pm.Ul(Pm.Li(Pm.P(Pm.T("first"))), Pm.Li(Pm.P(Pm.T("second")))),
                Pm.P(Pm.File(attachment.Id, attachment.FileName, FileSize)));
            updates.Add(await codec.BuildAsync(Encoding.UTF8.GetBytes(document.ToJsonString()), CancellationToken.None));
        }

        var pages = new List<Page>();
        var rows = new List<PageUpdate>();
        foreach (var notebook in notebooks)
        {
            for (var n = 0; n < NotesPerNotebook; n++)
            {
                var page = new Page
                {
                    WorkspaceId = _workspaceId,
                    ParentId = notebook.Id,
                    Title = $"Note {notebook.Title} {n}",
                    SortKey = $"a{n:D6}",
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                pages.Add(page);
                rows.Add(new PageUpdate { PageId = page.Id, Data = updates[pages.Count % Files], CreatedAt = now });
            }
        }

        await UseDbAsync(async db =>
        {
            db.Pages.AddRange(notebooks);
            await db.SaveChangesAsync();
            db.Attachments.AddRange(attachments);
            await db.SaveChangesAsync();
            return 0;
        });
        foreach (var chunk in pages.Zip(rows).Chunk(500))
        {
            await UseDbAsync(async db =>
            {
                db.Pages.AddRange(chunk.Select(c => c.First));
                await db.SaveChangesAsync();
                db.PageUpdates.AddRange(chunk.Select(c => c.Second));
                await db.SaveChangesAsync();
                return 0;
            });
        }

        // Settled, so that what is measured is the export and not what the set-up left behind.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
                await Task.Delay(10);
            }
        });

        var zipPath = Path.Combine(Path.GetTempPath(), $"inkpact-export-{Guid.NewGuid():N}.zip");
        var clock = Stopwatch.StartNew();
        try
        {
            using (var response = await _client.GetAsync($"/api/workspaces/{_workspaceId}/export", HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(zipPath);
                await (await response.Content.ReadAsStreamAsync()).CopyToAsync(file);
            }

            clock.Stop();
            await stop.CancelAsync();
            await sampler;

            using var archive = ZipFile.OpenRead(zipPath);
            archive.Entries.Count(e => e.FullName.EndsWith(".md", StringComparison.Ordinal) && e.FullName != "README.md").ShouldBe(Notebooks * (NotesPerNotebook + 1));
            archive.Entries.Count(e => e.FullName.StartsWith("_attachments/", StringComparison.Ordinal)).ShouldBe(Files);
            archive.Entries.Where(e => e.FullName.StartsWith("_attachments/", StringComparison.Ordinal)).ShouldAllBe(e => e.Length == FileSize);
            var growth = (peak - baseline) / (1024 * 1024);
            output.WriteLine($"{Notebooks * (NotesPerNotebook + 1)} pages and {Files * FileSize / (1024 * 1024)} MB of files exported in {clock.Elapsed.TotalSeconds:0.0} s, zip {new FileInfo(zipPath).Length / (1024 * 1024)} MB, managed memory grew by {growth} MB");

            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromMinutes(1));
            // The files alone are 100 MB: held in memory, as a buffered zip would, the heap would grow by more than that.
            growth.ShouldBeLessThan(60);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }
}
