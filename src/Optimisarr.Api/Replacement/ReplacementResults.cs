using Optimisarr.Api.Endpoints;

namespace Optimisarr.Api.Replacement;

/// <summary>Maps a <see cref="ReplacementActionResult"/> to an HTTP response.</summary>
public static class ReplacementResults
{
    public static IResult ToHttp(ReplacementActionResult result) => result.Kind switch
    {
        ReplacementResultKind.Success => Results.Ok(ReplacementDto.From(result.Replacement!)),
        ReplacementResultKind.NotFound =>
            ApiErrors.NotFound(ErrorCode(result.Kind), result.Message ?? "Replacement not found."),
        ReplacementResultKind.Invalid =>
            ApiErrors.BadRequest(ErrorCode(result.Kind), result.Message ?? "Replacement action is not valid."),
        // A failed operation that left the original safe — surface it as a server-side
        // problem so the UI shows it clearly rather than as a client mistake.
        _ => Results.Json(
            new ApiError(ErrorCode(result.Kind), result.Message ?? "Replacement action failed."),
            statusCode: StatusCodes.Status500InternalServerError)
    };

    internal static string ErrorCode(ReplacementResultKind kind) => kind switch
    {
        ReplacementResultKind.NotFound => "replacement.action.notFound",
        ReplacementResultKind.Invalid => "replacement.action.invalid",
        _ => "replacement.action.failed"
    };
}
