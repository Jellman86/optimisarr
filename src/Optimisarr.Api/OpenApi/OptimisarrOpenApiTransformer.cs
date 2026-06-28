using System.Reflection;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Optimisarr.Api.OpenApi;

/// <summary>
/// Enriches the generated OpenAPI document so it is useful to client generators and API browsers:
/// a titled/described/versioned info block, area tags so operations group sensibly, and a documented
/// <c>401</c> on every admin-token-protected operation.
/// </summary>
internal sealed class OptimisarrOpenApiTransformer : IOpenApiDocumentTransformer
{
    private const string AuthDescription = "Admin token required when OPTIMISARR_ADMIN_TOKEN is set on the server.";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "Optimisarr API";
        document.Info.Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "dev";
        document.Info.Description =
            "HTTP API for Optimisarr — safe, verified FFmpeg transcoding for self-hosted media libraries. "
            + "This contract is still pre-1.0 and may change between releases.";

        document.Tags = new HashSet<OpenApiTag>(
            OptimisarrApiTags.AllTags.Select(name => new OpenApiTag { Name = name }));

        foreach (var (path, item) in document.Paths)
        {
            if (item.Operations is null)
            {
                continue;
            }

            var tag = OptimisarrApiTags.TagFor(path);
            var requiresToken = OptimisarrApiTags.RequiresAdminToken(path);

            foreach (var operation in item.Operations.Values)
            {
                operation.Tags = new HashSet<OpenApiTagReference> { new(tag, document) };

                operation.Responses ??= new OpenApiResponses();
                if (requiresToken && !operation.Responses.ContainsKey("401"))
                {
                    operation.Responses["401"] = new OpenApiResponse { Description = AuthDescription };
                }
            }
        }

        return Task.CompletedTask;
    }
}
