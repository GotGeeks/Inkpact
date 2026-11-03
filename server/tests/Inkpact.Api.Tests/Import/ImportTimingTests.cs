using System.Diagnostics;
using Inkpact.Api.Import;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Inkpact.Api.Tests.Import;

/// <summary>
/// How long a large library takes (run 1.16d), as the app runs it: pooled database connections, the real hub and Yjs sidecar.
/// Slow on purpose, so <c>dotnet test server --filter "Category!=Slow"</c> leaves them out while working; <c>pnpm check</c> runs them.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Slow")]
public sealed class ImportTimingTests(PostgresFixture postgres, ITestOutputHelper output) : ReimportHarness(postgres)
{
    private const int Notebooks = 50;
    private const int NotesPerNotebook = 100;
    private const int Notes = Notebooks * NotesPerNotebook;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    /// <summary>
    /// 5,000 notes in 50 notebooks (some nested), with formatting, links and lists; one note in ten has a picture of its own.
    /// With <paramref name="changed"/>, every tenth note has other text, so 500 notes changed in Joplin.
    /// </summary>
    private static JexBuilder Library(bool changed)
    {
        var jex = new JexBuilder();
        for (var folder = 0; folder < Notebooks; folder++)
        {
            jex.Folder($"f{folder}", folder % 5 == 0 ? null : $"f{folder - folder % 5}");
            for (var note = 0; note < NotesPerNotebook; note++)
            {
                var picture = note % 10 == 0 ? $"\n\n![a picture](:/{JexBuilder.Id($"p{folder}-{note}")})" : "";
                var text = changed && note % 10 == 5 ? "Rewritten in Joplin" : "Some **text** with a [link](https://example.com/" + note + ")";
                jex.Note($"n{folder}-{note}", $"f{folder}", body: $"# Note {note}\n\n{text}.\n\n- one\n- two{picture}", title: $"Note {note:000}");
                if (picture.Length > 0)
                {
                    jex.Resource($"p{folder}-{note}", "picture.png", [.. Png, (byte)folder, (byte)note], "image/png");
                }
            }
        }

        return jex;
    }

    [Fact]
    public async Task Five_thousand_notes_import_in_five_minutes_the_same_file_again_in_thirty_seconds_and_five_hundred_changes_are_timed()
    {
        await StartAsync();

        var clock = Stopwatch.StartNew();
        var first = await ImportAsync(Library(changed: false));
        var firstTime = clock.Elapsed;
        output.WriteLine($"5,000 notes, 50 notebooks, 500 pictures: {firstTime.TotalSeconds:0.0} s");
        first.State.ShouldBe(ImportStates.Completed);
        CountsOf(first).Failed.ShouldBe(0, first.Report);
        CountsOf(first).Notes.ShouldBe(Notes);
        firstTime.ShouldBeLessThan(TimeSpan.FromMinutes(5));
        (await UseDbAsync(db => db.Pages.CountAsync())).ShouldBe(Notes + Notebooks);

        clock.Restart();
        var again = await ImportAsync(Library(changed: false));
        var againTime = clock.Elapsed;
        output.WriteLine($"The same file again: {againTime.TotalSeconds:0.0} s");
        var counts = CountsOf(again);
        (counts.Failed, counts.Updated, counts.Notes).ShouldBe((0, 0, 0));
        counts.AlreadyImported.ShouldBe(Notes + Notebooks);
        againTime.ShouldBeLessThan(TimeSpan.FromSeconds(30));

        clock.Restart();
        var changed = await ImportAsync(Library(changed: true));
        var changedTime = clock.Elapsed;
        output.WriteLine($"After 500 notes changed: {changedTime.TotalSeconds:0.0} s");
        (CountsOf(changed).Failed, CountsOf(changed).Updated).ShouldBe((0, Notes / 10));
        changedTime.ShouldBeLessThan(TimeSpan.FromMinutes(5));
    }
}
