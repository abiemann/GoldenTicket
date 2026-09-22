using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace GoldenTicket.CompanionHost;

public sealed partial class CompanionServer
{
    private void MapBoardImages(WebApplication app)
    {
        app.MapGet("/api/board-image/{id}", async (HttpContext context, string id) =>
        {
            var credentials = StreamCredentials(context);
            if (credentials is null) return Results.Unauthorized();
            if (!Guid.TryParseExact(id, "N", out _)) return Results.NotFound();
            var image = await bridge.ReadBoardImageAsync(id, context.RequestAborted);
            // Recheck authorization after an asynchronous read in case control changed.
            if (StreamCredentials(context) != credentials) return Results.Unauthorized();
            if (image is null || image.Id != id || !CompanionBoardImage.IsValid(image)) return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(image.Jpeg, "image/jpeg", enableRangeProcessing: false);
        });
    }
}
