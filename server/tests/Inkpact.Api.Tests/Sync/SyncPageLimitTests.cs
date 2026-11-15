using System.Net;
using System.Net.Http.Json;
using Inkpact.Api.Auth;
using Inkpact.Api.Pages;
using Inkpact.Api.Sync;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Inkpact.Api.Tests.Testing.SyncTestClient;

namespace Inkpact.Api.Tests.Sync;

/// <summary>
/// The size limit on live edits (run B.10): the hub refuses an update that would take a page's stored bytes past
/// <see cref="SyncOptions.MaxStoredBytes"/>, except a small one within the margin, and after a refusal stores nothing more of
/// that subscription. With a queue of 256 KiB, so a test needs kilobytes: the limit is 192 KiB, a small update at most
/// 64 bytes, and small updates go up to 208 KiB. Updates are opaque bytes, as they are to the hub (the sidecar is off).
/// </summary>
public class SyncPageLimitTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const long Queue = 256 * 1024;
    private const int KiB = 1024;
    private const int Small = 64;

    private static readonly SyncOptions Limits = new() { MaxQueuedBytes = Queue };

    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly List<IAsyncDisposable> _sockets = [];
    private InkpactFactory _factory = null!;
    private SyncUser _jos = null!;
    private Guid _workspaceId;

    public async Task InitializeAsync()
    {
        _factory = new InkpactFactory(
            await postgres.CreateDatabaseAsync(),
            time: _clock,
            settings: new Dictionary<string, string>
            {
                ["Sync:MaxQueuedBytes"] = Queue.ToString(),
                // The import's limits must fit what a page may be, or the app does not start.
                ["Import:MaxNoteTextBytes"] = "1000",
                ["Import:MaxNoteUpdateBytes"] = "1000",
            });
        _jos = await SyncUser.SetUpAdministratorAsync(_factory);
        _workspaceId = await UseDbAsync(db => db.Workspaces.Select(w => w.Id).SingleAsync());
    }

    public async Task DisposeAsync()
    {
        foreach (var socket in _sockets)
        {
            await socket.DisposeAsync();
        }

        await _factory.DisposeAsync();
    }

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    private SyncHub Hub => _factory.Services.GetRequiredService<SyncHub>();

    private async Task<SyncTestClient> ConnectAsync(SyncUser? user = null)
    {
        var client = await (user ?? _jos).ConnectAsync();
        _sockets.Add(client);
        return client;
    }

    private async Task<Guid> CreatePageAsync()
    {
        var response = await _jos.Http.PostJsonAsync($"/api/workspaces/{_workspaceId}/pages", new { title = "Page" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<PageChange>())!.Page.Id;
    }

    private Task<long> StoredBytesAsync(Guid pageId) =>
        UseDbAsync(db => db.PageUpdates.Where(u => u.PageId == pageId).SumAsync(u => (long)u.Data.Length));

    private Task<int> StoredCountAsync(Guid pageId) => UseDbAsync(db => db.PageUpdates.CountAsync(u => u.PageId == pageId));

    /// <summary>An update of exactly <paramref name="length"/> bytes, told apart by its first byte.</summary>
    private static byte[] Update(int length, byte mark = 1)
    {
        var update = new byte[length];
        Array.Fill(update, mark);
        return update;
    }

    /// <summary>The next message that is not an update relayed from someone else on the page.</summary>
    private static async Task<Received> NextAnswerAsync(SyncTestClient client)
    {
        while (true)
        {
            var received = await client.ReceiveAsync();
            if (received.Type != MessageType.Update)
            {
                return received;
            }
        }
    }

    /// <summary>Sends an update that is taken: the sender hears nothing back about it before a pong.</summary>
    private static async Task TakenAsync(SyncTestClient client, Guid pageId, byte[] update)
    {
        await client.SendUpdateAsync(pageId, update);
        await client.SendAsync([(byte)MessageType.Ping]);
        var answer = await NextAnswerAsync(client);
        answer.Type.ShouldBe(MessageType.Pong, answer.ToString());
    }

    private static async Task RefusedAsync(SyncTestClient client, Guid pageId, byte[] update)
    {
        await client.SendUpdateAsync(pageId, update);
        var refusal = await NextAnswerAsync(client);
        refusal.Type.ShouldBe(MessageType.Error, refusal.ToString());
        refusal.PageId.ShouldBe(pageId);
        refusal.Code.ShouldBe(SyncErrors.PageTooLarge);
    }

    /// <summary>The page filled to <paramref name="kib"/> KiB through one client, in updates of at most 60 KiB.</summary>
    private static async Task FillAsync(SyncTestClient client, Guid pageId, int kib)
    {
        for (var left = kib; left > 0; left -= 60)
        {
            await TakenAsync(client, pageId, Update(Math.Min(left, 60) * KiB));
        }
    }

    // ---- the figures --------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_limits_are_worked_out_from_the_queue()
    {
        var defaults = new SyncOptions();
        (defaults.MaxStoredBytes, defaults.SmallUpdateBytes, defaults.MaxStoredBytesForSmallUpdates)
            .ShouldBe((12L * 1024 * 1024, 4L * 1024, 13L * 1024 * 1024));
        (Limits.MaxStoredBytes, Limits.SmallUpdateBytes, Limits.MaxStoredBytesForSmallUpdates)
            .ShouldBe((192L * KiB, (long)Small, 208L * KiB));
        // Every page that may be stored still fits the queue with the traffic around it.
        defaults.MaxStoredBytesForSmallUpdates.ShouldBeLessThan(defaults.MaxQueuedBytes);
    }

    // ---- the limit ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Updates_up_to_the_limit_are_stored_and_relayed()
    {
        var page = await CreatePageAsync();
        var writer = await ConnectAsync();
        var reader = await ConnectAsync();
        await writer.JoinAsync(page);
        await reader.JoinAsync(page);

        await FillAsync(writer, page, 120);
        await TakenAsync(writer, page, Update(72 * KiB, mark: 7));

        (await StoredBytesAsync(page)).ShouldBe(Limits.MaxStoredBytes);
        for (var i = 0; i < 3; i++)
        {
            (await reader.ReceiveAsync(MessageType.Update)).PageId.ShouldBe(page);
        }
    }

    [Fact]
    public async Task An_update_that_would_cross_the_limit_is_refused_and_neither_stored_nor_relayed()
    {
        var page = await CreatePageAsync();
        var writer = await ConnectAsync();
        var reader = await ConnectAsync();
        await writer.JoinAsync(page);
        await reader.JoinAsync(page);
        await FillAsync(writer, page, 180);
        for (var i = 0; i < 3; i++)
        {
            await reader.ReceiveAsync(MessageType.Update);
        }

        await RefusedAsync(writer, page, Update(20 * KiB, mark: 9));

        await reader.ExpectSilenceAsync();
        (await StoredCountAsync(page)).ShouldBe(3);
        (await StoredBytesAsync(page)).ShouldBe(180 * KiB);

        // The other connection is not refused for it: its own update fits, and reaches the one that was refused.
        await TakenAsync(reader, page, Update(Small, mark: 3));
        (await writer.ReceiveAsync(MessageType.Update)).Payload.ShouldBe(Update(Small, mark: 3));
    }

    [Fact]
    public async Task After_a_refusal_nothing_more_of_that_subscription_is_stored_until_it_subscribes_again()
    {
        var page = await CreatePageAsync();
        var writer = await ConnectAsync();
        await writer.JoinAsync(page);
        await FillAsync(writer, page, 180);
        await RefusedAsync(writer, page, Update(20 * KiB));

        // A small update that would fit: it may build on the refused one, so it is dropped, and the client is not told twice.
        await TakenAsync(writer, page, Update(10, mark: 2));
        (await StoredCountAsync(page)).ShouldBe(3);

        await writer.UnsubscribeAsync(page);
        await writer.JoinAsync(page);
        await TakenAsync(writer, page, Update(10, mark: 2));
        (await StoredCountAsync(page)).ShouldBe(4);
    }

    // ---- the margin for small updates ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_at_the_limit_still_takes_small_updates_up_to_the_margin()
    {
        var page = await CreatePageAsync();
        await SeedAsync(page, (int)Limits.MaxStoredBytes);
        var writer = await ConnectAsync();
        await writer.JoinAsync(page);

        // At the limit: a small update (a deletion, say) is taken, a larger one is not.
        await TakenAsync(writer, page, Update(Small));
        await RefusedAsync(writer, page, Update(Small + 1));

        // Small updates go up to the margin and not past it. The log is filled to two small updates short of it first.
        await SeedAsync(page, (int)(Limits.MaxStoredBytesForSmallUpdates - 2 * Small - await StoredBytesAsync(page)));
        var again = await ConnectAsync();
        await again.JoinAsync(page);
        await TakenAsync(again, page, Update(Small));
        await TakenAsync(again, page, Update(Small));

        (await StoredBytesAsync(page)).ShouldBe(Limits.MaxStoredBytesForSmallUpdates);
        await RefusedAsync(again, page, Update(1));
    }

    /// <summary>Adds an update of <paramref name="bytes"/> to the page's log directly, before anyone subscribes to it.</summary>
    private Task SeedAsync(Guid pageId, int bytes) => UseDbAsync(db =>
    {
        db.PageUpdates.Add(new PageUpdate { PageId = pageId, UserId = _jos.UserId, Data = Update(bytes, mark: 5), CreatedAt = DateTimeOffset.UtcNow });
        return db.SaveChangesAsync();
    });

    // ---- the count ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_made_smaller_meanwhile_is_read_again_before_anything_is_refused()
    {
        var page = await CreatePageAsync();
        var first = await ConnectAsync();
        var second = await ConnectAsync();
        await first.JoinAsync(page);
        await second.JoinAsync(page);
        await FillAsync(first, page, 180);

        // A compaction made the page smaller, outside the hub (here: the log is emptied).
        await UseDbAsync(db => db.PageUpdates.Where(u => u.PageId == page).ExecuteDeleteAsync());

        // The hub's own count says it would not fit; a refusal is final for the subscription, so it asks the database first.
        await TakenAsync(second, page, Update(20 * KiB));
        (await StoredBytesAsync(page)).ShouldBe(20 * KiB);
    }

    [Fact]
    public async Task After_the_server_replaces_a_page_its_size_is_read_again()
    {
        var page = await CreatePageAsync();
        var client = await ConnectAsync();
        await client.JoinAsync(page);

        // The server's own replacement, much larger than what the room knew of the page.
        (await Hub.PublishAsync(page, Update(10), [], async token =>
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PageUpdateStore>().AppendAsync(page, _jos.UserId, Update(190 * KiB), token);
            return true;
        }, CancellationToken.None)).ShouldBeTrue();
        (await client.ReceiveAsync()).Type.ShouldBe(MessageType.Replaced);
        (await client.ReceiveAsync()).Type.ShouldBe(MessageType.Sync);

        await RefusedAsync(client, page, Update(10 * KiB));
        (await StoredCountAsync(page)).ShouldBe(1);
    }

    [Fact]
    public async Task A_replacement_that_stored_and_then_failed_still_makes_the_size_be_read_again()
    {
        var page = await CreatePageAsync();
        var client = await ConnectAsync();
        await client.JoinAsync(page);

        // Stored, then the answer was lost: the replacement throws, and nothing is relayed.
        await Should.ThrowAsync<InvalidOperationException>(Hub.PublishAsync(page, Update(10), [], async token =>
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PageUpdateStore>().AppendAsync(page, _jos.UserId, Update(190 * KiB), token);
            throw new InvalidOperationException("the answer was lost");
        }, CancellationToken.None));

        await RefusedAsync(client, page, Update(10 * KiB));
        (await StoredCountAsync(page)).ShouldBe(1);
    }

    [Fact]
    public async Task Updates_of_two_people_count_together()
    {
        var page = await CreatePageAsync();
        var jos = await ConnectAsync();
        var editor = await ConnectAsync(await OtherEditorAsync());
        await jos.JoinAsync(page);
        await editor.JoinAsync(page);

        await FillAsync(jos, page, 100);
        await FillAsync(editor, page, 90);

        await RefusedAsync(jos, page, Update(10 * KiB));
        (await StoredBytesAsync(page)).ShouldBe(190 * KiB);
    }

    [Fact]
    public async Task A_viewer_is_told_the_page_is_read_only_before_anything_about_its_size()
    {
        var page = await CreatePageAsync();
        var jos = await ConnectAsync();
        await jos.JoinAsync(page);
        await FillAsync(jos, page, 192);
        var viewer = await ConnectAsync(await OtherEditorAsync(WorkspaceRole.Viewer));
        await viewer.JoinAsync(page);

        await viewer.SendUpdateAsync(page, Update(20 * KiB));

        (await viewer.ReceiveAsync()).Code.ShouldBe(SyncErrors.ReadOnly);
    }

    [Fact]
    public async Task A_refusal_is_logged_with_numbers_only()
    {
        var page = await CreatePageAsync();
        var writer = await ConnectAsync();
        await writer.JoinAsync(page);
        await FillAsync(writer, page, 180);

        await RefusedAsync(writer, page, Update(20 * KiB));

        var entry = _factory.Logs.Entries.Where(e => e.Message.StartsWith("Refused an update", StringComparison.Ordinal)).ShouldHaveSingleItem();
        entry.Message.ShouldBe($"Refused an update of {20 * KiB} bytes to page {page}, which holds {180 * KiB} bytes");
    }

    private async Task<SyncUser> OtherEditorAsync(WorkspaceRole role = WorkspaceRole.Editor)
    {
        var email = $"other-{Guid.NewGuid():N}@example.com";
        await UseDbAsync(async db =>
        {
            var user = new User { Email = email, DisplayName = "Other", PasswordHash = new Argon2PasswordHasher().Hash(AuthTestExtensions.Password), CreatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = _workspaceId, UserId = user.Id, Role = role, CreatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        return await SyncUser.LogInAsync(_factory, email);
    }
}
