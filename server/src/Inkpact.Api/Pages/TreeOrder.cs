namespace Inkpact.Api.Pages;

/// <summary>
/// The one order of a page tree, for the tree the client gets and for the export: parents before their children, siblings by
/// key compared as bytes (the way the database sorts them), and by id when two keys are equal. Walked without recursion, so a
/// deep tree does not exhaust the stack.
/// </summary>
internal static class TreeOrder
{
    /// <summary>
    /// <paramref name="pages"/> in tree order. A page whose parent is not among them cannot be reached from the top (none should
    /// exist): those come last, each with the pages below it, so nothing is lost.
    /// </summary>
    public static List<T> Of<T>(IEnumerable<T> pages, Func<T, Guid> id, Func<T, Guid?> parentId, Func<T, string> sortKey)
    {
        var all = pages.ToList();
        var ids = all.Select(id).ToHashSet();
        var byParent = all.ToLookup(parentId);
        var ordered = new List<T>(all.Count);

        IEnumerable<T> Sorted(IEnumerable<T> siblings) =>
            siblings.OrderBy(sortKey, StringComparer.Ordinal).ThenBy(id);

        void Walk(IEnumerable<T> tops)
        {
            var stack = new Stack<IEnumerator<T>>();
            stack.Push(tops.GetEnumerator());
            while (stack.Count > 0)
            {
                var siblings = stack.Peek();
                if (!siblings.MoveNext())
                {
                    siblings.Dispose();
                    stack.Pop();
                    continue;
                }

                ordered.Add(siblings.Current);
                stack.Push(Sorted(byParent[id(siblings.Current)]).GetEnumerator());
            }
        }

        Walk(Sorted(byParent[null]));
        Walk(Sorted(all.Where(p => parentId(p) is { } parent && !ids.Contains(parent))));
        return ordered;
    }
}
