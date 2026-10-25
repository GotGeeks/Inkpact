using FsCheck;
using FsCheck.Fluent;
using Inkpact.Api.Pages;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Inkpact.Api.Tests.Pages;

/// <summary>
/// Random sequences of tree operations, applied to the real service (on Postgres) and to a plain in-memory model of
/// the rules in "Tree and metadata conflicts". After every step the two must agree on the answer, the tree, the trash
/// and the versions; and the tree must be sound: no cycles, no page lost, sibling keys in strict order, and no row
/// changed except the page the operation was about.
///
/// A failing sequence is shrunk to a short one and FsCheck prints the seed, so it can be replayed. Set
/// INKPACT_PROPERTY_RUNS for a longer run (the default is 100 sequences).
/// </summary>
public class TreeModelTests(PostgresFixture postgres, ITestOutputHelper output) : TreeTestBase(postgres)
{
    // How often each kind of operation ended each way, over all sequences. A generator that never produced a
    // refusal, or a restore into the top level, would make this test pass without proving anything.
    private readonly Dictionary<string, int> _outcomes = [];

    protected override bool PooledConnections => true;

    private static int Runs =>
        int.TryParse(Environment.GetEnvironmentVariable("INKPACT_PROPERTY_RUNS"), out var runs) && runs > 0 ? runs : 100;

    [Fact]
    public void The_tree_always_matches_the_model_and_stays_sound()
    {
        var config = Config.QuickThrowOnFailure.WithMaxTest(Runs).WithEndSize(200).WithQuietOnSuccess(true);

        Check.One(config, Prop.ForAll(Sequences(), numbers => RunSequenceAsync(numbers).GetAwaiter().GetResult()));

        output.WriteLine(string.Join(Environment.NewLine, _outcomes.OrderBy(o => o.Key).Select(o => $"{o.Key,-40} {o.Value}")));

        // Robust floors, not exact numbers: a generator that stopped producing a kind of operation, or never got a
        // restore or a move through, would make this test pass without proving anything. The rare outcomes are
        // covered by the scripted sequences below.
        int Total(string kind) => _outcomes.Where(o => o.Key.StartsWith(kind + " ", StringComparison.Ordinal)).Sum(o => o.Value);
        foreach (var kind in new[] { "create", "rename", "move", "trash", "restore" })
        {
            Total(kind).ShouldBeGreaterThan(40, $"too few {kind} operations were generated");
        }

        _outcomes.GetValueOrDefault("create ok").ShouldBeGreaterThan(40);
        _outcomes.GetValueOrDefault("move ok").ShouldBeGreaterThan(5);
        _outcomes.GetValueOrDefault("trash ok").ShouldBeGreaterThan(20);
        _outcomes.GetValueOrDefault("restore ok original").ShouldBeGreaterThan(10);
        _outcomes.Where(o => !o.Key.Contains(" ok") && !o.Key.EndsWith("noop")).Sum(o => o.Value).ShouldBeGreaterThan(100, "too few refusals");
    }

    // ---- scripted sequences: the rare outcomes, every time ------------------------------------------------------------

    private static Func<TreeModel, Operation> Create(int? parent = null, int? after = null, int? before = null) =>
        model => model.Create(parent, after, before);

    private static Func<TreeModel, Operation> Rename(int page) => model => model.Simple(OperationKind.Rename, page);

    private static Func<TreeModel, Operation> Move(int page, int? parent, int? after = null, int? before = null) =>
        model => model.Move(page, parent, after, before);

    private static Func<TreeModel, Operation> Trash(int page) => model => model.Simple(OperationKind.Trash, page);

    private static Func<TreeModel, Operation> Restore(int page) => model => model.Simple(OperationKind.Restore, page);

    private async Task ScriptAsync(string expectedOutcome, params Func<TreeModel, Operation>[] steps)
    {
        await RunStepsAsync(steps);
        _outcomes.GetValueOrDefault(expectedOutcome).ShouldBeGreaterThan(0, $"the script did not produce {expectedOutcome}");
    }

