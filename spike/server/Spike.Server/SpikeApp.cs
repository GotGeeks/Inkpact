using Npgsql;

namespace Spike.Server;

public static class SpikeApp
{
    public static async Task<WebApplication> BuildAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var connectionString = builder.Configuration.GetConnectionString("Spike")
            ?? throw new InvalidOperationException("ConnectionStrings:Spike is not set");

        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        builder.Services.AddSingleton<IUpdateStore, PostgresUpdateStore>();
        builder.Services.AddSingleton<SyncHub>();

        var app = builder.Build();
        await app.Services.GetRequiredService<IUpdateStore>().EnsureSchemaAsync(CancellationToken.None);

        app.UseWebSockets();

        // Spike only: no auth, no Origin check, no permission check on subscribe (all in 1.09).
        app.Map("/ws", async (HttpContext context, SyncHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await hub.RunAsync(socket, context.RequestAborted);
        });

        if (app.Environment.IsDevelopment())
        {
            // Lets the check script verify that no update is stored twice.
            app.MapGet("/debug/pages/{pageId}/updates", async (string pageId, IUpdateStore updates, CancellationToken ct) =>
                Results.Ok(new { count = await updates.CountAsync(pageId, ct) }));
        }

        return app;
    }
}
