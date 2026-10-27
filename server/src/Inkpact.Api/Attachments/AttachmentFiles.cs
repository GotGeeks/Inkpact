using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;

namespace Inkpact.Api.Attachments;

/// <summary>What the server makes of an uploaded file: a safe name, and a type it worked out itself.</summary>
public static class AttachmentFiles
{
    public const int MaxFileNameLength = 255;

    /// <summary>
    /// The only types shown inside a page and served inline, and only when the file's own bytes say so. SVG is not one
    /// of them: it can carry script.
    /// </summary>
    public static readonly IReadOnlySet<string> InlineTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp",
    };

    private const string Fallback = "application/octet-stream";

    private static readonly FileExtensionContentTypeProvider Extensions = new();

    // Windows refuses these in a name; the rest of the world does not mind losing them.
    private static readonly string Refused = "<>:\"/\\|?*";

    /// <summary>
    /// The type of a file. A PNG, JPEG, GIF or WebP is recognised by its first bytes, whatever its name says. Anything
    /// else gets the type its extension suggests, except that an image type the bytes did not confirm becomes
    /// <c>application/octet-stream</c>, so no file is ever served inline on the strength of its name.
    /// </summary>
    public static string DetectType(ReadOnlySpan<byte> head, string fileName)
    {
        if (Signature(head) is { } recognised)
        {
            return recognised;
        }

        return Extensions.TryGetContentType(fileName, out var byName) && !InlineTypes.Contains(byName)
            ? byName
            : Fallback;
    }

    public static bool IsInline(string contentType) => InlineTypes.Contains(contentType);

    private static string? Signature(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return null;
    }

    /// <summary>
    /// A name that is safe in a download header and on any file system, for when the export writes it out: no folders,
    /// no control characters, nothing Windows refuses, at most 255 characters with the extension kept. An empty
    /// result is <c>file</c>.
    /// </summary>
    public static string CleanFileName(string? name)
    {
        // Normalising refuses half a surrogate pair (a JSON escape such as \ud800 decodes to one), so those go first.
        var text = WithoutBrokenCharacters(name ?? "").Normalize(NormalizationForm.FormC);

        // Only the last part of a path; browsers send just a name, but a crafted request may not.
        text = text[(text.LastIndexOfAny(['/', '\\']) + 1)..];

        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || (rune.IsAscii && Refused.Contains((char)rune.Value))
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.PrivateUse)
            {
                continue;
            }

            builder.Append(rune.ToString());
        }

        // Windows also drops trailing dots and spaces, which would make two different names one.
        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned.All(c => c == '.'))
        {
            return "file";
        }

        // CON, NUL, COM1.txt and the like name devices on Windows, whatever their extension.
        if (WindowsDeviceName.IsMatch(cleaned))
        {
            cleaned = "_" + cleaned;
        }

        return Shorten(cleaned);
    }

    private static readonly Regex WindowsDeviceName = new(
        @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(\..*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string WithoutBrokenCharacters(string text)
    {
        var builder = new StringBuilder(text.Length);
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var used) == OperationStatus.Done)
            {
                builder.Append(rest[..used]);
            }

            rest = rest[Math.Max(used, 1)..];
        }

        return builder.ToString();
    }

    private static string Shorten(string name)
    {
        if (name.Length <= MaxFileNameLength)
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        if (extension.Length > 20)
        {
            extension = "";
        }

        return CutAtTextElement(name[..^extension.Length], MaxFileNameLength - extension.Length) + extension;
    }

    /// <summary>The longest start of the text that fits, without cutting a character or emoji in half.</summary>
    private static string CutAtTextElement(string text, int maxLength)
    {
        var length = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var next = length + elements.GetTextElement().Length;
            if (next > maxLength)
            {
                break;
            }

            length = next;
        }

        return text[..length];
    }
}
