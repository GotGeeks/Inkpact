using FsCheck.Xunit;
using Inkpact.Api.Import;

namespace Inkpact.Api.Tests.Import;

public sealed class ImportPlanTests
{
    private static string Id(string name) => JexBuilder.Id(name);

    private static JexFolder Folder(string name, string? parent = null, string? title = null, bool encrypted = false, bool trashed = false) =>
        new(Id(name), parent is null ? "" : Id(parent), title ?? name, encrypted, trashed, 0, 0);

    private static JexNote Note(string name, string? parent, string? title = null, bool encrypted = false, bool trashed = false) =>
        new(Id(name), parent is null ? "" : Id(parent), title ?? name, false, false, null, null, null, null, encrypted, false, trashed, 0, 0);

    private static JexIndex IndexOf(IEnumerable<JexFolder> folders, IEnumerable<JexNote> notes)
    {
        var index = new JexIndex("none");
        foreach (var folder in folders)
        {
            index.Folders[folder.Id] = folder;
        }

        foreach (var note in notes)
        {
            index.Notes[note.Id] = note;
        }

        return index;
    }

    private static string[] Names(ImportPlan plan, IEnumerable<string> known) =>
        [.. plan.Steps.Select(step => known.Single(name => Id(name) == step.Id))];

    [Fact]
    public void Parents_come_before_children_and_notebooks_before_notes()
    {
        var plan = ImportPlan.Build(IndexOf(
            [Folder("projects"), Folder("archive", "projects"), Folder("home")],
            [Note("plan", "projects"), Note("old", "archive"), Note("loose", "home")]));

        Names(plan, ["projects", "archive", "home", "plan", "old", "loose"]).ShouldBe(["home", "loose", "projects", "archive", "old", "plan"]);
        plan.Steps.Select(s => s.ParentId).ShouldBe([null, Id("home"), null, Id("projects"), Id("archive"), Id("projects")]);
    }

    [Fact]
    public void Siblings_are_sorted_by_title_without_case_or_accents()
    {
        var plan = ImportPlan.Build(IndexOf(
            [Folder("a", title: "Zebra"), Folder("b", title: "apple"), Folder("c", title: "Äpfel"), Folder("d", title: "éclair"), Folder("e", title: "Éclair")],
            []));

        plan.Steps.Select(s => s.Id).ShouldBe([Id("c"), Id("b"), Id("e"), Id("d"), Id("a")]);
    }

    [Fact]
    public void Notes_with_the_same_title_keep_one_order_whatever_the_order_of_the_file()
    {
        var one = ImportPlan.Build(IndexOf([Folder("f")], [Note("x", "f", "Same"), Note("y", "f", "Same"), Note("z", "f", "Same")]));
        var other = ImportPlan.Build(IndexOf([Folder("f")], [Note("z", "f", "Same"), Note("y", "f", "Same"), Note("x", "f", "Same")]));

        other.Steps.ShouldBe(one.Steps);
    }

    [Fact]
    public void A_notebook_whose_parent_is_not_in_the_file_is_at_the_top()
    {
        // What a single exported notebook looks like: its parent_id points at something that was not exported.
        var plan = ImportPlan.Build(IndexOf([Folder("sub", "gone")], [Note("n", "sub")]));

        plan.Steps.Select(s => s.ParentId).ShouldBe([null, Id("sub")]);
        plan.NotesWithoutNotebook.ShouldBeEmpty();
    }

    [Fact]
    public void A_note_without_a_notebook_is_at_the_top_and_is_remembered()
    {
        var plan = ImportPlan.Build(IndexOf([Folder("f")], [Note("orphan", "gone"), Note("nowhere", null)]));

        plan.Steps.Where(s => s.Kind == StepKind.Note).Select(s => s.ParentId).ShouldBe([null, null]);
        plan.NotesWithoutNotebook.Order().ShouldBe(new[] { Id("nowhere"), Id("orphan") }.Order());
    }