    [Fact]
    public Task A_restore_into_a_parent_that_is_in_the_trash_goes_to_the_top_level() =>
        ScriptAsync("restore ok root", Create(), Create(parent: 1), Create(), Trash(2), Trash(1), Restore(2), Restore(1));

    [Fact]
    public Task A_page_hidden_by_its_parent_must_have_the_parent_restored_first() =>
        ScriptAsync("restore restore_the_parent", Create(), Create(parent: 1), Trash(1), Restore(2), Restore(1), Restore(2));

    [Fact]
    public Task Nothing_can_go_under_a_page_in_the_trash() =>
        ScriptAsync("move parent_in_trash", Create(), Create(), Trash(1), Create(parent: 1), Move(2, parent: 1), Restore(1), Move(2, parent: 1));

    [Fact]
    public Task A_page_cannot_be_moved_below_itself() =>
        ScriptAsync("move move_cycle", Create(), Create(parent: 1), Create(parent: 2), Move(1, parent: 3), Move(1, parent: 1), Move(2, parent: 3), Move(3, parent: 1));

    [Fact]
    public Task A_page_in_the_trash_cannot_be_renamed_or_moved() =>
        ScriptAsync("move page_in_trash", Create(), Create(), Trash(1), Rename(1), Move(1, parent: null, after: 2), Restore(1), Rename(1));

    [Fact]
    public Task Neighbours_that_are_not_siblings_or_not_adjacent_are_refused() =>
        ScriptAsync(
            "create position_invalid",
            Create(), Create(), Create(), Create(parent: 1), Create(after: 1, before: 3), Create(after: 4), Create(before: 4), Create(after: 1, before: 1),
            Move(3, parent: null, after: 3), Move(3, parent: 1, after: 4), Move(3, parent: 1, after: 4, before: 2));

    [Fact]
    public Task Trashing_and_restoring_in_every_order_keeps_the_tree_sound() =>
        ScriptAsync(
            "restore ok original",
            Create(), Create(parent: 1), Create(parent: 1, after: 2), Create(parent: 2), Trash(4), Trash(2), Trash(1), Restore(1), Restore(2), Restore(4), Trash(3),
            Trash(1), Trash(1), Restore(3), Restore(1), Restore(3), Restore(3), Move(4, parent: null), Move(3, parent: 4), Move(1, parent: 3), Move(4, parent: 1));

    /// <summary>
    /// Sequences of operations as numbers, five to an operation, each number spread evenly. The numbers that FsCheck makes
    /// for an int[] stay small at first, which made nearly every operation a create. Shrinks by dropping operations.
    /// </summary>
    private static Arbitrary<int[]> Sequences()
    {
        var operation = Gen.ArrayOf(Gen.Choose(0, 9999), 5);
        var sequence = Gen.Sized(size => Gen.Choose(1, Math.Clamp(size / 4 + 1, 1, 40))
            .SelectMany(count => Gen.ListOf(operation, count))
            .Select(operations => operations.SelectMany(o => o).ToArray()));

        IEnumerable<int[]> Shrink(int[] numbers)
        {
            // Only whole operations are dropped: first each half, then one at a time. Shrinking every number as well
            // multiplies the candidates, and each one is a run against the database.
            var operations = numbers.Length / 5;
            if (operations > 3)
            {
                yield return numbers.Take(operations / 2 * 5).ToArray();
                yield return numbers.Skip(operations / 2 * 5).ToArray();
            }

            for (var chunk = 0; chunk < operations; chunk++)
            {
                yield return numbers.Take(chunk * 5).Concat(numbers.Skip(chunk * 5 + 5)).ToArray();
            }
        }

        return Arb.From(sequence, Shrink);
    }

