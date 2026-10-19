using System.Text.Json;
using Optimisarr.Api.Diagnostics;

namespace Optimisarr.Tests;

public sealed class DiagnosticDetailSanitizerTests
{
    [Fact]
    public void Policy_snapshots_retain_typed_image_perceptual_fields_at_export()
    {
        const string json = """
            {"policy":{"imagePerceptualReportingEnabled":true,"imagePerceptualGateEnabled":true,"minimumImagePerceptualScore":88.5,"imageQualityGateEnabled":true,"unknownField":true,"path":"/data/secret"}}
            """;

        var result = DiagnosticDetailSanitizer.Read(json);

        Assert.NotNull(result);
        var policy = result.Value.GetProperty("policy");
        Assert.True(policy.GetProperty("imagePerceptualReportingEnabled").GetBoolean());
        Assert.True(policy.GetProperty("imagePerceptualGateEnabled").GetBoolean());
        Assert.Equal(88.5, policy.GetProperty("minimumImagePerceptualScore").GetDouble());
        Assert.True(policy.GetProperty("imageQualityGateEnabled").GetBoolean());
        Assert.False(policy.TryGetProperty("unknownField", out _));
        Assert.False(policy.TryGetProperty("path", out _));
    }
}
