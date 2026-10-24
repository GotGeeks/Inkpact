namespace Spike.Server;

public static class SpikeApp
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<RelayHub>();

        var app = builder.Build();
        app.UseWebSockets();

        // Spike only: no auth, no Origin check. The page id comes from the query string;
        // run 0.2 moves it into every message so one socket can carry many pages.
        app.Map("/ws", async (HttpContext context, RelayHub hub) =>
        {
            var pageId = context.Request.Query["page"].ToString();
            if (!context.WebSockets.IsWebSocketRequest || pageId.Length is 0 or > 128)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await hub.RunAsync(pageId, socket, context.RequestAborted);
        });

        return app;
    }
}