    private void Count(Operation operation, TreeResult<PageChange> actual, TreeModel model)
    {
        var kind = operation.Kind.ToString().ToLowerInvariant();
        var outcome = actual.Error is { } error
            ? $"{kind} {error.Code}"
            : operation.Kind == OperationKind.Restore && actual.Value!.RestoredTo is { } to ? $"{kind} ok {to}"
            : (operation.Kind is OperationKind.Trash or OperationKind.Restore) && !model.ChangedLastOperation ? $"{kind} noop"
            : $"{kind} ok";
        _outcomes[outcome] = _outcomes.GetValueOrDefault(outcome) + 1;
    }

    // ---- one sequence ------------------------------------------------------------------------------------------------

    private Task<bool> RunSequenceAsync(int[] numbers)
    {
        const int perOperation = 5;
        var steps = Enumerable.Range(0, numbers.Length / perOperation)
            .Select(chunk => (Func<TreeModel, Operation>)(model => model.Decode(numbers.AsSpan(chunk * perOperation, perOperation))));
        return RunStepsAsync(steps);
    }

    /// <summary>Runs the operations one by one against the service and the model, checking everything after each.</summary>
    private async Task<bool> RunStepsAsync(IEnumerable<Func<TreeModel, Operation>> steps)
    {
        // A workspace of its own for every sequence, in the database of the test.
        var (_, workspaceId) = await AddWorkspaceAsync($"{Guid.NewGuid():N}@example.com");
        var model = new TreeModel();
        var rows = await RowsOfAsync(workspaceId);
        var step = 0;

        foreach (var next in steps)
        {
            var operation = next(model);
            var before = rows;

            var actual = await ApplyAsync(workspaceId, operation);
            var expected = model.Apply(operation, actual);
            Count(operation, actual, model);

            var context = $"step {++step}: {operation}";
            AssertSameAnswer(expected, actual, context);
            rows = await RowsOfAsync(workspaceId);
            await AssertConsistentAsync(workspaceId, model, before, rows, operation, actual, context);
        }

        return true;
    }

    private async Task<Dictionary<Guid, Page>> RowsOfAsync(Guid workspaceId)
    {
        await using var db = CreateContext();
        return await db.Pages.AsNoTracking().Where(p => p.WorkspaceId == workspaceId).ToDictionaryAsync(p => p.Id);
    }

    private Task<TreeResult<PageChange>> ApplyAsync(Guid workspaceId, Operation operation) => operation.Kind switch
    {
        OperationKind.Create => WithTree(tree => tree.CreateAsync(workspaceId, operation.Title, operation.Position)),
        OperationKind.Rename => WithTree(tree => tree.RenameAsync(operation.Page, operation.Title, null)),
        OperationKind.Move => WithTree(tree => tree.MoveAsync(operation.Page, operation.Position, null)),
        OperationKind.Trash => WithTree(tree => tree.TrashAsync(operation.Page, null)),
        OperationKind.Restore => WithTree(tree => tree.RestoreAsync(operation.Page, null)),
        _ => throw new InvalidOperationException(),
    };

    private static void AssertSameAnswer(string? expectedError, TreeResult<PageChange> actual, string context)
    {
        (actual.Error?.Code).ShouldBe(expectedError, context);
    }

