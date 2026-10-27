using System.Globalization;
using System.Text;

namespace Inkpact.Api.Import;

internal enum StepKind
{
    Notebook,
    Note,
}

/// <summary>One page to make: a notebook or a note, and the notebook it goes under (null for the top level).</summary>
internal sealed record ImportStep(StepKind Kind, string Id, string? ParentId);

internal enum SkipReason
{
    Encrypted,
    Trashed,
}

/// <summary>What a JEX file turns into, decided before anything is written: which items, under what, in which order.</summary>
internal sealed class ImportPlan
{
    /// <summary>Parents before their children; notebooks before notes among siblings, each by title.</summary>
    public List<ImportStep> Steps { get; } = [];

    public List<(StepKind Kind, string Id, string Title, SkipReason Reason)> Skipped { get; } = [];

    /// <summary>Notebooks that were their own ancestor; each was put at the top level instead.</summary>
    public List<string> BrokenCycles { get; } = [];

    /// <summary>Notes whose notebook is not in the file, or is not imported; they go at the top level.</summary>
    public List<string> NotesWithoutNotebook { get; } = [];

    /// <summary>The steps under a notebook (null for the top level), in order.</summary>
    public ILookup<string?, ImportStep> Children => _children ??= Steps.ToLookup(step => step.ParentId, StringComparer.Ordinal);

    private ILookup<string?, ImportStep>? _children;

    public static ImportPlan Build(JexIndex index)
    {
        var plan = new ImportPlan();

        var notebooks = new Dictionary<string, JexFolder>(StringComparer.Ordinal);
        foreach (var folder in index.Folders.Values)
        {
            if (folder.Encrypted)
            {
                plan.Skipped.Add((StepKind.Notebook, folder.Id, folder.Title, SkipReason.Encrypted));
            }
            else if (folder.Trashed)
            {
                plan.Skipped.Add((StepKind.Notebook, folder.Id, folder.Title, SkipReason.Trashed));
            }
            else
            {
                notebooks[folder.Id] = folder;
            }
        }

        var parentOf = ResolveParents(notebooks, plan.BrokenCycles);

        var notes = new List<JexNote>();
        foreach (var note in index.Notes.Values)
        {
            if (note.Encrypted)
            {
                plan.Skipped.Add((StepKind.Note, note.Id, note.Title, SkipReason.Encrypted));
            }
            else if (note.Trashed)
            {
                plan.Skipped.Add((StepKind.Note, note.Id, note.Title, SkipReason.Trashed));
            }
            else
            {
                notes.Add(note);
            }
        }

        var notebooksUnder = notebooks.Values.ToLookup(folder => parentOf[folder.Id], StringComparer.Ordinal);
        var notesUnder = notes.ToLookup(
            note =>
            {
                if (notebooks.ContainsKey(note.ParentId))
                {
                    return note.ParentId;
                }

                plan.NotesWithoutNotebook.Add(note.Id);
                return null;
            },
            StringComparer.Ordinal);

        // Depth first, without recursion: a file can nest notebooks as deep as it likes. A stack pops the last pushed, so
        // the children are pushed in reverse order, and what follows a notebook's own page is its notebooks, then its notes.
        var work = new Stack<(StepKind Kind, string Id, string? ParentId)>();
        void PushChildren(string? parent)
        {
            foreach (var note in notesUnder[parent].OrderBy(n => n, NoteOrder.Instance).Reverse())
            {
                work.Push((StepKind.Note, note.Id, parent));
            }

            foreach (var notebook in notebooksUnder[parent].OrderBy(f => f, NotebookOrder.Instance).Reverse())
            {
                work.Push((StepKind.Notebook, notebook.Id, parent));
            }
        }

        PushChildren(null);
        while (work.Count > 0)
        {
            var (kind, id, parent) = work.Pop();
            plan.Steps.Add(new ImportStep(kind, id, parent));
            if (kind == StepKind.Notebook)
            {
                PushChildren(id);
            }
        }

        plan.NotesWithoutNotebook.Sort(StringComparer.Ordinal);
        return plan;
    }

    /// <summary>
    /// For each notebook the notebook it goes under, or null for the top level: its parent when that is a notebook to
    /// import, else the top level. A notebook that is its own ancestor goes to the top level, in place of the one in the
    /// loop with the lowest id, so the same file always gives the same tree.
    /// </summary>
    private static Dictionary<string, string?> ResolveParents(Dictionary<string, JexFolder> notebooks, List<string> brokenCycles)
    {
        var parentOf = notebooks.Values.ToDictionary(
            folder => folder.Id,
            folder => notebooks.ContainsKey(folder.ParentId) && folder.ParentId != folder.Id ? folder.ParentId : null,
            StringComparer.Ordinal);

        // Every notebook has one parent at most, so a walk up either ends or loops. 0 is not visited, 1 on this walk, 2 done.
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var start in notebooks.Keys.Order(StringComparer.Ordinal))
        {
            var path = new List<string>();
            var current = start;
            while (current is not null && state.GetValueOrDefault(current) == 0)
            {
                state[current] = 1;
                path.Add(current);
                current = parentOf[current];
            }

            if (current is not null && state[current] == 1)
            {
                var loop = path.Skip(path.IndexOf(current)).ToList();
                var top = loop.Min(StringComparer.Ordinal)!;
                parentOf[top] = null;
                brokenCycles.Add(top);
            }

            foreach (var id in path)
            {
                state[id] = 2;
            }
        }

        brokenCycles.Sort(StringComparer.Ordinal);
        return parentOf;
    }

    /// <summary>
    /// A title as it sorts: without case or accents, as <c>Intl</c> would put it, and without depending on the system's
    /// culture data (the image may have none).
    /// </summary>
    public static string TitleKey(string title)
    {
        var lower = title.Trim().ToLowerInvariant();
        string decomposed;
        try
        {
            decomposed = lower.Normalize(NormalizationForm.FormD);
        }
        catch (Exception e) when (e is PlatformNotSupportedException or ArgumentException)
        {
            return lower;
        }

        var key = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                key.Append(c);
            }
        }

        return key.ToString();
    }

    private static int CompareTitles(string titleA, string idA, string titleB, string idB)
    {
        var byKey = string.CompareOrdinal(TitleKey(titleA), TitleKey(titleB));
        if (byKey != 0)
        {
            return byKey;
        }

        var byTitle = string.CompareOrdinal(titleA, titleB);
        return byTitle != 0 ? byTitle : string.CompareOrdinal(idA, idB);
    }

    private sealed class NotebookOrder : IComparer<JexFolder>
    {
        public static readonly NotebookOrder Instance = new();

        public int Compare(JexFolder? a, JexFolder? b) => CompareTitles(a!.Title, a.Id, b!.Title, b.Id);
    }

    private sealed class NoteOrder : IComparer<JexNote>
    {
        public static readonly NoteOrder Instance = new();

        public int Compare(JexNote? a, JexNote? b) => CompareTitles(a!.Title, a.Id, b!.Title, b.Id);
    }
}