    [Fact]
    public void Notebooks_that_are_their_own_ancestors_are_cut_at_the_lowest_id_and_all_are_imported_once()
    {
        var plan = ImportPlan.Build(IndexOf([Folder("a", "c"), Folder("b", "a"), Folder("c", "b"), Folder("self", "self")], []));

        plan.Steps.Count.ShouldBe(4);
        plan.Steps.Select(s => s.Id).Distinct().Count().ShouldBe(4);
        var lowest = new[] { Id("a"), Id("b"), Id("c") }.Min(StringComparer.Ordinal)!;
        plan.BrokenCycles.ShouldBe([lowest]);
        plan.Steps.Single(s => s.Id == lowest).ParentId.ShouldBeNull();
    }

    [Fact]
    public void What_is_encrypted_or_in_the_trash_is_left_out_and_listed()
    {
        var plan = ImportPlan.Build(IndexOf(
            [Folder("live"), Folder("secret", encrypted: true), Folder("bin", trashed: true)],
            [Note("n", "live"), Note("cipher", "live", encrypted: true), Note("deleted", "live", trashed: true), Note("inbin", "bin")]));

        plan.Steps.Select(s => s.Id).ShouldBe([Id("live"), Id("n"), Id("inbin")]);
        plan.Skipped.Select(s => (s.Kind, s.Reason)).Order().ShouldBe(
            new[]
            {
                (StepKind.Notebook, SkipReason.Encrypted),
                (StepKind.Notebook, SkipReason.Trashed),
                (StepKind.Note, SkipReason.Encrypted),
                (StepKind.Note, SkipReason.Trashed),
            }.Order());
        // A note whose notebook is in the trash but that is not in it itself would otherwise vanish: it surfaces at the top.
        plan.NotesWithoutNotebook.ShouldBe([Id("inbin")]);
    }

    [Fact]
    public void Nesting_as_deep_as_a_file_can_make_it_does_not_use_the_stack()
    {
        var folders = Enumerable.Range(0, 200_000).Select(i => Folder($"f{i}", i == 0 ? null : $"f{i - 1}")).ToList();

        var plan = ImportPlan.Build(IndexOf(folders, [Note("leaf", "f199999")]));

        plan.Steps.Count.ShouldBe(200_001);
        plan.Steps[0].Id.ShouldBe(Id("f0"));
        plan.Steps[^1].Id.ShouldBe(Id("leaf"));
    }

    [Fact]
    public void The_children_of_a_notebook_are_found_by_its_id()
    {
        var plan = ImportPlan.Build(IndexOf([Folder("f"), Folder("g", "f")], [Note("n", "f"), Note("top", null)]));

        plan.Children[Id("f")].Select(s => s.Id).ShouldBe([Id("g"), Id("n")]);
        plan.Children[null].Select(s => s.Id).ShouldBe([Id("f"), Id("top")]);
    }

    [Property(MaxTest = 500)]
    public bool Whatever_the_notebooks_point_at_every_item_is_planned_once_under_a_parent_that_comes_first(int[] parents, int[] noteParents)
    {
        var folders = parents.Take(40).Select((p, i) => Folder($"f{i}", Pick(p, parents.Take(40).Count()))).ToList();
        var notes = noteParents.Take(40).Select((p, i) => Note($"n{i}", Pick(p, folders.Count))).ToList();

        var plan = ImportPlan.Build(IndexOf(folders, notes));

        var position = plan.Steps.Select((step, i) => (step.Id, i)).ToDictionary(pair => pair.Id, pair => pair.i);
        var onceEach = plan.Steps.Count == folders.Count + notes.Count && position.Count == plan.Steps.Count;
        var parentsFirst = plan.Steps.All(step => step.ParentId is null || (position.TryGetValue(step.ParentId, out var at) && at < position[step.Id]));
        return onceEach && parentsFirst;

        // A parent among the notebooks, or none.
        static string? Pick(int value, int count) => count == 0 || (uint)value % (uint)(count + 1) == count ? null : $"f{(uint)value % (uint)(count + 1)}";
    }
}
