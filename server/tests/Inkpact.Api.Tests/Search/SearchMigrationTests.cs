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
    public async Task The_file_text_migration_keeps_what_is_found_and_then_finds_the_text_of_a_file()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var db = new InkpactDbContext(new DbContextOptionsBuilder<InkpactDbContext>().UseNpgsql(connectionString).Options);
        var searchMigration = db.Database.GetMigrations().Single(m => m.EndsWith("_AddSearch", StringComparison.Ordinal));
        await db.GetService<IMigrator>().MigrateAsync(searchMigration);
        var (userId, workspaceId, page) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO users (id, email, normalized_email, display_name, created_at, is_admin)
            VALUES ({userId}, 'old@example.com', 'old@example.com', 'Old', now(), true)
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO workspaces (id, name, owner_id, created_at) VALUES ({workspaceId}, 'Old workspace', {userId}, now())
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO pages (id, workspace_id, title, sort_key, created_at, updated_at, is_todo, version, content_text)
            VALUES ({page}, {workspaceId}, 'Café', 'a0', now(), now(), false, 1, 'Een bezoek aan het museum.')
            """);

        await db.Database.MigrateAsync();

        var found = async (string query) => await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pages WHERE id = {page} AND search_vector @@ to_tsquery('inkpact_search', {query})")
            .SingleAsync() == 1;
        (await found("cafe:*")).ShouldBeTrue();
        (await found("museum:*")).ShouldBeTrue();
        (await found("zeppelin:*")).ShouldBeFalse();

        await db.Database.ExecuteSqlAsync($"UPDATE pages SET file_text = 'the zeppelin hangar' WHERE id = {page}");

        (await found("zeppelin:*")).ShouldBeTrue();
        (await found("museum:*")).ShouldBeTrue();
        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE indexname = 'ix_pages_search_vector'").SingleAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task The_file_text_migration_marks_the_notes_kept_as_files_for_the_next_import_and_no_other()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var db = new InkpactDbContext(new DbContextOptionsBuilder<InkpactDbContext>().UseNpgsql(connectionString).Options);
        var searchMigration = db.Database.GetMigrations().Single(m => m.EndsWith("_AddSearch", StringComparison.Ordinal));
        await db.GetService<IMigrator>().MigrateAsync(searchMigration);
        var (userId, workspaceId) = (Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO users (id, email, normalized_email, display_name, created_at, is_admin)
            VALUES ({userId}, 'old@example.com', 'old@example.com', 'Old', now(), true)
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO workspaces (id, name, owner_id, created_at) VALUES ({workspaceId}, 'Old workspace', {userId}, now())
            """);

        // A note kept as a file: blank text, its body file. A note that is content. A note with blank text that only has a file of
        // the export (with its external id). A page that is no note of an import, with a body-like file.
        var (asFile, asContent, withResource, notImported) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var pages = new[] { (asFile, "\n"), (asContent, "Some text"), (withResource, ""), (notImported, "") };
        for (var i = 0; i < pages.Length; i++)
        {
            var (id, text) = pages[i];
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO pages (id, workspace_id, title, sort_key, created_at, updated_at, is_todo, version, content_text)
                VALUES ({id}, {workspaceId}, 'Note', {"a" + i}, now(), now(), false, 1, {text})
                """);
        }

        var files = new (Guid Page, string Name, string? ExternalId)[]
        {
            (asFile, "Big.md", null), (asContent, "Old body.md", null), (withResource, "readme.md", "resource-1"), (notImported, "Notes.html", null),
        };
        foreach (var (pageId, name, externalId) in files)
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO attachments (id, page_id, file_name, content_type, size, sha256, created_at, external_source_type, external_id)
                VALUES ({Guid.NewGuid()}, {pageId}, {name}, 'text/markdown', 10, {new string('a', 64)}, now(), 'joplin', {externalId})
                """);
        }

        var importId = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO imports (id, workspace_id, user_id, source_type, file_name, file_size, state, total_items, processed_items, created_at, counts, report)
            VALUES ({importId}, {workspaceId}, {userId}, 'joplin', 'export.jex', 1, 'completed', 0, 0, now(), CAST({"{}"} AS jsonb), CAST({"[]"} AS jsonb))
            """);
        foreach (var (pageId, i) in new[] { asFile, asContent, withResource }.Select((p, i) => (p, i)))
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO import_items (id, workspace_id, source_type, external_id, kind, inkpact_id, content_hash, import_id, imported_at, converter_version)
                VALUES ({Guid.NewGuid()}, {workspaceId}, 'joplin', {"note-" + i}, 'note', {pageId}, 'hash', {importId}, now(), 2)
                """);
        }

        await db.Database.MigrateAsync();

        var versionOf = async (Guid pageId) => await db.Database
            .SqlQuery<int>($"SELECT converter_version AS \"Value\" FROM import_items WHERE inkpact_id = {pageId}")
            .SingleAsync();
        (await versionOf(asFile)).ShouldBe(1);
        (await versionOf(asContent)).ShouldBe(2);
        (await versionOf(withResource)).ShouldBe(2);
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
