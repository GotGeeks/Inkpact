namespace Spike.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class UpdateStoreTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Updates_load_in_append_order_per_page()
    {
        var page = PostgresFixture.NewPageId();
        var other = PostgresFixture.NewPageId();

        await fixture.Store.AppendAsync(page, [1], CancellationToken.None);
        await fixture.Store.AppendAsync(other, [99], CancellationToken.None);
        await fixture.Store.AppendAsync(page, [2, 2], CancellationToken.None);
        await fixture.Store.AppendAsync(page, [3], CancellationToken.None);

        Assert.Equal([[1], [2, 2], [3]], await fixture.Store.LoadAsync(page, CancellationToken.None));
        Assert.Equal(3, await fixture.Store.CountAsync(page, CancellationToken.None));
        Assert.Equal([[99]], await fixture.Store.LoadAsync(other, CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_page_loads_empty()
    {
        Assert.Empty(await fixture.Store.LoadAsync(PostgresFixture.NewPageId(), CancellationToken.None));
    }

    [Fact]
    public async Task Schema_setup_is_idempotent()
    {
        await fixture.Store.EnsureSchemaAsync(CancellationToken.None);
        await fixture.Store.EnsureSchemaAsync(CancellationToken.None);
    }
}
