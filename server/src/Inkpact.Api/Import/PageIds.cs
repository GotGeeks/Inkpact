using System.Security.Cryptography;
using System.Text;

namespace Inkpact.Api.Import;

/// <summary>
/// The id a page made from a Joplin note or notebook gets (run 1.16c). It is worked out from the workspace and the id in
/// the export, so a link to a note can be written before that note is imported, and still points to the right page when the
/// note is imported by a later run.
/// </summary>
public static class PageIds
{
    /// <summary>A version 8 (name-based) UUID from the SHA-256 of the workspace id, <c>joplin</c> and the Joplin id.</summary>
    public static Guid For(Guid workspaceId, string joplinId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{workspaceId:D}\njoplin\n{joplinId}"));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
