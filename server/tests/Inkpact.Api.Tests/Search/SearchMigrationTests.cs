using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Inkpact.Api.Tests.Search;

public class SearchMigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task The_migration_makes_a_vector_of_the_pages_that_are_already_there_even_when_one_has_a_huge_text()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var db = new InkpactDbContext(new DbContextOptionsBuilder<InkpactDbContext>().UseNpgsql(connectionString).Options);
        var before = db.Database.GetMigrations().Single(m => m.EndsWith("_AddImportPageSeq", StringComparison.Ordinal));
        await db.GetService<IMigrator>().MigrateAsync(before);

        var (userId, workspaceId, plain, huge, tag) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        // A million characters of words that are all different: more than a vector can hold when all of it is indexed.
        var hugeText = string.Join(' ', Enumerable.Range(0, 160_000).Select(i => $"w{i:x}"));
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO users (id, email, normalized_email, display_name, created_at, is_admin)
            VALUES ({userId}, 'old@example.com', 'old@example.com', 'Old', now(), true)
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO workspaces (id, name, owner_id, created_at) VALUES ({workspaceId}, 'Old workspace', {userId}, now())
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO pages (id, workspace_id, title, sort_key, created_at, updated_at, is_todo, version, content_text)
            VALUES ({plain}, {workspaceId}, 'Café', 'a0', now(), now(), false, 1, 'Een bezoek aan het museum.'),
                   ({huge}, {workspaceId}, 'Huge', 'a1', now(), now(), false, 1, {hugeText})
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO tags (id, workspace_id, name, normalized_name, created_at) VALUES ({tag}, {workspaceId}, 'Reizen', 'reizen', now())
            """);
        await db.Database.ExecuteSqlAsync($"INSERT INTO page_tags (page_id, tag_id) VALUES ({plain}, {tag})");

        await db.Database.MigrateAsync();

        var found = async (string query) => await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM pages WHERE search_vector @@ to_tsquery('inkpact_search', {query}) ORDER BY id")
            .ToListAsync();
        (await found("cafe:*")).ShouldBe([plain]);
        (await found("museum:*")).ShouldBe([plain]);
        (await found("reiz:*")).ShouldBe([plain]);
        (await found("w5:*")).ShouldBe([huge]);
        (await db.Database.SqlQuery<string>($"SELECT tag_text AS \"Value\" FROM pages WHERE id = {plain}").SingleAsync()).ShouldBe("Reizen");
        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_extension WHERE extname = 'unaccent'").SingleAsync()).ShouldBe(1);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_ts_config WHERE cfgname = 'inkpact_search'").SingleAsync()).ShouldBe(1);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE indexname IN ('ix_pages_search_vector', 'ix_pages_title_vector')").SingleAsync()).ShouldBe(2);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_trigger WHERE tgname IN ('page_tags_search', 'tags_search')").SingleAsync()).ShouldBe(2);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM live_pages({workspaceId})").SingleAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task The_migration_can_be_undone()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var db = new InkpactDbContext(new DbContextOptionsBuilder<InkpactDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync();
        var before = db.Database.GetMigrations().Single(m => m.EndsWith("_AddImportPageSeq", StringComparison.Ordinal));

        await db.GetService<IMigrator>().MigrateAsync(before);

        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'pages' AND column_name IN ('search_vector', 'title_vector', 'tag_text')").SingleAsync()).ShouldBe(0);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_ts_config WHERE cfgname = 'inkpact_search'").SingleAsync()).ShouldBe(0);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_trigger WHERE tgname IN ('page_tags_search', 'tags_search')").SingleAsync()).ShouldBe(0);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_proc WHERE proname IN ('live_pages', 'refresh_tag_text', 'page_tags_changed', 'tag_renamed')").SingleAsync()).ShouldBe(0);
    }
}
