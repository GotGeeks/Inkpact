using FsCheck;
using FsCheck.Fluent;
using Inkpact.Api.Import;
using Inkpact.Api.Tests.Testing;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.Import;

/// <summary>
/// The import moves pages in the tree (run 1.16b): for any moves in Joplin and here, in any order, the tree stays whole and
/// a move made here is never undone. The real database, hub and Yjs sidecar.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReimportMovePropertyTests(PostgresFixture postgres) : ReimportHarness(postgres)
{
    private const int Notebooks = 4;
    private const int Notes = 3;
    private const int Items = Notebooks + Notes;

    private enum Who { Joplin, Here, Import }

    /// <summary>
    /// For any sequence of moves in Joplin, moves here and imports: every page stays live and the tree has no cycle; a page
    /// moved here (away from where Joplin last had it) stays where the person put it; and a page that was not moved here is
    /// where Joplin has it now, or, when that would put it below itself, where it was, with the report saying so.
    /// </summary>
    [Fact]
    public async Task Whatever_the_moves_in_both_places_the_tree_stays_whole_and_a_move_made_here_is_never_undone()
    {
        await StartAsync();
        var run = 0;
        // One number a step: who moves (or an import), which item, and where to (the top level or a notebook).
        var sequences = Arb.From(Gen.Choose(1, 10).SelectMany(length => Gen.ArrayOf(Gen.Choose(0, 9999), length)), _ => []);
        var config = Config.QuickThrowOnFailure.WithMaxTest(15).WithQuietOnSuccess(true);

        Check.One(config, Prop.ForAll(sequences, steps => CheckMovesAsync(++run, steps).GetAwaiter().GetResult()));
    }

    private async Task<bool> CheckMovesAsync(int run, int[] steps)
    {
        // Items 0 to 3 are notebooks, 4 to 6 notes. A place is -1 for the top level, or a notebook's number.
        string Name(int item) => item < Notebooks ? $"m{run}f{item}" : $"m{run}n{item - Notebooks}";
        var joplin = Enumerable.Range(0, Items).Select(i => i < Notebooks ? -1 : 0).ToArray();
        JexBuilder Export()
        {
            var jex = new JexBuilder();
            for (var i = 0; i < Items; i++)
            {
                var parent = joplin[i] < 0 ? null : Name(joplin[i]);
                jex = i < Notebooks ? jex.Folder(Name(i), parent: parent) : jex.Note(Name(i), parent, body: "x", title: Name(i));
            }

            return jex;
        }

        await ImportAsync(Export());
        var ids = new Guid[Items];
        for (var i = 0; i < Items; i++)
        {
            ids[i] = (await PageOfAsync(Name(i))).Id;
        }

        var ok = true;
        foreach (var step in steps)
        {
            var (who, item, place) = ((Who)(step % 3), step / 3 % Items, step / (3 * Items) % (Notebooks + 1) - 1);
            switch (who)
            {
                case Who.Joplin:
                    // Joplin has no cycles, and a note is always in a notebook.
                    if (item < Notebooks ? !IsBelow(place < 0 ? null : place, item, p => joplin[p] < 0 ? null : joplin[p]) : place >= 0)
                    {
                        joplin[item] = place;
                    }

                    break;
                case Who.Here:
                    var parents = await ParentsAsync(ids);
                    Guid? target = place < 0 ? null : ids[place];
                    if (target != parents[ids[item]] && !IsBelow(target, ids[item], p => parents[p]))
                    {
                        ok &= await PersonMovesAsync(Name(item), target) is null;
                    }

                    break;
                case Who.Import:
                    ok &= await ImportAndCheckAsync(ids, Name, joplin, Export);
                    break;
            }
        }

        // Each sequence ends with an import, so every move is looked at once.
        return ok && await ImportAndCheckAsync(ids, Name, joplin, Export);
    }

    private async Task<bool> ImportAndCheckAsync(Guid[] ids, Func<int, string> name, int[] joplin, Func<JexBuilder> export)
    {
        var before = await ParentsAsync(ids);
        var bases = new string?[Items];
        for (var i = 0; i < Items; i++)
        {
            bases[i] = ImportBase.FromJson((await ItemOfAsync(name(i))).Base)?.ParentId;
        }

        var import = await ImportAsync(export());

        var after = await ParentsAsync(ids);
        var live = await UseDbAsync(db => db.Pages.Where(p => ids.Contains(p.Id)).AllAsync(p => p.DeletedAt == null));
        var ok = CountsOf(import).Failed == 0 && live && ids.All(id => !IsBelow(after[id], id, p => after[p]));

        var pageOf = Enumerable.Range(0, Notebooks).ToDictionary(i => JexBuilder.Id(name(i)), i => ids[i]);
        for (var i = 0; i < Items; i++)
        {
            if (bases[i] is not { } baseParent)
            {
                return false;
            }

            var id = ids[i];
            Guid? basePlace = baseParent.Length == 0 ? null : pageOf[baseParent];
            var joplinParent = joplin[i] < 0 ? "" : JexBuilder.Id(name(joplin[i]));
            if (before[id] != basePlace || joplinParent == baseParent)
            {
                // Moved here, or not moved in Joplin: it stays.
                ok &= after[id] == before[id];
            }
            else
            {
                Guid? joplinPlace = joplin[i] < 0 ? null : ids[joplin[i]];
                ok &= after[id] == joplinPlace
                    || (after[id] == before[id] && CodesOf(import, name(i)).Contains(NoteCodes.MoveCycle));
            }
        }

        return ok;
    }

    /// <summary>Whether <paramref name="place"/> is <paramref name="item"/> or below it; a walk up that does not end counts as below too.</summary>
    private static bool IsBelow<T>(T? place, T item, Func<T, T?> parentOf)
        where T : struct
    {
        var at = place;
        for (var steps = 0; at is { } p; steps++)
        {
            if (p.Equals(item) || steps > Items)
            {
                return true;
            }

            at = parentOf(p);
        }

        return false;
    }

    private Task<Dictionary<Guid, Guid?>> ParentsAsync(Guid[] ids) =>
        UseDbAsync(db => db.Pages.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ParentId));
}
