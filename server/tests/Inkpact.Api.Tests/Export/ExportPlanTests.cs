using Inkpact.Api.Export;

namespace Inkpact.Api.Tests.Export;

public sealed class ExportPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ExportPage Page(Guid id, string title, Guid? parent = null, string key = "a0") =>
        new(id, parent, title, key, Now, Now, false, null, null, []);

    private static ExportFile File(string name, string hash, Guid? id = null) => new(id ?? Guid.NewGuid(), name, hash, 10, Now);

    private static Guid G(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static Dictionary<string, string> PathsByTitle(ExportPlan plan) => plan.Pages.ToDictionary(p => p.Page.Title, p => p.Path);

    [Fact]
    public void A_page_is_a_file_and_the_pages_under_it_are_in_a_folder_beside_it()
    {
        var plan = ExportPlan.Build(
            [Page(G(1), "Notes"), Page(G(2), "Ideas", G(1)), Page(G(3), "Deep", G(2)), Page(G(4), "Other", key: "b0")], []);

        PathsByTitle(plan).ShouldBe(new Dictionary<string, string>
        {
            ["Notes"] = "Notes.md",
            ["Ideas"] = "Notes/Ideas.md",
            ["Deep"] = "Notes/Ideas/Deep.md",
            ["Other"] = "Other.md",
        });
    }

    [Fact]
    public void Pages_come_in_tree_order_whatever_order_they_are_given_in()
    {
        var plan = ExportPlan.Build(
            [Page(G(4), "d", G(1), "a1"), Page(G(3), "c", key: "b0"), Page(G(2), "b", G(1), "a0"), Page(G(1), "a", key: "a0"), Page(G(5), "e", G(4), "a0")], []);

        plan.Pages.Select(p => p.Page.Title).ShouldBe(["a", "b", "d", "e", "c"]);
    }

    [Fact]
    public void Sibling_keys_are_compared_as_bytes_the_way_the_tree_orders_them()
    {
        // Ordinal: "B" (0x42) comes before "a" (0x61), which a culture-aware comparison would not do.
        var plan = ExportPlan.Build([Page(G(1), "lower", key: "a"), Page(G(2), "upper", key: "B")], []);

        plan.Pages.Select(p => p.Page.Title).ShouldBe(["upper", "lower"]);
    }

    [Fact]
    public void The_same_tree_gives_the_same_plan_every_time()
    {
        ExportPage[] pages = [Page(G(1), "Same"), Page(G(2), "Same"), Page(G(3), "Same")];

        var first = ExportPlan.Build(pages, []).Pages.Select(p => p.Path).ToArray();
        var second = ExportPlan.Build([.. pages.Reverse()], []).Pages.Select(p => p.Path).ToArray();

        first.ShouldBe(["Same.md", "Same (2).md", "Same (3).md"]);
        second.ShouldBe(first);
    }

    [Fact]
    public void An_empty_title_is_Untitled_and_two_of_them_are_numbered()
    {
        var plan = ExportPlan.Build([Page(G(1), ""), Page(G(2), "   ", key: "b0")], []);

        plan.Pages.Select(p => p.Path).ShouldBe(["Untitled.md", "Untitled (2).md"]);
    }

    [Fact]
    public void Siblings_in_different_folders_may_share_a_name()
    {
        var plan = ExportPlan.Build([Page(G(1), "A"), Page(G(2), "B", key: "b0"), Page(G(3), "Same", G(1)), Page(G(4), "Same", G(2))], []);

        plan.Pages.Select(p => p.Path).ShouldBe(["A.md", "A/Same.md", "B.md", "B/Same.md"]);
    }

    [Fact]
    public void A_title_with_a_slash_does_not_make_a_folder()
    {
        var plan = ExportPlan.Build([Page(G(1), "a/b/../c")], []);

        plan.Pages[0].Path.ShouldBe("a-b-..-c.md");
    }

    [Fact]
    public void The_README_and_the_attachments_folder_are_left_to_the_export()
    {
        var plan = ExportPlan.Build([Page(G(1), "README"), Page(G(2), "_attachments", key: "b0"), Page(G(3), "readme", key: "c0")], []);

        plan.Pages.Select(p => p.Path).ShouldBe(["README (2).md", "_attachments (2).md", "readme (3).md"]);
    }

    [Fact]
    public void A_page_whose_parent_is_not_in_the_export_is_put_at_the_top()
    {
        var plan = ExportPlan.Build([Page(G(1), "Orphan", G(99))], []);

        plan.Pages[0].Path.ShouldBe("Orphan.md");
    }

    [Fact]
    public void A_deep_tree_is_planned_without_running_out_of_stack()
    {
        var pages = new List<ExportPage> { Page(G(1), "n") };
        for (var i = 2; i <= 5_000; i++)
        {
            pages.Add(Page(G(i), "n", G(i - 1)));
        }

        var plan = ExportPlan.Build(pages, []);

        plan.Pages.Count.ShouldBe(5_000);
        plan.Pages[^1].Path.Count(c => c == '/').ShouldBe(4_999);
    }

    // ---- links -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("A.md", "B.md", "B.md")]
    [InlineData("A.md", "A/B.md", "A/B.md")]
    [InlineData("A/B.md", "A.md", "../A.md")]
    [InlineData("A/B.md", "A/C.md", "C.md")]
    [InlineData("A/B/C.md", "D.md", "../../D.md")]
    [InlineData("A/B/C.md", "A/D/E.md", "../D/E.md")]
    [InlineData("A.md", "_attachments/x.png", "_attachments/x.png")]
    [InlineData("A/B.md", "_attachments/x.png", "../_attachments/x.png")]
    [InlineData("A.md", "A.md", "A.md")]
    public void A_link_is_the_relative_way_between_two_files(string from, string to, string expected) =>
        ExportPlan.Relative(from, to).ShouldBe(expected);

    [Theory]
    [InlineData("My note (draft).md", "My%20note%20%28draft%29.md")]
    [InlineData("a[1].md", "a%5B1%5D.md")]
    [InlineData("100% #1?.md", "100%25%20%231%3F.md")]
    [InlineData("tom&jerry=1+1.md", "tom%26jerry%3D1%2B1.md")]
    [InlineData("quote'\".md", "quote%27%22.md")]
    [InlineData("a_b-c~d.md", "a_b-c~d.md")]
    [InlineData("één.md", "één.md")]
    [InlineData("a😀b.md", "a%F0%9F%98%80b.md")]
    public void A_name_in_a_link_has_every_character_a_reader_could_misread_encoded(string name, string expected) =>
        ExportPlan.Relative("x.md", name).ShouldBe(expected);

    [Fact]
    public void A_page_link_goes_to_a_page_of_the_export_and_to_nothing_else()
    {
        var plan = ExportPlan.Build([Page(G(1), "Notes"), Page(G(2), "Ideas", G(1))], []);

        plan.PathOfPage("Notes/Ideas.md", G(1)).ShouldBe("../Notes.md");
        plan.PathOfPage("Notes.md", G(2)).ShouldBe("Notes/Ideas.md");
        plan.PathOfPage("Notes.md", G(3)).ShouldBeNull();
    }

    // ---- files -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_file_is_given_its_name_in_the_attachments_folder_when_a_page_first_uses_it()
    {
        var picture = File("photo.png", "aa");
        var plan = ExportPlan.Build([], [picture]);

        plan.TakeNewFiles().ShouldBeEmpty();
        plan.UseFile(picture.Id).ShouldBe("_attachments/photo.png");
        plan.TakeNewFiles().Select(f => f.Path).ShouldBe(["_attachments/photo.png"]);
        plan.TakeNewFiles().ShouldBeEmpty();
    }

    [Fact]
    public void A_file_used_twice_is_one_copy_and_so_is_a_file_that_is_on_two_pages_as_two_rows()
    {
        var first = File("photo.png", "aa");
        var again = File("photo.png", "aa");
        var renamed = File("holiday.png", "aa");
        var plan = ExportPlan.Build([], [first, again, renamed]);

        plan.UseFile(first.Id).ShouldBe("_attachments/photo.png");
        plan.UseFile(first.Id).ShouldBe("_attachments/photo.png");
        plan.UseFile(again.Id).ShouldBe("_attachments/photo.png");
        // The same bytes under another name are still the same file: one copy, under the name it was first used with.
        plan.UseFile(renamed.Id).ShouldBe("_attachments/photo.png");
        plan.TakeNewFiles().Count.ShouldBe(1);
    }

    [Fact]
    public void Two_different_files_with_one_name_are_numbered_in_the_order_they_are_used()
    {
        var a = File("scan.pdf", "aa");
        var b = File("scan.pdf", "bb");
        var plan = ExportPlan.Build([], [a, b]);

        plan.UseFile(b.Id).ShouldBe("_attachments/scan.pdf");
        plan.UseFile(a.Id).ShouldBe("_attachments/scan (2).pdf");
    }

    [Fact]
    public void An_id_that_is_no_attachment_of_the_workspace_is_not_exported()
    {
        var plan = ExportPlan.Build([], [File("photo.png", "aa")]);

        plan.UseFile(Guid.NewGuid()).ShouldBeNull();
        plan.TakeNewFiles().ShouldBeEmpty();
    }
}
