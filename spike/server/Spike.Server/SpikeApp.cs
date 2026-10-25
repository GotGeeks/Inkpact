using Npgsql;
using Spike.Crdt;

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

        // Off unless configured: the sidecar needs Node, and the sync hub does not need compaction to run.
        if (builder.Configuration.GetValue<bool>("Compaction:Enabled"))
        {
            var sidecarScript = Path.GetFullPath(
                builder.Configuration["Sidecar:Script"] ?? throw new InvalidOperationException("Sidecar:Script is not set"),
                builder.Environment.ContentRootPath);
            builder.Services.AddSingleton<ICrdtCodec>(_ => new YjsSidecarCodec(sidecarScript));
            builder.Services.AddSingleton<Compactor>();
            builder.Services.AddHostedService<CompactionWorker>();
        }

        var app = builder.Build();
        await app.Services.GetRequiredService<IUpdateStore>().EnsureSchemaAsync(CancellationToken.None);

        // The server also has to find out about a client that vanished without a close (a phone leaving the
        // Wi-Fi), or its presence stays on everyone's screen. The default interval is two minutes.
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15),
            KeepAliveTimeout = TimeSpan.FromSeconds(15),
        });

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
