namespace Optimisarr.Api.Endpoints;

/// <summary>
/// A machine-readable API error. <paramref name="Error"/> remains an English compatibility
/// fallback for older clients; the web client translates <paramref name="Code"/>.
/// </summary>
internal sealed record ApiError(string Code, string Error, object? Args = null, object? Details = null);

internal static class ApiErrors
{
    public static IResult BadRequest(string code, string fallback, object? args = null, object? details = null) =>
        Results.BadRequest(new ApiError(code, fallback, args, details));

    public static IResult NotFound(string code, string fallback, object? args = null) =>
        Results.NotFound(new ApiError(code, fallback, args));

    public static IResult Conflict(string code, string fallback, object? args = null) =>
        Results.Conflict(new ApiError(code, fallback, args));

    public static IResult Upstream(string code, string fallback) =>
        Results.Json(new ApiError(code, fallback), statusCode: StatusCodes.Status502BadGateway);
}
