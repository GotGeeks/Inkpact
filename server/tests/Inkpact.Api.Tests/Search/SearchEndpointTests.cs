using System.Net;
using System.Net.Http.Json;
using Inkpact.Api.Auth;
using Inkpact.Api.Search;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.Search;

/// <summary>Search over HTTP against the real database: what is found, in what order, and what never is.</summary>
[Collection(PostgresCollection.Name)]
public class SearchEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private InkpactFactory _factory = null!;
    private HttpClient _owner = null!;
    private Guid _workspaceId;
    private int _pages;

    public async Task InitializeAsync()
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync());
        _owner = await _factory.SignedInClientAsync();
        _workspaceId = await UseDbAsync(db => db.Workspaces.Select(w => w.Id).SingleAsync());
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    private async Task<Guid> AddPageAsync(
        string title, string text = "", string tags = "", Guid? parentId = null, bool trashed = false, Guid? workspaceId = null, DateTimeOffset? updated = null)
    {
        var page = new Page
        {
            WorkspaceId = workspaceId ?? _workspaceId,
            ParentId = parentId,
            Title = title,
            SortKey = $"a{_pages++:D5}",
            CreatedAt = Now,
            UpdatedAt = updated ?? Now,
            DeletedAt = trashed ? Now : null,
            ContentText = text,
            TagText = tags,
        };
        await UseDbAsync(async db =>
        {
            db.Pages.Add(page);
            await db.SaveChangesAsync();
            return 0;
        });
        return page.Id;
    }

    private async Task<SearchResponse> SearchAsync(string text, HttpClient? client = null, Guid? workspaceId = null)
    {
        var response = await (client ?? _owner).GetAsync($"/api/workspaces/{workspaceId ?? _workspaceId}/search?q={Uri.EscapeDataString(text)}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SearchResponse>())!;
    }

    private async Task<Guid[]> IdsAsync(string text) => [.. (await SearchAsync(text)).Results.Select(r => r.Id)];

    private static string Show(IEnumerable<SearchSegment> segments) =>
        string.Concat(segments.Select(s => s.Match ? $"[{s.Text}]" : s.Text));

    // ---- what matches ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("cafe")]
    [InlineData("CAFE")]
    [InlineData("café")]
    [InlineData("CAFÉ")]
    [InlineData("café")]
    public async Task Case_and_accents_do_not_matter(string typed)
    {
        var id = await AddPageAsync("Café de Paris");
        await AddPageAsync("Something else");

        (await IdsAsync(typed)).ShouldBe([id]);
    }

    [Fact]
    public async Task An_accent_in_the_text_is_found_without_one_in_the_search_and_the_other_way_round()
    {
        var plain = await AddPageAsync("Notes", text: "We met in the cafe on Monday.");
        var accented = await AddPageAsync("More notes", text: "Een bezoek aan het café.");

        (await IdsAsync("café")).ShouldBe([plain, accented], ignoreOrder: true);
    }

    [Fact]
    public async Task A_word_that_is_only_half_typed_finds_the_page()
    {
        var id = await AddPageAsync("Notes", text: "De vergadering begint om negen uur.");

        (await IdsAsync("vergad")).ShouldBe([id]);
        (await IdsAsync("v")).ShouldBe([id]);
        (await IdsAsync("vergaderingen")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_word_must_match_but_not_in_the_same_place()
    {
        var both = await AddPageAsync("Weekly plan", text: "Prepare the meeting.");
        await AddPageAsync("Weekly", text: "Nothing about it.");
        await AddPageAsync("Other", text: "A meeting.");

        (await IdsAsync("meet week")).ShouldBe([both]);
    }

    [Fact]
    public async Task English_and_Dutch_are_matched_by_what_is_typed_and_not_by_a_stem()
    {
        var english = await AddPageAsync("Notes", text: "running shoes");
        var dutch = await AddPageAsync("Notities", text: "boodschappen doen");

        (await IdsAsync("running")).ShouldBe([english]);
        (await IdsAsync("run")).ShouldBe([english]);
        (await IdsAsync("boodschap")).ShouldBe([dutch]);
        (await IdsAsync("boodschappen")).ShouldBe([dutch]);
    }

    [Fact]
    public async Task A_page_is_found_by_its_tags()
    {
        var id = await AddPageAsync("Plain title", tags: "architecture crdt");

        (await IdsAsync("crdt")).ShouldBe([id]);
        (await IdsAsync("archi")).ShouldBe([id]);
    }

    [Fact]
    public async Task A_renamed_page_is_found_by_its_new_title_at_once()
    {
        var id = await AddPageAsync("Old name");

        (await _owner.PatchJsonAsync($"/api/pages/{id}", new { title = "Brand new name" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await IdsAsync("brand")).ShouldBe([id]);
        (await IdsAsync("old")).ShouldBeEmpty();
    }

    // ---- the order -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_title_ranks_above_a_tag_which_ranks_above_the_text()
    {
        var inText = await AddPageAsync("Page one", text: "Notes about a sync problem.");
        var inTag = await AddPageAsync("Page two", tags: "sync");
        var inTitle = await AddPageAsync("Sync", text: "Nothing.");

        (await IdsAsync("sync")).ShouldBe([inTitle, inTag, inText]);
    }

    [Fact]
    public async Task Equal_matches_come_newest_edit_first()
    {
        var oldest = await AddPageAsync("Sync one", updated: Now.AddDays(-3));
        var newest = await AddPageAsync("Sync two", updated: Now);
        var middle = await AddPageAsync("Sync three", updated: Now.AddDays(-1));

        (await IdsAsync("sync")).ShouldBe([newest, middle, oldest]);
    }

    [Fact]
    public async Task At_most_thirty_pages_come_back()
    {
        for (var i = 0; i < SearchService.MaxResults + 5; i++)
        {
            await AddPageAsync($"Sync {i}");
        }

        (await SearchAsync("sync")).Results.Count.ShouldBe(SearchService.MaxResults);
    }

    // ---- what never is found ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_in_the_trash_and_the_pages_under_one_are_not_found()
    {
        var live = await AddPageAsync("Secret live");
        var trashed = await AddPageAsync("Secret trashed", trashed: true);
        var parent = await AddPageAsync("Secret parent", trashed: true);
        await AddPageAsync("Secret child", parentId: parent);
        var notebook = await AddPageAsync("Secret notebook");
        var inNotebook = await AddPageAsync("Secret inside", parentId: notebook);
        await AddPageAsync("Secret under trashed page", parentId: trashed);

        (await IdsAsync("secret")).ShouldBe([live, notebook, inNotebook], ignoreOrder: true);
    }

    [Fact]
    public async Task A_page_comes_back_when_it_is_restored_from_the_trash()
    {
        var parent = await AddPageAsync("Plan");
        var child = await AddPageAsync("Plan details", parentId: parent);
        (await _owner.PostJsonAsync($"/api/pages/{parent}/trash")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await IdsAsync("plan")).ShouldBeEmpty();

        (await _owner.PostJsonAsync($"/api/pages/{parent}/restore")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await IdsAsync("plan")).ShouldBe([parent, child], ignoreOrder: true);
    }

    [Fact]
    public async Task A_page_of_another_workspace_is_not_found()
    {
        var other = await UseDbAsync(async db =>
        {
            var owner = await db.Users.FirstAsync();
            var workspace = new Workspace { Name = "Other", OwnerId = owner.Id, CreatedAt = Now };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();
            return workspace.Id;
        });
        var mine = await AddPageAsync("Budget");
        await AddPageAsync("Budget of the other workspace", workspaceId: other);

        (await IdsAsync("budget")).ShouldBe([mine]);
    }

    [Fact]
    public async Task A_stranger_gets_not_found_and_learns_nothing()
    {
        await AddPageAsync("Secret title");
        var stranger = _factory.CreateClient();
        await UseDbAsync(async db =>
        {
            db.Users.Add(new User { Email = "stranger@example.com", DisplayName = "S", PasswordHash = new Argon2PasswordHasher().Hash(AuthTestExtensions.Password), CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
        (await stranger.LoginAsync("stranger@example.com")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var found = await stranger.GetAsync($"/api/workspaces/{_workspaceId}/search?q=secret");
        var nothing = await stranger.GetAsync($"/api/workspaces/{Guid.NewGuid()}/search?q=secret");

        found.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        nothing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await found.Content.ReadAsStringAsync()).ShouldBe(await nothing.Content.ReadAsStringAsync());
        (await found.Content.ReadAsStringAsync()).ShouldNotContain("Secret");
    }

    [Fact]
    public async Task A_viewer_can_search_and_someone_not_signed_in_cannot()
    {
        var id = await AddPageAsync("Shared notes");
        var viewer = _factory.CreateClient();
        await UseDbAsync(async db =>
        {
            var user = new User { Email = "viewer@example.com", DisplayName = "V", PasswordHash = new Argon2PasswordHasher().Hash(AuthTestExtensions.Password), CreatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = _workspaceId, UserId = user.Id, Role = WorkspaceRole.Viewer, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
        (await viewer.LoginAsync("viewer@example.com")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await SearchAsync("shared", viewer)).Results.Select(r => r.Id).ShouldBe([id]);
        var anonymous = await _factory.CreateClient().GetAsync($"/api/workspaces/{_workspaceId}/search?q=shared");
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---- what is typed ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("&|!():*")]
    [InlineData("😀")]
    public async Task A_search_without_words_is_an_empty_list_and_not_an_error(string text)
    {
        await AddPageAsync("Anything");

        (await SearchAsync(text)).Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_search_without_a_text_at_all_is_an_empty_list()
    {
        var response = await _owner.GetAsync($"/api/workspaces/{_workspaceId}/search");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SearchResponse>())!.Results.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("a & b")]
    [InlineData("a | !b")]
    [InlineData("(((")]
    [InlineData("foo:*bar")]
    [InlineData("'quoted' \"double\" \\back")]
    [InlineData("<-> <2>")]
    public async Task Query_syntax_is_text_to_search_for_and_never_an_error(string text)
    {
        await AddPageAsync("Plain page", text: "foo bar a b");

        var response = await _owner.GetAsync($"/api/workspaces/{_workspaceId}/search?q={Uri.EscapeDataString(text)}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task More_than_eight_words_and_a_very_long_word_are_answered()
    {
        await AddPageAsync("Notes");

        (await SearchAsync(string.Join(' ', Enumerable.Repeat("notes", 20)))).Results.Count.ShouldBe(1);
        (await SearchAsync(new string('x', 5000))).Results.ShouldBeEmpty();
    }

    // ---- what comes back -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_title_and_the_text_come_back_with_the_matches_marked()
    {
        await AddPageAsync("Weekly meeting", text: "We talked about the budget. Then the meeting ended.");

        var hit = (await SearchAsync("meet")).Results.Single();

        Show(hit.Title).ShouldBe("Weekly [meeting]");
        Show(hit.Snippet).ShouldContain("[meeting]");
        Show(hit.Snippet).ShouldContain("budget");
    }

    [Fact]
    public async Task A_match_is_marked_whatever_its_accents_and_case()
    {
        await AddPageAsync("CAFÉ menu");

        Show((await SearchAsync("cafe")).Results.Single().Title).ShouldBe("[CAFÉ] menu");
    }

    [Fact]
    public async Task A_page_that_matched_only_by_its_title_or_a_tag_has_no_snippet()
    {
        await AddPageAsync("Sync", text: "Nothing of interest here.");
        await AddPageAsync("Other", text: "Nothing.", tags: "sync");

        var results = (await SearchAsync("sync")).Results;

        results.Count.ShouldBe(2);
        results.ShouldAllBe(r => r.Snippet.Count == 0);
    }

    [Fact]
    public async Task A_page_without_a_title_comes_back_with_an_empty_title()
    {
        var id = await AddPageAsync("", text: "A page with only some text in it.");

        var hit = (await SearchAsync("text")).Results.Single();

        hit.Id.ShouldBe(id);
        hit.Title.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_long_text_is_cut_to_a_snippet_around_the_match()
    {
        var text = string.Concat(Enumerable.Repeat("filler words go here. ", 500)) + "the needle is here. " + string.Concat(Enumerable.Repeat("more filler. ", 500));
        await AddPageAsync("Haystack", text: text);

        var snippet = Show((await SearchAsync("needle")).Results.Single().Snippet);

        snippet.ShouldContain("[needle]");
        snippet.Length.ShouldBeLessThan(400);
    }

    [Fact]
    public async Task Control_characters_in_a_page_cannot_pass_for_marks()
    {
        await AddPageAsync("A \u0001fake\u0002 title \u0003", text: "Some \u0001fake\u0002 text with the word match in it \u0003 and more.");

        var hit = (await SearchAsync("match")).Results.Single();

        // Nothing but the match is marked, and what the page held of those characters is gone.
        Show(hit.Title).ShouldBe("A fake title ");
        hit.Snippet.Where(s => s.Match).Select(s => s.Text).ShouldBe(["match"]);
        hit.Snippet.ShouldAllBe(s => !s.Text.Contains('\u0001') && !s.Text.Contains('\u0002') && !s.Text.Contains('\u0003'));
        Show(hit.Snippet).ShouldContain("Some fake text with the word [match] in it");
    }

    [Fact]
    public async Task Only_the_start_of_a_very_long_text_is_searched()
    {
        var filler = string.Concat(Enumerable.Repeat("blah ", 50_000));
        var early = await AddPageAsync("Early", text: "earlybird " + filler);
        await AddPageAsync("Late", text: filler + "latebird");

        (await IdsAsync("earlybird")).ShouldBe([early]);
        (await IdsAsync("latebird")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_title_match_is_found_first_among_many_pages_that_match_in_their_text_only()
    {
        var old = await AddPageAsync("Meeting minutes", updated: Now.AddYears(-3));
        for (var i = 0; i < SearchService.TextCandidates + 50; i++)
        {
            await AddPageAsync($"Page {i}", text: "We had a meeting about it.", updated: Now.AddMinutes(-i));
        }

        var results = (await SearchAsync("meet")).Results;

        results.Count.ShouldBe(SearchService.MaxResults);
        results[0].Id.ShouldBe(old);
    }

    [Fact]
    public async Task A_match_beyond_the_start_of_a_long_text_is_found_but_has_no_snippet()
    {
        var text = string.Concat(Enumerable.Repeat("filler words go here. ", (SearchService.SnippetWindow / 20) + 200)) + "the needle is here.";
        var id = await AddPageAsync("Haystack", text: text);

        var hit = (await SearchAsync("needle")).Results.Single();

        hit.Id.ShouldBe(id);
        hit.Snippet.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_word_in_the_title_and_another_in_the_text_finds_the_page()
    {
        var id = await AddPageAsync("Weekly plan", text: "Prepare the meeting.");

        var hit = (await SearchAsync("week meet")).Results.Single();

        hit.Id.ShouldBe(id);
        Show(hit.Title).ShouldBe("[Weekly] plan");
        Show(hit.Snippet).ShouldContain("[meeting]");
    }
}