    private async Task AssertConsistentAsync(
        Guid workspaceId,
        TreeModel model,
        Dictionary<Guid, Page> before,
        Dictionary<Guid, Page> after,
        Operation operation,
        TreeResult<PageChange> actual,
        string context)
    {
        // The tree the service shows is the tree the model has: same pages, same parents, same order, same versions.
        var tree = await WithTree(service => service.GetTreeAsync(workspaceId));
        if (actual.Succeeded && operation.Kind == OperationKind.Restore)
        {
            actual.Value!.RestoredTo.ShouldBe(model.LastRestoredTo, context + ": restored to the wrong place");
            if (model.WasRestoredInPlace(operation.Page))
            {
                // Where a restored page lands among its siblings depends on its old key, which the model does not have.
                // So the model takes the place the service chose, after checking the siblings kept their order.
                model.AdoptPlace(operation.Page, tree.Pages.Where(p => p.ParentId == model.ParentOf(operation.Page)).Select(p => p.Id).ToList(), context);
            }
        }

        tree.Pages.Select(p => (p.Id, p.ParentId, p.Version)).ShouldBe(model.VisibleTree(), context + ": the tree differs from the model");

        // Sibling keys strictly increase, as bytes: nothing is tied, nothing is out of order.
        foreach (var siblings in tree.Pages.GroupBy(p => p.ParentId))
        {
            var keys = siblings.Select(p => p.SortKey).ToList();
            keys.Zip(keys.Skip(1)).ShouldAllBe(pair => string.CompareOrdinal(pair.First, pair.Second) < 0, context + ": sibling keys are not in strict order");
            keys.ShouldAllBe(key => FractionalIndex.IsValid(key), context + ": a key is not valid");
        }

        // Every page is in the tree, or hidden by a page in the trash: none is lost, and none is in both places.
        var visible = tree.Pages.Select(p => p.Id).ToHashSet();
        foreach (var id in after.Keys)
        {
            var inTrash = after[id].DeletedAt is not null || model.IsHidden(id);
            visible.Contains(id).ShouldBe(!inTrash, $"{context}: page {model.Name(id)} is in the tree: {visible.Contains(id)}, in the trash: {inTrash}");
        }

        // What the trash shows are exactly the tops of what was trashed.
        var trash = (await WithTree(service => service.GetTrashAsync(workspaceId))).Select(p => p.Id).ToHashSet();
        trash.ShouldBe(model.TrashTops().ToHashSet(), context + ": the trash differs from the model");

        // Nothing changed but the page the operation was about, and nothing at all when it was refused or did nothing.
        var changed = after.Values.Where(page => !before.TryGetValue(page.Id, out var old) || Differs(old, page)).Select(p => p.Id).ToHashSet();
        var allowed = actual.Succeeded && actual.Value!.Page.Version != (before.TryGetValue(actual.Value.Page.Id, out var previous) ? previous.Version : 0)
            ? new HashSet<Guid> { actual.Value.Page.Id }
            : [];
        changed.ShouldBe(allowed, context + ": rows changed that the operation should not have touched");
        before.Keys.ShouldAllBe(id => after.ContainsKey(id), context + ": a page was deleted");
    }

    private static bool Differs(Page x, Page y) =>
        x.ParentId != y.ParentId || x.SortKey != y.SortKey || x.Title != y.Title || x.Version != y.Version
        || x.DeletedAt != y.DeletedAt || x.UpdatedAt != y.UpdatedAt || x.WorkspaceId != y.WorkspaceId;

    // ---- the model ---------------------------------------------------------------------------------------------------

    private enum OperationKind
    {
        Create,
        Rename,
        Move,
        Trash,
        Restore,
    }

    private sealed record Operation(OperationKind Kind, Guid Page, string Title, Position Position)
    {
        public override string ToString() =>
            Kind == OperationKind.Create
                ? $"create \"{Title}\" under {Short(Position.ParentId)} after {Short(Position.AfterId)} before {Short(Position.BeforeId)}"
                : Kind == OperationKind.Move
                    ? $"move {Short(Page)} under {Short(Position.ParentId)} after {Short(Position.AfterId)} before {Short(Position.BeforeId)}"
                    : $"{Kind.ToString().ToLowerInvariant()} {Short(Page)}";

        private static string Short(Guid? id) => id is null ? "-" : id.Value.ToString()[^4..];
    }

    private sealed class TreeModel
    {
        private sealed class Node(Guid id)
        {
            public Guid Id { get; } = id;

            public Guid? Parent { get; set; }

            public bool Trashed { get; set; }

            public int Version { get; set; } = 1;
        }

        private readonly Dictionary<Guid, Node> _nodes = [];
        private readonly List<Guid> _created = [];

