using FsCheck;
using FsCheck.Fluent;
using Inkpact.Api.Tags;
using Inkpact.Api.Tests.Testing;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.Tags;

/// <summary>
/// For any sequence of add-and-remove set operations on a page's tags, the page ends with exactly the names a plain
/// model of the same operations gives, and the workspace never has two tags whose lower-case name is the same. The real
/// database.
/// </summary>
public class TagPropertyTests(PostgresFixture postgres) : TagTestBase(postgres)
{
    private const int NamePoolSize = 4;

    private static string NameOf(int n) => ((char)('A' + n % NamePoolSize)).ToString();

    [Fact]
    public void A_pages_tags_always_match_a_plain_model_of_the_same_operations()
    {
        var run = 0;
        var steps = Arb.From(Gen.Choose(1, 12).SelectMany(length => Gen.ArrayOf(Gen.Choose(0, 99), length)), _ => []);
        var config = Config.QuickThrowOnFailure.WithMaxTest(40).WithQuietOnSuccess(true);

        Check.One(config, Prop.ForAll(steps, numbers => RunAsync(++run, numbers).GetAwaiter().GetResult()));
    }

    private async Task<bool> RunAsync(int run, int[] steps)
    {
        var page = await CreatePageAsync($"Page {run}");
        var model = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in steps)
        {
            var add = step % 2 == 0;
            var name = NameOf(step / 2);

            if (add)
            {
                var result = await WithTagsAsync(tags => tags.ChangeTagsAsync(page, [name], []));
                if (!result.Succeeded)
                {
                    return false;
                }

                model.Add(name);
            }
            else
            {
                // Remove by id: only possible if the page already has a tag of this name.
                var current = await WithTagsAsync(tags => tags.GetPageTagsAsync(page));
                var existing = current.SingleOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    continue;
                }

                var result = await WithTagsAsync(tags => tags.ChangeTagsAsync(page, [], [existing.Id]));
                if (!result.Succeeded)
                {
                    return false;
                }

                model.Remove(name);
            }

            var now = await WithTagsAsync(tags => tags.GetPageTagsAsync(page));
            if (!now.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(model))
            {
                return false;
            }
        }

        // No two tags of the workspace share a lower-case name, whatever ran.
        await using var db = CreateContext();
        var names = await db.Tags.AsNoTracking().Where(t => t.WorkspaceId == WorkspaceId).Select(t => t.NormalizedName).ToListAsync();
        return names.Count == names.Distinct().Count();
    }
}
