using System.Text;
using Inkpact.Api.Import;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.Import;

/// <summary>
/// A note too large to open as a page's content (run 1.16d): the page keeps it whole as a file, with the files the note uses.
/// The limits are set small, so a note of a few kilobytes stands for one of many megabytes.
/// </summary>
public sealed class ImportSizeTests : ReimportHarness
{
    private readonly PostgresFixture _postgres;

    public ImportSizeTests(PostgresFixture postgres)
        : base(postgres)
    {
        _postgres = postgres;
    }

    private const int TextLimit = 2000;
    private const int UpdateLimit = 3000;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private Task StartWithLimitsAsync(long text = TextLimit, long update = UpdateLimit, long? attachment = null) => StartAsync(new Dictionary<string, string>
    {
        ["Import:MaxNoteTextBytes"] = text.ToString(),
        ["Import:MaxNoteUpdateBytes"] = update.ToString(),
        ["Attachments:MaxFileSize"] = (attachment ?? 50L * 1024 * 1024).ToString(),
    });

    /// <summary>Text of about this many bytes, as plain words.</summary>
    private static string Text(int bytes) => string.Concat(Enumerable.Repeat("word ", bytes / 5 + 1))[..bytes];

    /// <summary>Few bytes of text, but many blocks: its update is much larger than the text.</summary>
    private static string Blocks(int items) => string.Concat(Enumerable.Range(0, items).Select(i => $"- x{i % 10}\n"));

    private static JexBuilder Library(string body, bool html = false, string title = "Big") =>
        new JexBuilder().Folder("work").Note("big", "work", body: body, title: title, html: html);

    private Task<List<Attachment>> FilesOfAsync(Guid pageId) =>
        UseDbAsync(db => db.Attachments.AsNoTracking().Where(a => a.PageId == pageId).OrderBy(a => a.FileName).ToListAsync());

