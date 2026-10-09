using System.Text.Json.Serialization;

namespace Optimisarr.Sidecar.Linux;

public sealed record PairRequest(string? Server, string? Code);

public static class DashboardHost
{
    public static WebApplication Create(WorkerDashboard dashboard, string? urls = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseUrls(urls ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://127.0.0.1:8788");
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'none'";
            await next(context);
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapGet("/api/sidecar/diagnostics", () => Results.File(dashboard.Diagnostics?.Export() ?? "{\"schemaVersion\":1,\"entries\":[]}"u8.ToArray(), "application/json", "optimisarr-sidecar-diagnostics.json"));
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/api/sidecar/status", () =>
        {
            dashboard.Viewed();
            return dashboard.Snapshot(ScratchStorage.Read(dashboard.ScratchPath));
        });
        app.MapGet("/api/sidecar/jobs/{id:int}/preview", (int id) =>
            dashboard.ReadPreview(id) is { } jpeg ? Results.File(jpeg, "image/jpeg") : Results.NotFound());
        app.MapGet("/api/sidecar/jobs/{id:int}/artwork", (int id) =>
            dashboard.ReadArtwork(id) is { } artwork ? Results.File(artwork.Bytes, artwork.ContentType) : Results.NotFound());
        // The only write, and only while the worker has no credential. A JSON body already keeps a
        // plain cross-site form out (415); the fetch-metadata check refuses scripted ones too.
        app.MapPost("/api/sidecar/pair", async (PairRequest request, HttpContext context) =>
        {
            if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
                return Results.Json(new { error = "Pair this worker from its own page." }, statusCode: StatusCodes.Status403Forbidden);
            var attempt = await dashboard.Pairing.SubmitAsync(request.Server, request.Code);
            return attempt.Problem is null
                ? Results.Ok(new { paired = true })
                : Results.Json(new { error = attempt.Problem }, statusCode: attempt.StatusCode);
        });
        return app;
    }
}
