using System.Text.Json;
using Optimisarr.Api.Endpoints;
using Optimisarr.Api.Replacement;

namespace Optimisarr.Tests;

public sealed class ApiErrorContractTests
{
    [Fact]
    public void Error_contract_keeps_machine_code_and_english_fallback()
    {
        var payload = new ApiError(
            "settings.maxConcurrentJobs.minimum",
            "Max concurrent jobs must be at least 1.");

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var document = JsonDocument.Parse(json);
        Assert.Equal("settings.maxConcurrentJobs.minimum", document.RootElement.GetProperty("code").GetString());
        Assert.Equal("Max concurrent jobs must be at least 1.", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Error_contract_can_carry_structured_arguments_and_details()
    {
        var payload = new ApiError("example", "Fallback", new { id = 42 }, new[] { "detail" });

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(42, document.RootElement.GetProperty("args").GetProperty("id").GetInt32());
        Assert.Equal("detail", document.RootElement.GetProperty("details")[0].GetString());
    }

    [Theory]
    [InlineData(ReplacementResultKind.NotFound, "replacement.action.notFound")]
    [InlineData(ReplacementResultKind.Invalid, "replacement.action.invalid")]
    [InlineData(ReplacementResultKind.Failed, "replacement.action.failed")]
    public void Replacement_errors_use_stable_machine_codes(ReplacementResultKind kind, string expected)
    {
        Assert.Equal(expected, ReplacementResults.ErrorCode(kind));
    }
}
