using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Attachments;

/// <summary>What the client gets back for an attachment; <c>Inline</c> says whether the editor can show it as an image.</summary>
public sealed record AttachmentDto(Guid Id, string FileName, string ContentType, long Size, bool Inline)
{
    public static AttachmentDto From(Attachment attachment) =>
        new(attachment.Id, attachment.FileName, attachment.ContentType, attachment.Size, AttachmentFiles.IsInline(attachment.ContentType));
}

/// <summary>Adds files to pages and finds them again. The upload endpoint uses it, and the Joplin import (run 1.15) will.</summary>
public sealed class AttachmentService(
    InkpactDbContext db,
    IAttachmentStore store,
    AttachmentOptions options,
    TimeProvider time,
    ILogger<AttachmentService> logger)
{
    /// <summary>
    /// Stores the content and adds it to the page under the given name. Null when the file is empty. Throws
    /// <see cref="FileTooLargeException"/> when it is over the limit. The caller has checked the role on the page.
    /// </summary>
    public async Task<AttachmentDto?> AddAsync(Guid pageId, Guid? userId, string? fileName, Stream content, CancellationToken cancellationToken = default)
    {
        var stored = await store.PutAsync(content, options.MaxFileSize, cancellationToken);
        if (stored.Size == 0)
        {
            return null;
        }

        var name = AttachmentFiles.CleanFileName(fileName);
        var attachment = new Attachment
        {
            PageId = pageId,
            FileName = name,
            ContentType = AttachmentFiles.DetectType(stored.Head, name),
            Size = stored.Size,
            Sha256 = stored.Sha256,
            CreatedAt = time.GetUtcNow(),
            CreatedBy = userId,
        };
        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(cancellationToken);
        return AttachmentDto.From(attachment);
    }

    /// <summary>The attachment and its content, or null when either is missing. The caller has checked the role.</summary>
    public async Task<(Attachment Attachment, Stream Content)?> OpenAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var attachment = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == attachmentId, cancellationToken);
        if (attachment is null)
        {
            return null;
        }

        var content = await store.OpenReadAsync(attachment.Sha256, cancellationToken);
        if (content is null)
        {
            // The row is there but the file is not: a volume that was lost or restored without it.
            logger.LogError("The file of attachment {AttachmentId} ({Sha256}) is missing from the store", attachment.Id, attachment.Sha256);
            return null;
        }

        return (attachment, content);
    }
}
