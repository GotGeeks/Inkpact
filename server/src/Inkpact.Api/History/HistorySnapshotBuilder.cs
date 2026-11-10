using System.Data;
using System.Text;
using System.Text.Json;
using Inkpact.Api.Crdt;
using Inkpact.Api.Sync;
using Inkpact.Data;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.History;

internal sealed record HistorySnapshot(
    Guid PageId,
    Guid WorkspaceId,
    string Title,
    string FileText,
    int MetadataVersion,
    DateTimeOffset ObservedAt,
    byte[] State,
    byte[] DocumentJson);

internal sealed class HistorySnapshotVerificationException(string message, Exception? inner = null) : Exception(message, inner);

internal interface IHistorySnapshotHooks
{
    Task AfterMetadataReadAsync(Guid pageId, CancellationToken cancellationToken);
}

internal sealed class NoHistorySnapshotHooks : IHistorySnapshotHooks
{
    public Task AfterMetadataReadAsync(Guid pageId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Builds a full, independently readable state from one consistent observation of a page.</summary>
internal sealed class HistorySnapshotBuilder(
    IDbContextFactory<InkpactDbContext> dbFactory,
    IYjsCodec codec,
    TimeProvider time,
    IHistorySnapshotHooks hooks)
{
    private static readonly byte[] EmptyDocument = Encoding.UTF8.GetBytes("""{"type":"doc"}""");

    internal async Task<HistorySnapshot?> BuildAsync(Guid pageId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var page = await db.Pages.AsNoTracking().Where(p => p.Id == pageId)
            .Select(p => new { p.WorkspaceId, p.Title, p.FileText, p.Version })
            .SingleOrDefaultAsync(cancellationToken);
        if (page is null)
        {
            return null;
        }

        var observedAt = time.GetUtcNow().ToUniversalTime();
        await hooks.AfterMetadataReadAsync(pageId, cancellationToken);
        var content = await PageUpdateStore.LoadInTransactionAsync(db, pageId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Even a never-opened page starts with this fixed update on every client.
        var firstParagraph = await codec.BuildAsync(EmptyDocument, cancellationToken);
        IReadOnlyList<byte[]> inputs = [firstParagraph, .. content.Inputs];
        var merged = await codec.CompactAsync(inputs, cancellationToken);
        if (merged.State.Length == 0 || merged.ReplayFingerprint.Length == 0
            || !merged.ReplayFingerprint.AsSpan().SequenceEqual(merged.StateFingerprint))
        {
            throw new HistorySnapshotVerificationException($"The history state of page {pageId} does not match its replay.");
        }

        var replay = await codec.ReadAsync(inputs, cancellationToken);
        var saved = await codec.ReadAsync([merged.State], cancellationToken);
        if (replay.Skipped != 0 || saved.Skipped != 0)
        {
            throw new HistorySnapshotVerificationException($"The history state of page {pageId} contains unsupported document elements.");
        }

        try
        {
            using var replayJson = JsonDocument.Parse(replay.Json);
            using var savedJson = JsonDocument.Parse(saved.Json);
            if (!IsDocument(replayJson.RootElement) || !IsDocument(savedJson.RootElement))
            {
                throw new HistorySnapshotVerificationException($"The history state of page {pageId} is not a document.");
            }

            if (!JsonElement.DeepEquals(replayJson.RootElement, savedJson.RootElement))
            {
                throw new HistorySnapshotVerificationException($"The readable history state of page {pageId} does not match its replay.");
            }
        }
        catch (JsonException ex)
        {
            throw new HistorySnapshotVerificationException($"The history state of page {pageId} cannot be read as document JSON.", ex);
        }

        return new(pageId, page.WorkspaceId, page.Title, page.FileText, page.Version, observedAt, merged.State, saved.Json);
    }

    private static bool IsDocument(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && type.GetString() == "doc";
}