        // Ordered children that are not themselves in the trash, per parent; the top level is keyed by Guid.Empty.
        private readonly Dictionary<Guid, List<Guid>> _live = new() { [Guid.Empty] = [] };
        private readonly Dictionary<Guid, int> _numbers = [];

        private Guid? IdOf(int? number) => number is null ? null : _created[number.Value - 1];

        /// <summary>A create, naming the pages by the order they were created in (1 is the first).</summary>
        public Operation Create(int? parent, int? after, int? before) =>
            new(OperationKind.Create, Guid.Empty, $"page {_created.Count + 1}", new Position(IdOf(parent), IdOf(after), IdOf(before)));

        public Operation Move(int page, int? parent, int? after, int? before) =>
            new(OperationKind.Move, _created[page - 1], "", new Position(IdOf(parent), IdOf(after), IdOf(before)));

        public Operation Simple(OperationKind kind, int page) =>
            new(kind, _created[page - 1], $"renamed {page}", new Position(null, null, null));

        public string Name(Guid id) => _numbers.TryGetValue(id, out var number) ? $"#{number}" : id.ToString();

        public Guid? ParentOf(Guid id) => _nodes[id].Parent;

        /// <summary>
        /// Turns five numbers into an operation on pages the model knows. The first operation is always a create.
        /// Guided by what the model knows: most of the time a restore picks a page that is in the trash, a move picks a page
        /// that can be moved, and neighbours are real siblings, so the interesting cases (a restore into a parent that is
        /// gone, a move that would make a cycle) happen often. The rest of the time anything goes, so refusals happen too.
        /// </summary>
        public Operation Decode(ReadOnlySpan<int> n)
        {
            var kind = _created.Count == 0 ? OperationKind.Create : ((uint)n[0] % 20) switch
            {
                < 5 => OperationKind.Create,
                < 7 => OperationKind.Rename,
                < 12 => OperationKind.Move,
                < 16 => OperationKind.Trash,
                _ => OperationKind.Restore,
            };
            if (_created.Count == 0)
            {
                return new Operation(kind, Guid.Empty, "page 1", new Position(null, null, null));
            }

            var visible = _created.Where(id => !IsHidden(id)).ToList();
            var pageNumber = (uint)n[0] / 20;
            Guid page;
            switch (kind)
            {
                case OperationKind.Restore:
                    var trashed = _created.Where(id => _nodes[id].Trashed).ToList();
                    page = pageNumber % 10 < 7 && trashed.Count > 0 ? Choose(trashed, pageNumber / 10) : Choose(_created, pageNumber / 10);
                    break;
                case OperationKind.Trash:
                    // Often a parent of something already in the trash, so that child can be restored into a parent that is gone.
                    var parentsOfTrashed = visible.Where(id => _nodes.Values.Any(c => c.Trashed && c.Parent == id)).ToList();
                    page = pageNumber % 10 < 4 && parentsOfTrashed.Count > 0 ? Choose(parentsOfTrashed, pageNumber / 10)
                        : pageNumber % 10 < 8 && visible.Count > 0 ? Choose(visible, pageNumber / 10)
                        : Choose(_created, pageNumber / 10);
                    break;
                default:
                    page = pageNumber % 10 < 7 && visible.Count > 0 ? Choose(visible, pageNumber / 10) : Choose(_created, pageNumber / 10);
                    break;
            }

            Guid? parent = null;
            if (kind is OperationKind.Create or OperationKind.Move)
            {
                var asked = (uint)n[4];
                var pool = asked % 10 < 7 && visible.Count > 0 ? visible : _created;
                parent = asked % 5 == 0 ? null : Choose(pool, asked / 10);
            }

            // Neighbours: mostly real siblings of the parent, sometimes any page at all.
            var siblings = _live.TryGetValue(parent ?? Guid.Empty, out var live) ? live.Where(id => id != page).ToList() : [];
            Guid? Neighbour(int number) =>
                (uint)number % 10 < 8 && siblings.Count > 0 ? Choose(siblings, (uint)number / 10) : Choose(_created, (uint)number / 10);
            var x = Neighbour(n[2]);
            var y = Neighbour(n[3]);
            var position = ((uint)n[1] % 5) switch
            {
                1 => new Position(parent, x, null),
                2 => new Position(parent, null, x),
                3 => new Position(parent, x, NextLiveAfter(parent, x, moved: kind == OperationKind.Move ? page : null)),
                4 => new Position(parent, x, y),
                _ => new Position(parent, null, null),
            };

            return new Operation(kind, page, $"page {_created.Count + 1}", position);
        }

