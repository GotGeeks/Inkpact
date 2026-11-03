using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using Inkpact.Api.Search;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace Inkpact.Api.Tests.Search;

/// <summary>
/// How fast the first results come back on a workspace of 10,000 pages (the architecture's target: the 95th percentile under
/// 200 ms), asked over HTTP as the app runs it, with pooled connections. Slow on purpose, so
/// <c>dotnet test server --filter "Category!=Slow"</c> leaves it out while working; <c>pnpm check</c> runs it.
/// </summary>
[Collection(SlowCollection.Name)]
[Trait("Category", "Slow")]
public sealed class SearchTimingTests(PostgresFixture postgres, ITestOutputHelper output) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const int Notebooks = 100;
    private const int NotesPerNotebook = 99;
    private const int LargeNotes = 20;

    private InkpactFactory _factory = null!;
    private HttpClient _client = null!;
    private Guid _workspaceId;

    public async Task InitializeAsync()
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(pooled: true));
        _client = await _factory.SignedInClientAsync();
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

    /// <summary>Words made of syllables, some with an accent, so that a prefix of two letters matches many of them, as in real notes.</summary>
    private static string[] Vocabulary(Random random)
    {
        string[] syllables = ["ba", "ke", "lo", "mi", "su", "ta", "ve", "zo", "ren", "ing", "dor", "pla", "ni", "fo", "gri"];
        return
        [
            .. Enumerable.Range(0, 3000).Select(_ =>
            {
                var word = string.Concat(Enumerable.Range(0, random.Next(2, 5)).Select(_ => syllables[random.Next(syllables.Length)]));
                return random.Next(20) == 0 ? word.Replace('e', 'é') : word;
            }).Distinct(),
        ];
    }

    private static string Words(Random random, string[] vocabulary, int count)
    {
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            text.Append(vocabulary[random.Next(vocabulary.Length)]).Append(i % 12 == 11 ? ".\n" : " ");
        }

        return text.ToString();
    }

    private async Task SeedAsync(Random random, string[] vocabulary)
    {
        var tags = Enumerable.Range(0, 40).Select(_ => vocabulary[random.Next(vocabulary.Length)]).Distinct().ToArray();
        var now = DateTimeOffset.UtcNow;
        var key = 0;
        var large = 0;
        var pages = new List<Page>();
        for (var notebook = 0; notebook < Notebooks; notebook++)
        {
            var parent = new Page
            {
                WorkspaceId = _workspaceId,
                Title = Words(random, vocabulary, 2).Trim(),
                SortKey = $"a{key++:D6}",
                CreatedAt = now,
                UpdatedAt = now,
            };
            pages.Add(parent);
            for (var note = 0; note < NotesPerNotebook; note++)
            {
                var isLarge = large < LargeNotes && random.Next(500) == 0;
                large += isLarge ? 1 : 0;
                pages.Add(new Page
                {
                    WorkspaceId = _workspaceId,
                    ParentId = parent.Id,
                    Title = Words(random, vocabulary, random.Next(2, 5)).Trim(),
                    SortKey = $"a{key++:D6}",
                    CreatedAt = now,
                    UpdatedAt = now.AddMinutes(-random.Next(100_000)),
                    ContentText = Words(random, vocabulary, isLarge ? 8_000 : 250),
                    TagText = string.Join(' ', Enumerable.Range(0, random.Next(4)).Select(_ => tags[random.Next(tags.Length)]).Distinct()),
                });
            }
        }

        foreach (var chunk in pages.Chunk(500))
        {
            await UseDbAsync(async db =>
            {
                db.Pages.AddRange(chunk);
                await db.SaveChangesAsync();
                return 0;
            });
        }

        await UseDbAsync(db => db.Database.ExecuteSqlRawAsync("ANALYZE pages"));
    }

    [Fact]
    public async Task The_first_results_of_a_search_in_ten_thousand_pages_come_back_in_two_hundred_milliseconds_at_the_95th_percentile()
    {
        var random = new Random(17);
        var vocabulary = Vocabulary(random);
        await SeedAsync(random, vocabulary);
        (await UseDbAsync(db => db.Pages.CountAsync())).ShouldBeGreaterThanOrEqualTo(10_000);

        // One to three prefixes of two to six letters, as typed: the short ones match nearly every page, which is the worst case.
        var queries = Enumerable.Range(0, 100).Select(_ => string.Join(' ', Enumerable.Range(0, random.Next(1, 4)).Select(_ =>
        {
            var word = vocabulary[random.Next(vocabulary.Length)];
            return word[..Math.Min(word.Length, random.Next(2, 7))];
        }))).ToList();
        queries.AddRange(["ba", "ke", "lo", "mi", "su", "ta"]);

        foreach (var warmUp in new[] { "ba", "zo", "gri" })
        {
            (await _client.GetAsync($"/api/workspaces/{_workspaceId}/search?q={warmUp}")).EnsureSuccessStatusCode();
        }

        var times = new List<(double Milliseconds, string Query, int Hits)>();
        foreach (var query in queries)
        {
            var clock = Stopwatch.StartNew();
            var response = await _client.GetAsync($"/api/workspaces/{_workspaceId}/search?q={Uri.EscapeDataString(query)}");
            var result = await response.Content.ReadFromJsonAsync<SearchResponse>();
            clock.Stop();
            response.EnsureSuccessStatusCode();
            times.Add((clock.Elapsed.TotalMilliseconds, query, result!.Results.Count));
        }

        var sorted = times.OrderBy(t => t.Milliseconds).ToList();
        var p95 = sorted[(int)Math.Ceiling(sorted.Count * 0.95) - 1].Milliseconds;
        output.WriteLine($"{times.Count} searches in 10,000 pages: median {sorted[sorted.Count / 2].Milliseconds:0} ms, 95th percentile {p95:0} ms, slowest {sorted[^1].Milliseconds:0} ms ('{sorted[^1].Query}')");
        times.Count(t => t.Hits > 0).ShouldBeGreaterThan(90, "the queries have to find something, or the test measures nothing");
        p95.ShouldBeLessThan(200);
    }
}
