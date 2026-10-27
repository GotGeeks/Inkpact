using System.Text;
using Inkpact.Api.Attachments;

namespace Inkpact.Api.Tests.Attachments;

public class AttachmentFilesTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];
    private static readonly byte[] Gif = "GIF89a\x01\x00\x01\x00\x80\x00"u8.ToArray();
    private static readonly byte[] WebP = [.. "RIFF"u8, 0x24, 0x00, 0x00, 0x00, .. "WEBP"u8];

    public static TheoryData<byte[], string, string> Types => new()
    {
        // Recognised by the bytes, whatever the name says.
        { Png, "photo.png", "image/png" },
        { Png, "notes.txt", "image/png" },
        { Png, "no-extension", "image/png" },
        { Jpeg, "IMG_0001.JPG", "image/jpeg" },
        { Gif, "funny.gif", "image/gif" },
        { WebP, "sticker.webp", "image/webp" },
        // Named like an image, but the bytes are not: never an image type, so never inline.
        { Encoding.UTF8.GetBytes("<html><script>alert(1)</script>"), "cat.png", "application/octet-stream" },
        { Encoding.UTF8.GetBytes("<html>"), "cat.jpg", "application/octet-stream" },
        { [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8], "sound.webp", "application/octet-stream" },
        // SVG and HTML keep their type, and are served as downloads.
        { Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script/></svg>"), "logo.svg", "image/svg+xml" },
        { Encoding.UTF8.GetBytes("<!doctype html>"), "page.html", "text/html" },
        // Everything else by its extension.
        { Encoding.UTF8.GetBytes("%PDF-1.7"), "manual.pdf", "application/pdf" },
        { Encoding.UTF8.GetBytes("hello"), "readme.txt", "text/plain" },
        { Encoding.UTF8.GetBytes("hello"), "readme", "application/octet-stream" },
        { Encoding.UTF8.GetBytes("hello"), "archive.unknownext", "application/octet-stream" },
        // Too short to be anything.
        { [0x89, 0x50], "tiny.png", "application/octet-stream" },
        { [], "empty.bin", "application/octet-stream" },
    };

    [Theory]
    [MemberData(nameof(Types))]
    public void The_type_comes_from_the_bytes_for_images_and_from_the_name_for_the_rest(byte[] head, string fileName, string expected)
    {
        AttachmentFiles.DetectType(head, fileName).ShouldBe(expected);
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("image/gif", true)]
    [InlineData("image/webp", true)]
    [InlineData("image/svg+xml", false)]
    [InlineData("text/html", false)]
    [InlineData("application/pdf", false)]
    [InlineData("IMAGE/PNG", false)] // the server writes them itself, in lower case
    public void Only_the_four_recognised_image_types_are_inline(string contentType, bool inline)
    {
        AttachmentFiles.IsInline(contentType).ShouldBe(inline);
    }

    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("Vakantie foto's 2026.jpeg", "Vakantie foto's 2026.jpeg")]
    [InlineData("  spaced.txt  ", "spaced.txt")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\windows\\system.ini", "system.ini")]
    [InlineData("C:\\Users\\jos\\Desktop\\scan.pdf", "scan.pdf")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "abcdefgh.txt")]
    [InlineData("line\nbreak\t.txt", "linebreak.txt")]
    [InlineData("null\0byte.txt", "nullbyte.txt")]
    [InlineData("right\u202Eto\u202Cleft.txt", "righttoleft.txt")] // a bidi override would disguise the extension
    [InlineData("zero\u200Bwidth.txt", "zerowidth.txt")]
    [InlineData("ends with dots...", "ends with dots")]
    [InlineData("crème brûlée.md", "crème brûlée.md")]
    [InlineData("日本語のノート.pdf", "日本語のノート.pdf")]
    [InlineData("party 🎉.png", "party 🎉.png")]
    [InlineData("", "file")]
    [InlineData("   ", "file")]
    [InlineData("...", "file")]
    [InlineData("folder/", "file")]
    [InlineData(null, "file")]
    public void A_name_is_cleaned_to_something_safe_everywhere(string? name, string expected)
    {
        AttachmentFiles.CleanFileName(name).ShouldBe(expected);
    }

    [Fact]
    public void Half_a_character_is_dropped_instead_of_failing()
    {
        // In the test body, not in InlineData: xUnit turns a lone surrogate there into replacement characters.
        AttachmentFiles.CleanFileName("half\ud800.txt").ShouldBe("half.txt"); // as a JSON escape decodes it
        AttachmentFiles.CleanFileName("\udc00low.txt").ShouldBe("low.txt");
        AttachmentFiles.CleanFileName("\ud800").ShouldBe("file");
        AttachmentFiles.CleanFileName("party 🎉.png").ShouldBe("party 🎉.png"); // a whole pair stays
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("COM1.tar.gz", "_COM1.tar.gz")]
    [InlineData("lpt9", "_lpt9")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("CONFIG.SYS", "CONFIG.SYS")]
    [InlineData("COM10.txt", "COM10.txt")]
    public void A_windows_device_name_is_not_kept_as_it_is(string name, string expected)
    {
        AttachmentFiles.CleanFileName(name).ShouldBe(expected);
    }

    [Fact]
    public void A_decomposed_name_is_stored_composed()
    {
        // macOS sends "é" as "e" plus a combining accent; the same name must look the same everywhere.
        AttachmentFiles.CleanFileName("cre\u0300me.txt").ShouldBe("crème.txt");
    }

    [Fact]
    public void A_long_name_is_cut_to_255_characters_and_keeps_its_extension()
    {
        var cleaned = AttachmentFiles.CleanFileName(new string('a', 400) + ".pdf");

        cleaned.Length.ShouldBe(AttachmentFiles.MaxFileNameLength);
        cleaned.ShouldEndWith("a.pdf");
    }

    [Fact]
    public void A_long_name_is_never_cut_in_the_middle_of_an_emoji()
    {
        // Each emoji is two UTF-16 units; a cut between them would leave half a character.
        var cleaned = AttachmentFiles.CleanFileName(string.Concat(Enumerable.Repeat("🎉", 200)) + ".png");

        cleaned.Length.ShouldBeLessThanOrEqualTo(AttachmentFiles.MaxFileNameLength);
        cleaned.ShouldEndWith("🎉.png");
        cleaned.EnumerateRunes().ShouldAllBe(r => r.Value != 0xFFFD);
        Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(cleaned)).ShouldBe(cleaned);
    }

    [Fact]
    public void A_very_long_extension_is_not_kept_at_the_cost_of_the_name()
    {
        var cleaned = AttachmentFiles.CleanFileName("report." + new string('x', 300));

        cleaned.Length.ShouldBe(AttachmentFiles.MaxFileNameLength);
        cleaned.ShouldStartWith("report.");
    }
}