        private static Guid Choose(List<Guid> pool, uint number) => pool[(int)(number % (uint)pool.Count)];

        private Guid? NextLiveAfter(Guid? parent, Guid? after, Guid? moved)
        {
            var siblings = _live[parent ?? Guid.Empty].Where(id => id != moved).ToList();
            var index = after is null ? -1 : siblings.IndexOf(after.Value);
            return index >= 0 && index + 1 < siblings.Count ? siblings[index + 1] : null;
        }

        /// <summary>Applies the operation as the rules say. Returns the error code the service must give, or null for success.</summary>
        public string? Apply(Operation operation, TreeResult<PageChange> actual)
        {
            ChangedLastOperation = true;
            switch (operation.Kind)
            {
                case OperationKind.Create:
                    {
                        if (Place(operation.Position, moved: null, out var index) is { } code)
                        {
                            return code;
                        }

                        if (!actual.Succeeded)
                        {
                            return null; // the service refused something the model allows; the comparison reports it
                        }

                        var id = actual.Value!.Page.Id;
                        var node = new Node(id) { Parent = operation.Position.ParentId };
                        _nodes[id] = node;
                        _created.Add(id);
                        _numbers[id] = _created.Count;
                        _live[id] = [];
                        _live[operation.Position.ParentId ?? Guid.Empty].Insert(index, id);
                        return null;
                    }

                case OperationKind.Rename:
                    {
                        var node = _nodes[operation.Page];
                        if (IsHidden(node.Id))
                        {
                            return "page_in_trash";
                        }

                        node.Version++;
                        return null;
                    }

                case OperationKind.Move:
                    {
                        var node = _nodes[operation.Page];
                        if (IsHidden(node.Id))
                        {
                            return "page_in_trash";
                        }

                        var parent = operation.Position.ParentId;
                        if (parent is { } p && IsHidden(p))
                        {
                            return "parent_in_trash";
                        }

                        if (parent is { } q && IsAncestorOrSelf(node.Id, q))
                        {
                            return "move_cycle";
                        }

                        if (Place(operation.Position, moved: node.Id, out var index) is { } code)
                        {
                            return code;
                        }

                        _live[node.Parent ?? Guid.Empty].Remove(node.Id);
                        node.Parent = parent;
                        _live[parent ?? Guid.Empty].Insert(index, node.Id);
                        node.Version++;
                        return null;
                    }

                case OperationKind.Trash:
                    {
                        var node = _nodes[operation.Page];
                        if (node.Trashed || IsHidden(node.Id))
                        {
                            ChangedLastOperation = false;
                            return null;
                        }

                        node.Trashed = true;
                        _live[node.Parent ?? Guid.Empty].Remove(node.Id);
                        node.Version++;
                        return null;
                    }

                case OperationKind.Restore:
                    {
                        var node = _nodes[operation.Page];
                        LastRestoredTo = null;
                        if (!node.Trashed)
                        {
                            ChangedLastOperation = false;
                            return IsHidden(node.Id) ? "restore_the_parent" : null;
                        }

                        node.Trashed = false;
                        node.Version++;
                        var usable = node.Parent is null || !IsHidden(node.Parent.Value);
                        if (usable)
                        {
                            // The place is the service's to choose (see AdoptPlace); for now at the end.
                            _live[node.Parent ?? Guid.Empty].Add(node.Id);
                            _restoredInPlace = node.Id;
                            LastRestoredTo = "original";
                        }
                        else
                        {
                            node.Parent = null;
                            _live[Guid.Empty].Add(node.Id);
                            _restoredInPlace = null;
                            LastRestoredTo = "root";
                        }

                        return null;
                    }

                default:
                    throw new InvalidOperationException();
            }
        }

