using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Inkpact.Api.Import;

namespace Inkpact.Api.Tests.Import;

public sealed class JexArchiveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"inkpact-jex-{Guid.NewGuid():N}");

    public JexArchiveTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(JexBuilder jex) => jex.ToFile(_directory);

    private string Write(byte[] bytes)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.jex");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static async Task<JexException> ReadFailureAsync(string path) =>
        await Should.ThrowAsync<JexException>(() => JexArchive.ReadIndexAsync(path, default));

    [Fact]
    public async Task Every_kind_of_item_is_found_with_what_the_import_needs()
    {
        var path = Write(new JexBuilder()
            .Folder("projects")
            .Folder("archive", parent: "projects")
            .Note("plan", "projects", body: "# Plan", todo: true, due: "1793358000000", completed: "1793358100000")
            .Note("clip", "archive", body: "<p>x</p>", html: true)
            .Tag("work")
            .NoteTag("plan", "work")
            .Resource("pic", "picture.png", [1, 2, 3, 4])
            .Other("revision", 13));

        var index = await JexArchive.ReadIndexAsync(path, default);

        index.Folders.Keys.Order().ShouldBe(new[] { JexBuilder.Id("projects"), JexBuilder.Id("archive") }.Order());
        index.Folders[JexBuilder.Id("archive")].ParentId.ShouldBe(JexBuilder.Id("projects"));
        var plan = index.Notes[JexBuilder.Id("plan")];
        plan.IsTodo.ShouldBeTrue();
        plan.IsHtml.ShouldBeFalse();
        plan.Due!.Value.ToUnixTimeMilliseconds().ShouldBe(1793358000000);
        plan.Completed!.Value.ToUnixTimeMilliseconds().ShouldBe(1793358100000);
        plan.Created!.Value.ShouldBe(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        index.Notes[JexBuilder.Id("clip")].IsHtml.ShouldBeTrue();
        index.Tags[JexBuilder.Id("work")].Title.ShouldBe("work");
        index.TagsOfNote[JexBuilder.Id("plan")].ShouldBe([JexBuilder.Id("work")]);
        index.Skipped.ShouldBe(1);
    }

    [Fact]
    public async Task A_resource_has_its_name_from_its_item_and_its_bytes_from_the_archive()
    {
        var bytes = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
        var path = Write(new JexBuilder().Folder("f").Resource("pic", "my picture.png", bytes).Resource("other", "b.pdf", [9, 9]));

        var index = await JexArchive.ReadIndexAsync(path, default);
        var resource = index.Resources[JexBuilder.Id("pic")];

        resource.Title.ShouldBe("my picture.png");
        resource.Extension.ShouldBe("png");
        resource.DataLength.ShouldBe(bytes.Length);
        await using var content = JexArchive.OpenSlice(path, resource.DataOffset, resource.DataLength);
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        copy.ToArray().ShouldBe(bytes);
        // The slice stops where the file does, and not at the end of the archive.
        await using var second = JexArchive.OpenSlice(path, index.Resources[JexBuilder.Id("other")].DataOffset, 2);
        using var secondCopy = new MemoryStream();
        await second.CopyToAsync(secondCopy);
        secondCopy.ToArray().ShouldBe([9, 9]);
    }

    [Fact]
    public async Task A_resource_whose_item_is_missing_is_not_a_resource_and_an_item_without_bytes_has_none()
    {
        var path = Write(new JexBuilder()
            .Folder("f")
            .Entry($"resources/{JexBuilder.Id("stray")}.png", [1, 2, 3])
            .Raw($"{JexBuilder.Id("nobytes")}.md", $"missing.png\n\nid: {JexBuilder.Id("nobytes")}\nfile_extension: png\ntype_: 4"));

        var index = await JexArchive.ReadIndexAsync(path, default);

        index.Resources.ContainsKey(JexBuilder.Id("stray")).ShouldBeFalse();
        index.Resources[JexBuilder.Id("nobytes")].DataOffset.ShouldBe(-1);
    }

    [Fact]
    public async Task The_raw_bytes_of_an_item_are_read_from_where_the_index_says()
    {
        var path = Write(new JexBuilder().Folder("f").Note("n", "f", body: "the body"));
        var index = await JexArchive.ReadIndexAsync(path, default);
        var note = index.Notes[JexBuilder.Id("n")];

        var bytes = await JexArchive.ReadItemBytesAsync(path, note.Offset, note.Length, default);

        JexItem.Parse(Encoding.UTF8.GetString(bytes!))!.Body.ShouldBe("the body");
    }

    [Fact]
    public async Task Files_that_are_not_items_are_left_alone_and_nothing_is_extracted()
    {
        var path = Write(new JexBuilder()
            .Folder("f")
            .Entry("../../evil.md", "x"u8.ToArray())
            .Entry("/etc/passwd", "x"u8.ToArray())
            .Entry("resources/../../x", "x"u8.ToArray())
            .Entry("readme.txt", "x"u8.ToArray())
            .Entry("A".PadRight(32, 'A') + ".md", "x"u8.ToArray()));

        var index = await JexArchive.ReadIndexAsync(path, default);

        index.Folders.Count.ShouldBe(1);
        index.Skipped.ShouldBe(5);
        Directory.GetFiles(_directory).Length.ShouldBe(1);
    }

    [Fact]
    public async Task Links_and_directories_in_the_archive_are_not_followed()
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "resources/"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, $"{JexBuilder.Id("link")}.md") { LinkName = "/etc/passwd" });
            var text = Encoding.UTF8.GetBytes($"F\n\nid: {JexBuilder.Id("f")}\nparent_id: \ntype_: 2");
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{JexBuilder.Id("f")}.md") { DataStream = new MemoryStream(text) });
        }

        var index = await JexArchive.ReadIndexAsync(Write(stream.ToArray()), default);

        index.Folders.Count.ShouldBe(1);
        index.Notes.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_item_that_does_not_read_as_one_is_counted_as_unreadable()
    {
        var path = Write(new JexBuilder()
            .Folder("f")
            .Raw($"{JexBuilder.Id("broken")}.md", "no properties here")
            .Raw($"{JexBuilder.Id("other")}.md", $"T\n\nid: {JexBuilder.Id("wrong")}\ntype_: 2"));

        var index = await JexArchive.ReadIndexAsync(path, default);

        index.Unreadable.ShouldBe(2);
        index.Folders.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_item_too_big_to_read_is_listed_with_its_title_and_not_read()
    {
        var path = Write(new JexBuilder().Folder("f").Note("huge", "f", body: new string('x', 5000), title: "The huge one"));

        var index = await JexArchive.ReadIndexAsync(path, default, maxItemSize: 1000);

        index.Oversized.Select(o => o.Title).ShouldBe(["The huge one"]);
        index.Notes.ShouldBeEmpty();
        index.Folders.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Too_many_entries_stop_the_read()
    {
        var jex = new JexBuilder();
        for (var i = 0; i < 20; i++)
        {
            jex.Folder($"f{i}");
        }

        var failure = await Should.ThrowAsync<JexException>(() => JexArchive.ReadIndexAsync(Write(jex), default, maxEntries: 10));

        failure.Code.ShouldBe("import_too_many_items");
    }

    [Fact]
    public async Task Something_that_is_not_a_tar_is_invalid()
    {
        (await ReadFailureAsync(Write(Encoding.UTF8.GetBytes(new string('x', 5000))))).Code.ShouldBe("import_invalid");
    }

    [Fact]
    public async Task A_zip_is_invalid_and_does_not_look_like_a_tar()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var entry = zip.CreateEntry("a.md").Open();
            await entry.WriteAsync("hello"u8.ToArray());
        }

        var path = Write(stream.ToArray());

        (await ReadFailureAsync(path)).Code.ShouldBe("import_invalid");
        (await JexArchive.LooksLikeTarAsync(path, default)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_tar_that_is_cut_off_is_invalid()
    {
        var bytes = new JexBuilder().Folder("f").Note("n", "f", body: new string('x', 3000)).ToBytes();

        (await ReadFailureAsync(Write(bytes[..(bytes.Length - 3000)]))).Code.ShouldBe("import_invalid");
    }

    [Fact]
    public async Task A_tar_with_nothing_the_import_uses_is_empty()
    {
        (await ReadFailureAsync(Write(new JexBuilder().Other("revision", 13).Entry("readme.txt", [1])))).Code.ShouldBe("import_empty");
    }

    [Fact]
    public async Task A_real_tar_looks_like_one_and_an_empty_file_does_not()
    {
        (await JexArchive.LooksLikeTarAsync(Write(new JexBuilder().Folder("f")), default)).ShouldBeTrue();
        (await JexArchive.LooksLikeTarAsync(Write([]), default)).ShouldBeFalse();
    }
}
