namespace Optimisarr.Api.Replacement;

/// <summary>Maps a <see cref="ReplacementActionResult"/> to an HTTP response.</summary>
public static class ReplacementResults
{
    public static IResult ToHttp(ReplacementActionResult result) => result.Kind switch
    {
        ReplacementResultKind.Success => Results.Ok(ReplacementDto.From(result.Replacement!)),
        ReplacementResultKind.NotFound => Results.NotFound(new { error = result.Message }),
        ReplacementResultKind.Invalid => Results.BadRequest(new { error = result.Message }),
        // A failed operation that left the original safe — surface it as a server-side
        // problem so the UI shows it clearly rather than as a client mistake.
        _ => Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status500InternalServerError)
    };
}