        private Guid? _restoredInPlace;

        /// <summary>False when the last operation was accepted but did nothing.</summary>
        public bool ChangedLastOperation { get; private set; }

        /// <summary>Where the last restore put the page: "original", "root", or null when nothing was restored.</summary>
        public string? LastRestoredTo { get; private set; }

        public bool WasRestoredInPlace(Guid id) => _restoredInPlace == id;

        /// <summary>
        /// Takes the order the service gave the siblings of a restored page, after checking the others kept theirs.
        /// </summary>
        public void AdoptPlace(Guid restored, List<Guid> serviceOrder, string context)
        {
            var parent = _nodes[restored].Parent ?? Guid.Empty;
            var expectedOthers = _live[parent].Where(id => id != restored).ToList();
            serviceOrder.Where(id => id != restored).ToList().ShouldBe(expectedOthers, context + ": a restore changed the order of the other siblings");
            serviceOrder.ShouldContain(restored, context + ": the restored page is not among its siblings");
            _live[parent] = serviceOrder;
            _restoredInPlace = null;
        }

        /// <summary>The refusal the rules give for a place, or null with the index to insert at among the siblings.</summary>
        private string? Place(Position position, Guid? moved, out int index)
        {
            index = 0;
            var parent = position.ParentId;
            if (parent is { } p && !_nodes.ContainsKey(p))
            {
                return "parent_invalid";
            }

            if (parent is { } trashedParent && IsHidden(trashedParent))
            {
                return "parent_in_trash";
            }

            var (_, after, before) = position;
            var siblings = _live[parent ?? Guid.Empty].Where(id => id != moved).ToList();
            if ((after is not null && after == before) || (after is not null && after == moved) || (before is not null && before == moved))
            {
                return "position_invalid";
            }

            if (after is not null)
            {
                var at = siblings.IndexOf(after.Value);
                if (at < 0)
                {
                    return "position_invalid";
                }

                var next = at + 1 < siblings.Count ? siblings[at + 1] : (Guid?)null;
                if (before is not null && before != next)
                {
                    return "position_invalid";
                }

                index = at + 1;
            }
            else if (before is not null)
            {
                var at = siblings.IndexOf(before.Value);
                if (at < 0)
                {
                    return "position_invalid";
                }

                index = at;
            }
            else
            {
                index = siblings.Count;
            }

            return null;
        }

        // A page is hidden when it, or something above it, is in the trash.
        public bool IsHidden(Guid id)
        {
            for (Guid? current = id; current is not null; current = _nodes[current.Value].Parent)
            {
                if (_nodes[current.Value].Trashed)
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsAncestorOrSelf(Guid ancestor, Guid page)
        {
            for (Guid? current = page; current is not null; current = _nodes[current.Value].Parent)
            {
                if (current == ancestor)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The pages that can be reached from the top without passing the trash, in tree order.</summary>
        public List<(Guid Id, Guid? ParentId, int Version)> VisibleTree()
        {
            var result = new List<(Guid, Guid?, int)>();

            void Walk(Guid parent)
            {
                foreach (var id in _live[parent])
                {
                    result.Add((id, _nodes[id].Parent, _nodes[id].Version));
                    Walk(id);
                }
            }

            Walk(Guid.Empty);
            return result;
        }

        /// <summary>The pages in the trash with nothing above them in the trash.</summary>
        public IEnumerable<Guid> TrashTops() =>
            _nodes.Values.Where(n => n.Trashed && (n.Parent is null || !IsHidden(n.Parent.Value))).Select(n => n.Id);
    }
}