    // ---- the text limit --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_body_at_the_text_limit_is_content_and_one_byte_over_it_is_a_file()
    {
        await StartWithLimitsAsync();
        var jex = new JexBuilder()
            .Folder("work")
            .Note("under", "work", body: Text(TextLimit), title: "Under")
            .Note("over", "work", body: Text(TextLimit + 1), title: "Over");

        var import = await ImportAsync(jex);

        CountsOf(import).Failed.ShouldBe(0);
        (await FilesOfAsync((await PageOfAsync("under")).Id)).ShouldBeEmpty();
        (await FilesOfAsync((await PageOfAsync("over")).Id)).Single().FileName.ShouldBe("Over.md");
        EntryOf(import, "over").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.StoredAsFile]);
        EntriesOf(import).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_body_over_the_text_limit_becomes_a_page_with_the_note_as_a_file_and_the_report_says_so()
    {
        await StartWithLimitsAsync();
        var body = Text(TextLimit + 500);

        var import = await ImportAsync(Library(body));

        var counts = CountsOf(import);
        (counts.Notes, counts.Failed).ShouldBe((1, 0));
        var entry = EntryOf(import, "big");
        entry.Reasons.Select(r => r.Code).ShouldBe([NoteCodes.StoredAsFile]);
        var page = await PageOfAsync("big");
        entry.PageId.ShouldBe(page.Id);
        var file = (await FilesOfAsync(page.Id)).Single();
        (file.FileName, file.Size, file.ExternalId).ShouldBe(("Big.md", Encoding.UTF8.GetByteCount(body), null));
        IsInline(file).ShouldBeFalse();
        // The text of the note is not on the page: nothing was converted.
        (await TextOfAsync(page.Id)).ShouldNotContain("word word word");
    }

    private static bool IsInline(Attachment file) => Inkpact.Api.Attachments.AttachmentFiles.IsInline(file.ContentType);

    [Fact]
    public async Task An_HTML_note_is_kept_as_a_dot_html_file()
    {
        await StartWithLimitsAsync();

        await ImportAsync(Library($"<p>{Text(TextLimit + 100)}</p>", html: true));

        (await FilesOfAsync((await PageOfAsync("big")).Id)).Single().FileName.ShouldBe("Big.html");
    }

    [Fact]
    public async Task A_note_with_files_keeps_them_on_the_page_beside_its_own_file_and_stores_nothing_twice()
    {
        await StartWithLimitsAsync();
        var body = $"![a picture](:/{JexBuilder.Id("pic")})\n\n{Text(TextLimit + 100)}";

        var import = await ImportAsync(Library(body).Resource("pic", "picture.png", Png, "image/png"));

        CountsOf(import).Failed.ShouldBe(0);
        var files = await FilesOfAsync((await PageOfAsync("big")).Id);
        files.Select(f => f.FileName).ShouldBe(["Big.md", "picture.png"]);
        files.Single(f => f.FileName == "picture.png").ExternalId.ShouldBe(JexBuilder.Id("pic"));
        (await UseDbAsync(db => db.Attachments.CountAsync())).ShouldBe(2);

        // The same export again: nothing is stored again.
        await ImportAsync(Library(body).Resource("pic", "picture.png", Png, "image/png"));
        (await UseDbAsync(db => db.Attachments.CountAsync())).ShouldBe(2);
    }

    // ---- the update limit --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_note_that_is_small_text_but_a_large_update_is_a_file_and_a_small_one_is_not()
    {
        await StartWithLimitsAsync(text: 20_000);
        var small = await ImportAsync(Library(Blocks(10), title: "Small"));
        var page = await PageOfAsync("big");
        (await FilesOfAsync(page.Id)).ShouldBeEmpty();
        EntriesOf(small).ShouldBeEmpty();

        var manyBlocks = new JexBuilder().Folder("work").Note("many", "work", body: Blocks(400), title: "Many");
        var import = await ImportAsync(manyBlocks);

        EntryOf(import, "many").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.StoredAsFile]);
        var many = await PageOfAsync("many");
        (await FilesOfAsync(many.Id)).Single().FileName.ShouldBe("Many.md");
    }

    // ---- when the note changes, and in both places ---------------------------------------------------------------------

    [Fact]
    public async Task A_note_that_grows_over_the_limit_in_Joplin_is_replaced_by_its_file()
    {
        await StartWithLimitsAsync();
        await ImportAsync(Library("A short note"));
        var page = await PageOfAsync("big");

        var import = await ImportAsync(Library(Text(TextLimit + 800)));

        CountsOf(import).Updated.ShouldBe(1);
        EntryOf(import, "big").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.StoredAsFile]);
        (await FilesOfAsync(page.Id)).Single().FileName.ShouldBe("Big.md");
        (await TextOfAsync(page.Id)).ShouldNotContain("A short note");
    }

    [Fact]
    public async Task A_note_over_the_limit_that_changed_in_both_places_makes_a_copy_that_is_a_file_and_leaves_the_page_alone()
    {
        await StartWithLimitsAsync();
        await ImportAsync(Library("A short note"));
        var page = await PageOfAsync("big");
        await PersonEditsAsync(page.Id, "Written here");

        var import = await ImportAsync(Library(Text(TextLimit + 800)));

        (await TextOfAsync(page.Id)).ShouldBe("Written here");
        var copy = await UseDbAsync(db => db.Pages.AsNoTracking().SingleAsync(p => p.ExternalId == null && p.Title.Contains("Big") && p.Id != page.Id));
        (await FilesOfAsync(copy.Id)).Single().FileName.ShouldBe("Big.md");
        CountsOf(import).Conflicts.ShouldBe(1);
    }

    // ---- too large to be kept ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_body_larger_than_a_file_may_be_is_skipped_and_no_page_is_made()
    {
        await StartWithLimitsAsync(attachment: 5000);

        var import = await ImportAsync(Library(Text(6000)));

        var counts = CountsOf(import);
        (counts.Skipped, counts.Failed).ShouldBe((1, 0));
        var entry = EntryOf(import, "big");
        entry.Status.ShouldBe(ReportStatus.Skipped);
        entry.Reasons.Select(r => r.Code).ShouldBe([NoteCodes.ItemTooLarge]);
        (await UseDbAsync(db => db.Pages.CountAsync(p => p.ExternalId == JexBuilder.Id("big")))).ShouldBe(0);
    }

    [Fact]
    public async Task A_body_larger_than_a_file_may_be_leaves_none_of_its_files_in_the_store()
    {
        await StartWithLimitsAsync(attachment: 5000);
        var body = $"![a picture](:/{JexBuilder.Id("pic")})\n\n{Text(6000)}";

        var import = await ImportAsync(Library(body).Resource("pic", "picture.png", Png, "image/png"));

        CountsOf(import).Skipped.ShouldBe(1);
        (Directory.Exists(AttachmentsDirectory) ? Directory.EnumerateFiles(AttachmentsDirectory, "*", SearchOption.AllDirectories) : []).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_note_whose_page_of_files_would_still_be_too_large_to_open_is_skipped()
    {
        await StartWithLimitsAsync();
        var jex = Library(string.Concat(Enumerable.Range(0, 60).Select(i => $"[f{i}](:/{JexBuilder.Id($"r{i}")})\n\n")) + Text(TextLimit));
        for (var i = 0; i < 60; i++)
        {
            jex.Resource($"r{i}", $"file{i}.pdf", [.. "%PDF-1.4\n"u8, (byte)i], "application/pdf");
        }

        var import = await ImportAsync(jex);

        EntryOf(import, "big").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.ItemTooLarge]);
        (await UseDbAsync(db => db.Pages.CountAsync(p => p.ExternalId == JexBuilder.Id("big")))).ShouldBe(0);
    }

    // ---- what is already on the page counts too --------------------------------------------------------------------------

    [Fact]
    public async Task A_replacement_whose_page_with_the_old_content_would_be_over_the_limit_is_a_file_though_each_alone_fits()
    {
        await StartWithLimitsAsync();
        // Each about half the update limit: a client that opens the page before the compactor has run is sent both.
        await ImportAsync(Library(Text(1500)));
        var page = await PageOfAsync("big");

        var import = await ImportAsync(Library(Text(1600)));

        CountsOf(import).Updated.ShouldBe(1);
        EntryOf(import, "big").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.StoredAsFile]);
        (await FilesOfAsync(page.Id)).Single().FileName.ShouldBe("Big.md");
        var stored = await UseDbAsync(db => db.PageUpdates.Where(u => u.PageId == page.Id).SumAsync(u => (long)u.Data.Length));
        stored.ShouldBeLessThanOrEqualTo(UpdateLimit);
    }

    [Fact]
    public async Task A_note_converted_again_keeps_the_same_body_file_instead_of_adding_one()
    {
        await StartWithLimitsAsync();
        await ImportAsync(Library(Text(TextLimit + 300)));
        var page = await PageOfAsync("big");
        await UseDbAsync(db => db.ImportItems.ExecuteUpdateAsync(set => set.SetProperty(i => i.ConverterVersion, 1)));

        var import = await ImportAsync(Library(Text(TextLimit + 300)));

        CountsOf(import).Reconverted.ShouldBe(1);
        (await FilesOfAsync(page.Id)).Select(f => f.FileName).ShouldBe(["Big.md"]);
        CountsOf(import).Files.ShouldBe(0);
    }

    [Fact]
    public async Task A_note_renamed_in_Joplin_and_grown_past_what_a_file_may_be_takes_the_title_and_says_the_text_did_not_come()
    {
        await StartWithLimitsAsync(attachment: 5000);
        await ImportAsync(Library("A short note"));
        var page = await PageOfAsync("big");

        var import = await ImportAsync(Library(Text(6000), title: "Renamed"));

        var counts = CountsOf(import);
        (counts.Updated, counts.Skipped).ShouldBe((1, 0));
        var entry = EntryOf(import, "big");
        entry.Status.ShouldBe(ReportStatus.Updated);
        entry.Reasons.Select(r => r.Code).ShouldBe([NoteCodes.ItemTooLarge]);
        (await PageOfAsync("big")).Title.ShouldBe("Renamed");
        (await TextOfAsync(page.Id)).ShouldBe("A short note");
    }

    [Theory]
    [InlineData("2024/05 Meeting\nnotes", false, "2024-05 Meeting notes.md")]
    [InlineData(@"Ideas \ drafts", false, "Ideas - drafts.md")]
    [InlineData("Clip\tfrom the web", true, "Clip from the web.html")]
    [InlineData("  ", false, "note.md")]
    public void The_body_file_is_named_after_the_title_as_the_page_shows_it(string title, bool html, string name)
    {
        ImportRunner.BodyFileName(title, html).ShouldBe(name);
    }

    // ---- the start-up check ------------------------------------------------------------------------------------------------

    private const long HalfOfTheQueue = 8L * 1024 * 1024;

    [Theory]
    [InlineData("Import:MaxNoteTextBytes", HalfOfTheQueue, true)]
    [InlineData("Import:MaxNoteUpdateBytes", HalfOfTheQueue, true)]
    [InlineData("Import:MaxNoteTextBytes", HalfOfTheQueue + 1, false)]
    [InlineData("Import:MaxNoteUpdateBytes", HalfOfTheQueue + 1, false)]
    [InlineData("Import:MaxNoteTextBytes", 0, false)]
    public async Task The_server_starts_with_a_limit_of_at_most_half_the_sync_queue_and_refuses_one_above_it(string setting, long value, bool starts)
    {
        await using var factory = new InkpactFactory(
            await _postgres.CreateDatabaseAsync(), settings: new Dictionary<string, string> { [setting] = value.ToString() });

        if (starts)
        {
            Should.NotThrow(() => _ = factory.Services);
        }
        else
        {
            var error = Should.Throw<Exception>(() => _ = factory.Services);
            Flatten(error).ShouldContain(setting);
        }
    }

    [Fact]
    public async Task A_smaller_sync_queue_lowers_the_ceiling_with_it()
    {
        await using var factory = new InkpactFactory(
            await _postgres.CreateDatabaseAsync(), settings: new Dictionary<string, string> { ["Sync:MaxQueuedBytes"] = (4L * 1024 * 1024).ToString() });

        // The defaults, 8,000,000 bytes of text and 8 MiB of update, are above half of the queue now.
        Flatten(Should.Throw<Exception>(() => _ = factory.Services)).ShouldContain("Import:MaxNoteTextBytes");
    }

    private static string Flatten(Exception error)
    {
        var text = new StringBuilder();
        for (var at = error; at is not null; at = at.InnerException!)
        {
            text.AppendLine(at.Message);
            if (at.InnerException is null)
            {
                break;
            }
        }

        return text.ToString();
    }
}
