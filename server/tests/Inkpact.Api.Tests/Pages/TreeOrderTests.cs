using Inkpact.Api.Pages;

namespace Inkpact.Api.Tests.Pages;

public sealed class TreeOrderTests
{
    private sealed record Node(Guid Id, Guid? ParentId, string Key);

    private static Guid G(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static List<int> Order(params Node[] nodes) =>
        TreeOrder.Of(nodes, n => n.Id, n => n.ParentId, n => n.Key).Select(n => n.Id.ToByteArray()[0] | (n.Id.ToByteArray()[1] << 8)).ToList();

    [Fact]
    public void Parents_come_before_their_children_and_siblings_go_by_key_as_bytes()
    {
        Order(new(G(3), null, "b"), new(G(2), G(1), "a1"), new(G(1), null, "a"), new(G(4), G(1), "B"))
            .ShouldBe([1, 4, 2, 3]);
    }

    [Fact]
    public void Two_siblings_with_one_key_go_by_id_so_the_order_never_depends_on_the_input()
    {
        Order(new(G(2), null, "a"), new(G(1), null, "a")).ShouldBe([1, 2]);
        Order(new(G(1), null, "a"), new(G(2), null, "a")).ShouldBe([1, 2]);
    }

    [Fact]
    public void A_page_whose_parent_is_missing_comes_last_with_the_pages_below_it()
    {
        Order(new(G(1), null, "a"), new(G(2), G(99), "a"), new(G(3), G(2), "a")).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void A_deep_tree_does_not_exhaust_the_stack()
    {
        var nodes = Enumerable.Range(1, 50_000).Select(i => new Node(G(i), i == 1 ? null : G(i - 1), "a")).ToArray();

        TreeOrder.Of(nodes, n => n.Id, n => n.ParentId, n => n.Key).Count.ShouldBe(50_000);
    }
}
