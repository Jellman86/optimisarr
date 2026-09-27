using System.Text.Json.Serialization;

namespace Optimisarr.Sidecar.Linux;

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
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'";
            await next(context);
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/api/sidecar/status", () => dashboard.Snapshot(ScratchStorage.Read(dashboard.ScratchPath)));
        return app;
    }
}
