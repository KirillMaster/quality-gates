using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace KirillMaster.QualityGates.Results.AspNetCore;

public static class ResultHttpExtensions
{
    public static IResult ToHttpResult(this Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? TypedResults.NoContent() : Problem(result.Error);
    }

    public static IResult ToHttpResult<T>(this Result<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : Problem(result.Error);
    }

#pragma warning disable CS8524 // unnamed enum values are not reachable; CS8509 still fails the build when a new ErrorType is not mapped
    public static int StatusCode(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.Unexpected => StatusCodes.Status500InternalServerError,
    };
#pragma warning restore CS8524

    private static ProblemHttpResult Problem(Error error)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCode(error.Type),
            Title = error.Message,
            Extensions = { ["code"] = error.Code },
        };
        if (error.Details is { Count: > 0 })
        {
            problem.Extensions["errors"] = error.Details.Select(d => new { d.Code, d.Message }).ToArray();
        }

        return TypedResults.Problem(problem);
    }
}
